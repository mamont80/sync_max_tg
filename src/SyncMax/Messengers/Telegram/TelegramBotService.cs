using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;
using SyncMax.Data.Repositories;
using SyncMax.Models;
using SyncMax.Services;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace SyncMax.Messengers.Telegram;

/// <summary>
/// Фоновый сервис Telegram-бота: приём входящих обновлений через <see cref="TelegramApiClient"/> —
/// либо long polling, либо webhook (см. <see cref="TelegramOptions.Mode"/>) — и передача их в
/// <see cref="LinkingService"/>. Отправка ответов — забота клиента, этот класс только принимает
/// и разбирает апдейты. В режиме webhook апдейты поступают снаружи через
/// <see cref="HandleWebhookUpdateAsync"/> (вызывается из HTTP-эндпоинта в Program.cs).
/// </summary>
public sealed class TelegramBotService : BackgroundService
{
    // Отладочный дамп входящих апдейтов в человекочитаемом JSON.
    private static readonly JsonSerializerOptions DebugJson = new() { WriteIndented = true };

    private static readonly UpdateType[] AllowedUpdates =
        [UpdateType.Message, UpdateType.EditedMessage, UpdateType.MyChatMember, UpdateType.CallbackQuery, UpdateType.ChannelPost];

    private readonly TelegramApiClient _client;
    private readonly LinkingService _linking;
    private readonly ChatLinkingService _chatLinking;
    private readonly SystemCommandService _systemCommands;
    private readonly MessageRelayService _relay;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramBotService> _logger;
    private string _botId = string.Empty;

    public TelegramBotService(
        TelegramApiClient client, LinkingService linking, ChatLinkingService chatLinking,
        SystemCommandService systemCommands, MessageRelayService relay, IOptions<TelegramOptions> options,
        ILogger<TelegramBotService> logger)
    {
        _client = client;
        _linking = linking;
        _chatLinking = chatLinking;
        _systemCommands = systemCommands;
        _relay = relay;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_client.BotClient is not { } bot)
        {
            _logger.LogWarning("Telegram: токен не задан (Telegram:Token). Бот отключён.");
            return;
        }

        var me = await bot.GetMe(ct);
        _botId = me.Id.ToString();

        // Постоянная кнопка мини-приложения рядом с полем ввода. Настройка живёт на стороне
        // Telegram, поэтому выставляется один раз при старте, а не в каждом сообщении.
        await _client.ConfigureMenuButtonAsync(ct);

        if (_options.Mode == BotMode.Webhook)
        {
            if (string.IsNullOrWhiteSpace(_options.Webhook.Url))
            {
                _logger.LogWarning("Telegram: выбран режим Webhook, но Telegram:Webhook:Url не задан. Бот отключён.");
                return;
            }

            // Секрет — производная от токена бота (WebhookSecret): Telegram будет присылать
            // его заголовком X-Telegram-Bot-Api-Secret-Token, чем мы и проверим отправителя.
            await bot.SetWebhook(
                _options.Webhook.Url,
                allowedUpdates: AllowedUpdates,
                secretToken: WebhookSecret.FromToken(_options.Token),
                cancellationToken: ct);
            _logger.LogInformation("Telegram-бот @{Username} запущен в режиме webhook ({Url}).", me.Username, _options.Webhook.Url);

            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return;
        }

        // На случай переключения обратно с webhook на long polling: пока webhook
        // зарегистрирован, getUpdates отклоняется Telegram с ошибкой конфликта.
        await bot.DeleteWebhook(cancellationToken: ct);
        _logger.LogInformation("Telegram-бот @{Username} запущен в режиме long polling.", me.Username);

