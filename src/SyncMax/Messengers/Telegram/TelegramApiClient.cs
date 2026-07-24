using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;
using SyncMax.Models;
using SyncMax.Services;
using Telegram.Bot;
using Telegram.Bot.Types;
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
    private readonly ILogger<TelegramApiClient> _logger;
    private readonly Lazy<ITelegramBotClient?> _bot;

    public TelegramApiClient(IOptions<TelegramOptions> options, ILogger<TelegramApiClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _bot = new Lazy<ITelegramBotClient?>(
            () => IsConfigured ? new TelegramBotClient(_options.Token) : null);
    }

    public MessengerType Messenger => MessengerType.Telegram;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Token);

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
    /// Отправка сообщения (текст и/или медиа) в чат/группу. В Telegram id пользователя и
    /// чата — одно адресное пространство (chat_id). Форматирование — через entities (без
    /// parse_mode и экранирования). Каждое вложение шлётся своим родным методом; подпись
    /// (с «шапкой») ставится только на первое вложение. Если родная отправка не удалась —
    /// откат на отправку документом.
    /// </summary>
    public async Task SendChatMessageAsync(string chatId, RelayMessage message, CancellationToken ct)
    {
        if (BotClient is not { } bot)
        {
            _logger.LogWarning("Telegram: клиент не инициализирован, сообщение не отправлено.");
            return;
        }

        var id = long.Parse(chatId);

        if (!message.HasMedia)
        {
            await bot.SendMessage(id, message.Caption.Text,
                entities: TelegramFormatting.ToEntities(message.Caption), cancellationToken: ct);
            return;
        }

        var captionConsumed = false;
        foreach (var att in message.Attachments)
        {
            var caption = captionConsumed ? null : message.Caption;

            // video_note не поддерживает подпись — «шапку» шлём отдельным сообщением до кружка.
            if (att.Kind == MediaKind.VideoNote)
            {
                if (caption is { Text.Length: > 0 })
                    await bot.SendMessage(id, caption.Text, entities: TelegramFormatting.ToEntities(caption), cancellationToken: ct);
                captionConsumed = true;
                await SendMediaAsync(bot, id, att, null, ct);
                continue;
            }

            await SendMediaAsync(bot, id, att, caption, ct);
            if (caption is not null)
                captionConsumed = true;
        }
    }

    /// <summary>Отправляет одно вложение родным методом Telegram; при ошибке — откат на документ.</summary>
    private async Task SendMediaAsync(ITelegramBotClient bot, long id, MediaAttachment att, FormattedText? caption, CancellationToken ct)
    {
        var text = caption?.Text is { Length: > 0 } t ? t : null;
        var entities = caption is null ? null : TelegramFormatting.ToEntities(caption);
        var name = att.FileName ?? Path.GetFileName(att.FilePath);

        try
        {
            await using var fs = File.OpenRead(att.FilePath);
            var input = InputFile.FromStream(fs, name);
            switch (att.Kind)
            {
                case MediaKind.Photo:
                    await bot.SendPhoto(id, input, caption: text, captionEntities: entities, cancellationToken: ct);
                    break;
                case MediaKind.Video:
                    await bot.SendVideo(id, input, caption: text, captionEntities: entities, cancellationToken: ct);
                    break;
                case MediaKind.Animation:
                    await bot.SendAnimation(id, input, caption: text, captionEntities: entities, cancellationToken: ct);
                    break;
                case MediaKind.Voice:
                    await bot.SendVoice(id, input, caption: text, captionEntities: entities, cancellationToken: ct);
                    break;
                case MediaKind.Audio:
                    await bot.SendAudio(id, input, caption: text, captionEntities: entities, cancellationToken: ct);
                    break;
                case MediaKind.VideoNote:
                    await bot.SendVideoNote(id, input, cancellationToken: ct);
                    break;
                default:
                    await bot.SendDocument(id, input, caption: text, captionEntities: entities, cancellationToken: ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram: не удалось отправить {Kind} штатно, откат на документ.", att.Kind);
            await using var fs = File.OpenRead(att.FilePath);
            var input = InputFile.FromStream(fs, name);
            await bot.SendDocument(id, input, caption: text, captionEntities: entities, cancellationToken: ct);
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
