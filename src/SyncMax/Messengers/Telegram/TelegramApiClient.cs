using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;
using SyncMax.Models;
using SyncMax.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using TelegramUser = Telegram.Bot.Types.User;

namespace SyncMax.Messengers.Telegram;

/// <summary>
/// Тонкий клиент над Telegram Bot API. Владеет экземпляром <see cref="ITelegramBotClient"/>
/// и отдаёт его наружу через <see cref="BotClient"/> — он нужен <see cref="TelegramBotService"/>
/// для long polling входящих обновлений. Реализует <see cref="IMessengerApiClient"/> —
/// общий контракт отправки для LinkingService.
/// </summary>
public sealed class TelegramApiClient : IMessengerApiClient
{
    private readonly TelegramOptions _options;
    private readonly MiniAppOptions _miniApp;
    private readonly ILogger<TelegramApiClient> _logger;
    private readonly Lazy<ITelegramBotClient?> _bot;

    public TelegramApiClient(
        IOptions<TelegramOptions> options,
        IOptions<MiniAppOptions> miniApp,
        ILogger<TelegramApiClient> logger)
    {
        _options = options.Value;
        _miniApp = miniApp.Value;
        _logger = logger;
        _bot = new Lazy<ITelegramBotClient?>(() => IsConfigured ? CreateBot() : null);
    }