        var offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await bot.GetUpdates(
                    offset: offset,
                    timeout: 30,
                    allowedUpdates: AllowedUpdates,
                    cancellationToken: ct);

                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    LogIncomingUpdate(update);
                    await DoUpdate(update, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram: ошибка опроса, повтор через 3 c.");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    /// <summary>Обрабатывает апдейт, доставленный webhook-запросом (см. эндпоинт в Program.cs).</summary>
    public Task HandleWebhookUpdateAsync(Update update, CancellationToken ct)
    {
        LogIncomingUpdate(update);
        return DoUpdate(update, ct);
    }
    /// <summary>
    /// Обработка отдельного обновления
    /// </summary>
    private async Task DoUpdate(Update update, CancellationToken ct)
    {
        //Добавление бота в группу/канал
        if (update.Type == UpdateType.MyChatMember)
        {
            //if (update.MyChatMember.Chat)
            var userId = update.MyChatMember?.From?.Id.ToString();
            var newStatus = update.MyChatMember?.NewChatMember.Status;
            var chatId = update.MyChatMember?.Chat?.Id.ToString();
            if (newStatus == ChatMemberStatus.Member || newStatus == ChatMemberStatus.Administrator)
            { 
                if (!string.IsNullOrEmpty(userId) && !string.IsNullOrEmpty(chatId))
                {
                    await _chatLinking.HandleAddBotToGroup(
                        userId,
                        newStatus == ChatMemberStatus.Administrator ? MemberType.Administrator : MemberType.Member,
                        chatId,
                        MessengerType.Telegram,
                        ChatKind.Chat,
                        update.MyChatMember?.Chat.Title,
                        ct);
                }
            }
        }
        // Правка сообщения пользователем — переносим на пересланную копию.
        if (update.EditedMessage is { } edited)
        {
            await HandleEditedAsync(edited, ct);
            return;
        }

        var message = update.Message;
        if (message is null)
            return;

        // Эхо собственного сообщения бота (например, отправленного при пересылке в эту же
        // группу) — не обрабатываем повторно, иначе связка с обеих сторон зациклится.
        if (!string.IsNullOrEmpty(_botId) && message.From?.Id.ToString() == _botId)
            return;

        //Это сообщение лично боту
        if (message.Chat.Type == ChatType.Private)
        {
            if (message.Text is not { } text)
                return;
            var chatId = message.Chat.Id.ToString();
            if (await _systemCommands.TryHandleAsync(MessengerType.Telegram, chatId, text, ct))
                return;

            var name = TelegramApiClient.BuildDisplayName(message.From);
            await _linking.HandleAsync(MessengerType.Telegram, chatId, name, text, ct);
        }
        //Это сообщение в группу
        if (message.Chat.Type == ChatType.Group || message.Chat.Type == ChatType.Supergroup)
        {
            var chatId = message.Chat.Id.ToString();
            var command = message.Text?.Trim().ToLower();
            if (command == "/link" || command == "link" || command == "\\link")
            {
                var userId = message.From?.Id.ToString();
                var chatTitle = message.Chat.Title;
                if (!string.IsNullOrEmpty(userId))
                {
                    await _chatLinking.HandleRepostAsync(MessengerType.Telegram, userId, chatId, ChatKind.Chat, chatTitle, ct);
                }
                else _logger.LogWarning($"На команде /link пользователь не определён ChatTitle:{message.Chat.Title}");
                return;
            }

            // Сообщения от ботов (не только от себя самого, но и от любых других
            // ботов в группе) не пересылаем — это не пользовательский контент.
            if (message.From?.IsBot == true)
                return;

            var relay = await BuildRelayMessageAsync(message, ct);
            if (relay.IsEmpty)
                return;

            var senderName = TelegramApiClient.BuildDisplayName(message.From);
            await _relay.RelayMessageAsync(MessengerType.Telegram, chatId, senderName, relay, ct);
        }
    }

    /// <summary>
    /// Переносит правку сообщения Telegram на пересланную копию: новый текст/подпись (медиа не
    /// перезагружаем — только текст). Ботов и собственное эхо пропускаем.
    /// </summary>
    private async Task HandleEditedAsync(Message edited, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_botId) && edited.From?.Id.ToString() == _botId)
            return;
        if (edited.Chat.Type is not (ChatType.Group or ChatType.Supergroup))
            return;
        if (edited.From?.IsBot == true)
            return;

        var text = edited.Text ?? edited.Caption ?? string.Empty;
        var entities = edited.Entities ?? edited.CaptionEntities;
        var caption = TelegramFormatting.ToFormattedText(text, entities);
        var senderName = TelegramApiClient.BuildDisplayName(edited.From);

        await _relay.RelayEditAsync(
            MessengerType.Telegram, edited.Chat.Id.ToString(), senderName,
            edited.MessageId.ToString(), caption, HasMedia(edited), ct);
    }

    private static bool HasMedia(Message message) =>
        message.Photo is { Length: > 0 }
        || message.Video is not null
        || message.Animation is not null
        || message.VideoNote is not null
        || message.Voice is not null
        || message.Audio is not null
        || message.Document is not null;

    /// <summary>
    /// Строит платформо-независимое <see cref="RelayMessage"/> из сообщения Telegram: подпись
    /// (текст или caption + entities) и, если есть, одно медиа-вложение, скачанное во временный
    /// файл. Если скачать вложение не удалось — оно опускается (текст всё равно перешлётся).
    /// </summary>
    private async Task<RelayMessage> BuildRelayMessageAsync(Message message, CancellationToken ct)
    {
        var text = message.Text ?? message.Caption ?? string.Empty;
        var entities = message.Entities ?? message.CaptionEntities;
        var caption = TelegramFormatting.ToFormattedText(text, entities);

        var attachment = await TryDownloadMediaAsync(message, ct);
        return new RelayMessage
        {
            Caption = caption,
            Attachments = attachment is null ? [] : [attachment],
            SourceMessageId = message.MessageId.ToString(),
            ReplyToSourceMessageId = message.ReplyToMessage?.MessageId.ToString()
        };
    }

    /// <summary>Определяет тип медиа в сообщении, скачивает его во временный файл и возвращает вложение (или null).</summary>
    private async Task<MediaAttachment?> TryDownloadMediaAsync(Message message, CancellationToken ct)
    {
        // Порядок важен: анимация в Telegram дублируется и в Document, поэтому проверяется раньше.
        if (message.Photo is { Length: > 0 } photo)
            return await MakeAttachmentAsync(photo[^1].FileId, MediaKind.Photo, ".jpg", null, "image/jpeg", ct);
        if (message.Animation is { } animation)
            return await MakeAttachmentAsync(animation.FileId, MediaKind.Animation, Ext(animation.FileName, ".mp4"), animation.FileName, animation.MimeType, ct);
        if (message.Video is { } video)
            return await MakeAttachmentAsync(video.FileId, MediaKind.Video, Ext(video.FileName, ".mp4"), video.FileName, video.MimeType, ct);
        if (message.VideoNote is { } videoNote)
            return await MakeAttachmentAsync(videoNote.FileId, MediaKind.VideoNote, ".mp4", null, "video/mp4", ct);
        if (message.Voice is { } voice)
            return await MakeAttachmentAsync(voice.FileId, MediaKind.Voice, ".ogg", null, voice.MimeType ?? "audio/ogg", ct);
        if (message.Audio is { } audio)
            return await MakeAttachmentAsync(audio.FileId, MediaKind.Audio, Ext(audio.FileName, ".mp3"), audio.FileName, audio.MimeType, ct);
        if (message.Document is { } document)
            return await MakeAttachmentAsync(document.FileId, MediaKind.Document, Ext(document.FileName, ".bin"), document.FileName, document.MimeType, ct);
        return null;
    }

    private async Task<MediaAttachment?> MakeAttachmentAsync(
        string fileId, MediaKind kind, string extension, string? fileName, string? mimeType, CancellationToken ct)
    {
        var path = await _client.DownloadFileToTempAsync(fileId, extension, ct);
        if (path is null)
            return null;

        return new MediaAttachment { Kind = kind, FilePath = path, FileName = fileName, MimeType = mimeType };
    }

    /// <summary>Расширение из имени файла, иначе — запасное.</summary>
    private static string Ext(string? fileName, string fallback)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        return string.IsNullOrEmpty(ext) ? fallback : ext;
    }

    /// <summary>Пишет в лог полный входящий апдейт (сырой JSON) — для отладки.</summary>
    private void LogIncomingUpdate(Update update)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(update, DebugJson);
        }
        catch (Exception ex)
        {
            json = $"(не удалось сериализовать: {ex.Message})";
        }

        _logger.LogInformation("[Telegram] RAW апдейт #{Id} type={Type}:\n{Json}",
            update.Id, update.Type, json);
    }

}