    public MessengerType Messenger => MessengerType.Telegram;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Token);

    /// <summary>
    /// Создаёт клиент Telegram.Bot. Если задан <see cref="TelegramOptions.ApiBaseUrl"/> —
    /// используется он (собственный Bot API сервер), иначе официальный api.telegram.org.
    /// </summary>
    private TelegramBotClient CreateBot() =>
        string.IsNullOrWhiteSpace(_options.ApiBaseUrl)
            ? new TelegramBotClient(_options.Token)
            : new TelegramBotClient(new TelegramBotClientOptions(_options.Token, _options.ApiBaseUrl));

    /// <summary>Низкоуровневый клиент библиотеки Telegram.Bot — для long polling в TelegramBotService.</summary>
    public ITelegramBotClient? BotClient => _bot.Value;

    public async Task SendTextAsync(string userId, string text, CancellationToken ct)
    {
        if (BotClient is not { } bot)
        {
            _logger.LogWarning("Telegram: клиент не инициализирован, сообщение не отправлено.");
            return;
        }

        await bot.SendMessage(long.Parse(userId), text, cancellationToken: ct);
    }

    /// <summary>
    /// Сообщение с inline-кнопкой, открывающей мини-приложение. Telegram требует для
    /// web_app-кнопки https-адрес, поэтому при пустом или не-https <c>MiniApp:Url</c>
    /// кнопка не добавляется — уходит просто текст.
    /// </summary>
    public async Task SendMiniAppButtonAsync(string userId, string text, string buttonText, CancellationToken ct)
    {
        if (BotClient is not { } bot)
        {
            _logger.LogWarning("Telegram: клиент не инициализирован, сообщение не отправлено.");
            return;
        }

        var chatId = long.Parse(userId);
        var url = _miniApp.Url;

        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            await bot.SendMessage(chatId, text, cancellationToken: ct);
            return;
        }

        var markup = new InlineKeyboardMarkup(InlineKeyboardButton.WithWebApp(buttonText, new WebAppInfo { Url = url }));
        await bot.SendMessage(chatId, text, replyMarkup: markup, cancellationToken: ct);
    }

    /// <summary>
    /// Выставляет постоянную кнопку мини-приложения рядом с полем ввода (у всех личных
    /// чатов сразу). Вызывается один раз при старте бота: настройка живёт на стороне
    /// Telegram, а не в каждом сообщении.
    /// </summary>
    public async Task ConfigureMenuButtonAsync(CancellationToken ct)
    {
        if (BotClient is not { } bot)
            return;

        var url = _miniApp.Url;
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            await bot.SetChatMenuButton(
                menuButton: new MenuButtonWebApp { Text = "Открыть", WebApp = new WebAppInfo { Url = url } },
                cancellationToken: ct);
            _logger.LogInformation("Telegram: кнопка меню настроена на мини-приложение {Url}.", url);
        }
        catch (Exception ex)
        {
            // Не критично: приложение можно открыть и inline-кнопкой из сообщения.
            _logger.LogWarning(ex, "Telegram: не удалось выставить кнопку меню мини-приложения.");
        }
    }

    /// <summary>
    /// Отправка сообщения (текст и/или медиа) в чат/группу. В Telegram id пользователя и
    /// чата — одно адресное пространство (chat_id). Форматирование — через entities (без
    /// parse_mode и экранирования). Каждое вложение шлётся своим родным методом; подпись
    /// (с «шапкой») ставится только на первое вложение. Если родная отправка не удалась —
    /// откат на отправку документом.
    /// </summary>
    public async Task<string?> SendChatMessageAsync(string chatId, RelayMessage message, CancellationToken ct)
    {
        if (BotClient is not { } bot)
        {
            _logger.LogWarning("Telegram: клиент не инициализирован, сообщение не отправлено.");
            return null;
        }

        var id = long.Parse(chatId);
        var reply = ParseReply(message.ReplyToTargetMessageId);

        if (!message.HasMedia)
        {
            var sent = await bot.SendMessage(id, message.Caption.Text,
                entities: TelegramFormatting.ToEntities(message.Caption), replyParameters: reply,
                linkPreviewOptions: LinkPreview(message.DisableLinkPreview), cancellationToken: ct);
            return sent.MessageId.ToString();
        }

        string? firstId = null;
        var first = true;
        foreach (var att in message.Attachments)
        {
            var caption = first ? message.Caption : null;
            var attReply = first ? reply : null;

            // video_note не поддерживает подпись — «шапку» шлём отдельным сообщением до кружка
            // (ответ вешаем на «шапку», если она есть; иначе — на сам кружок).
            if (att.Kind == MediaKind.VideoNote)
            {
                if (caption is { Text.Length: > 0 })
                {
                    var header = await bot.SendMessage(id, caption.Text,
                        entities: TelegramFormatting.ToEntities(caption), replyParameters: attReply, cancellationToken: ct);
                    firstId ??= header.MessageId.ToString();
                    attReply = null;
                }
                var noteId = await SendMediaAsync(bot, id, att, null, attReply, ct);
                firstId ??= noteId;
            }
            else
            {
                var sentId = await SendMediaAsync(bot, id, att, caption, attReply, ct);
                firstId ??= sentId;
            }
            first = false;
        }
        return firstId;
    }

    /// <summary>Отправляет одно вложение родным методом Telegram; при ошибке — откат на документ. Возвращает message_id.</summary>
    private async Task<string?> SendMediaAsync(
        ITelegramBotClient bot, long id, MediaAttachment att, FormattedText? caption, ReplyParameters? reply, CancellationToken ct)
    {
        var text = caption?.Text is { Length: > 0 } t ? t : null;
        var entities = caption is null ? null : TelegramFormatting.ToEntities(caption);
        var name = att.FileName ?? Path.GetFileName(att.FilePath);

        try
        {
            await using var fs = File.OpenRead(att.FilePath);
            var input = InputFile.FromStream(fs, name);
            var sent = att.Kind switch
            {
                MediaKind.Photo => await bot.SendPhoto(id, input, caption: text, captionEntities: entities, replyParameters: reply, cancellationToken: ct),
                MediaKind.Video => await bot.SendVideo(id, input, caption: text, captionEntities: entities, replyParameters: reply, cancellationToken: ct),
                MediaKind.Animation => await bot.SendAnimation(id, input, caption: text, captionEntities: entities, replyParameters: reply, cancellationToken: ct),
                MediaKind.Voice => await bot.SendVoice(id, input, caption: text, captionEntities: entities, replyParameters: reply, cancellationToken: ct),
                MediaKind.Audio => await bot.SendAudio(id, input, caption: text, captionEntities: entities, replyParameters: reply, cancellationToken: ct),
                MediaKind.VideoNote => await bot.SendVideoNote(id, input, replyParameters: reply, cancellationToken: ct),
                _ => await bot.SendDocument(id, input, caption: text, captionEntities: entities, replyParameters: reply, cancellationToken: ct)
            };
            return sent.MessageId.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram: не удалось отправить {Kind} штатно, откат на документ.", att.Kind);
            await using var fs = File.OpenRead(att.FilePath);
            var input = InputFile.FromStream(fs, name);
            var doc = await bot.SendDocument(id, input, caption: text, captionEntities: entities, replyParameters: reply, cancellationToken: ct);
            return doc.MessageId.ToString();
        }
    }

    /// <summary>
    /// Настройка превью ссылок: null — поведение по умолчанию (Telegram разворачивает превью
    /// первой ссылки). Передаётся и при отправке, и при правке: правка перестраивает превью
    /// заново, и без этого параметра оно вернулось бы обратно.
    /// </summary>
    private static LinkPreviewOptions? LinkPreview(bool disabled) =>
        disabled ? new LinkPreviewOptions { IsDisabled = true } : null;

    private static ReplyParameters? ParseReply(string? targetMessageId) =>
        int.TryParse(targetMessageId, out var mid) ? new ReplyParameters { MessageId = mid } : null;

    /// <summary>
    /// Редактирует текст (для текстового сообщения) или подпись (для медиа) — через entities,
    /// без parse_mode. Медиа при правке подписи сохраняется.
    /// </summary>
    public async Task EditChatMessageAsync(
        string chatId, string messageId, FormattedText caption, bool isMediaCaption, bool disableLinkPreview,
        CancellationToken ct)
    {
        if (BotClient is not { } bot || !int.TryParse(messageId, out var mid))
            return;

        var id = long.Parse(chatId);
        var entities = TelegramFormatting.ToEntities(caption);
        try
        {
            if (isMediaCaption)
                await bot.EditMessageCaption(id, mid, caption: caption.Text, captionEntities: entities, cancellationToken: ct);
            else
                await bot.EditMessageText(id, mid, caption.Text, entities: entities,
                    linkPreviewOptions: LinkPreview(disableLinkPreview), cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram: не удалось отредактировать сообщение {Mid}.", mid);
        }
    }

    /// <summary>
    /// Заменяет содержимое сообщения на медиа (editMessageMedia). Bot API умеет добавлять
    /// медиа и к чисто текстовому сообщению — именно на этом держится статусное сообщение
    /// «видео из ссылок»: текст «скачивается…» превращается в само видео, оставаясь на своём
    /// месте в ленте.
    /// </summary>
    public async Task<bool> TryReplaceChatMessageMediaAsync(
        string chatId, string messageId, MediaAttachment media, FormattedText caption, CancellationToken ct)
    {
        if (BotClient is not { } bot || !int.TryParse(messageId, out var mid))
            return false;

        var text = caption.Text is { Length: > 0 } t ? t : null;
        var entities = TelegramFormatting.ToEntities(caption);
        var name = media.FileName ?? Path.GetFileName(media.FilePath);

        try
        {
            await using var fs = File.OpenRead(media.FilePath);
            var input = InputFile.FromStream(fs, name);
            InputMedia content = media.Kind switch
            {
                MediaKind.Photo => new InputMediaPhoto(input) { Caption = text, CaptionEntities = entities },
                MediaKind.Video => new InputMediaVideo(input) { Caption = text, CaptionEntities = entities },
                MediaKind.Animation => new InputMediaAnimation(input) { Caption = text, CaptionEntities = entities },
                MediaKind.Audio => new InputMediaAudio(input) { Caption = text, CaptionEntities = entities },
                _ => new InputMediaDocument(input) { Caption = text, CaptionEntities = entities }
            };

            await bot.EditMessageMedia(long.Parse(chatId), mid, content, cancellationToken: ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram: не удалось заменить содержимое сообщения {Mid} на {Kind}.", mid, media.Kind);
            return false;
        }
    }

    public async Task DeleteChatMessageAsync(string chatId, string messageId, CancellationToken ct)
    {
        if (BotClient is not { } bot || !int.TryParse(messageId, out var mid))
            return;

        try
        {
            await bot.DeleteMessage(long.Parse(chatId), mid, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram: не удалось удалить сообщение {Mid}.", mid);
        }
    }

    /// <summary>
    /// Скачивает файл Telegram по file_id во временный файл на диске. null, если скачать не
    /// удалось (в т.ч. лимит Bot API 20 МБ на скачивание). Расширение — подсказка для имени.
    /// </summary>
    public async Task<string?> DownloadFileToTempAsync(string fileId, string? extension, CancellationToken ct)
    {
        if (BotClient is not { } bot)
            return null;

        var path = TempFiles.NewPath(extension);
        try
        {
            await using var fs = File.Create(path);
            await bot.GetInfoAndDownloadFile(fileId, fs, ct);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram: не удалось скачать файл {FileId} (возможно, превышен лимит 20 МБ).", fileId);
            TempFiles.TryDelete(path);
            return null;
        }
    }

    /// <summary>
    /// Единая точка формирования читаемого имени пользователя Telegram: "{FirstName} {LastName}",
    /// если одно из имён пустое — только второе, если оба пустых — Username.
    /// </summary>
    public static string? BuildDisplayName(TelegramUser? user)
    {
        if (user is null)
            return null;

        var hasFirstName = !string.IsNullOrWhiteSpace(user.FirstName);
        var hasLastName = !string.IsNullOrWhiteSpace(user.LastName);

        var fullName = (hasFirstName, hasLastName) switch
        {
            (true, true) => $"{user.FirstName} {user.LastName}",
            (true, false) => user.FirstName,
            (false, true) => user.LastName,
            (false, false) => null
        };

        return fullName ?? user.Username;
    }
}
