using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;
using SyncMax.Models;
using SyncMax.Services;

namespace SyncMax.Messengers.Max;

/// <summary>
/// Фоновый сервис MAX-бота: приём обновлений через <see cref="MaxApiClient"/> — либо long
/// polling, либо webhook (см. <see cref="MaxOptions.Mode"/>) — и передача их в
/// <see cref="LinkingService"/>. Отправка ответов — забота клиента, этот класс только
/// принимает и разбирает апдейты. В режиме webhook апдейты поступают снаружи через
/// <see cref="HandleWebhookUpdateAsync"/> (вызывается из HTTP-эндпоинта в Program.cs).
/// </summary>
public sealed class MaxBotService : BackgroundService
{
    private readonly MaxApiClient _client;
    private readonly LinkingService _linking;
    private readonly ChatLinkingService _chatLinking;
    private readonly SystemCommandService _systemCommands;
    private readonly MessageRelayService _relay;
    private readonly MaxOptions _options;
    private readonly ILogger<MaxBotService> _logger;
    private string _botId = string.Empty;

    public MaxBotService(
        MaxApiClient client, LinkingService linking, ChatLinkingService chatLinking,
        SystemCommandService systemCommands, MessageRelayService relay, IOptions<MaxOptions> options,
        ILogger<MaxBotService> logger)
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
        if (!_client.IsConfigured)
        {
            _logger.LogWarning("MAX: токен не задан (Max:Token). Бот отключён.");
            return;
        }

        var me = await _client.GetMeAsync(ct);
        _botId = me?.UserId?.ToString() ?? string.Empty;

        if (_options.Mode == BotMode.Webhook)
        {
            if (string.IsNullOrWhiteSpace(_options.Webhook.Url))
            {
                _logger.LogWarning("MAX: выбран режим Webhook, но Max:Webhook:Url не задан. Бот отключён.");
                return;
            }

            var subscribeUrl = AppendSecretToken(_options.Webhook.Url, WebhookSecret.FromToken(_options.Token));
            await _client.SubscribeWebhookAsync(subscribeUrl, ct);
            _logger.LogInformation("MAX-бот запущен в режиме webhook ({Url}).", _options.Webhook.Url);

            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return;
        }

        _logger.LogInformation("MAX-бот запущен в режиме long polling.");
        long? marker = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var response = await _client.GetUpdatesAsync(marker, ct);

                foreach (var update in response?.Updates ?? [])
                    await LogAndHandleAsync(update, ct);

                marker = response?.Marker ?? marker;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MAX: ошибка опроса, повтор через 3 c.");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    /// <summary>Обрабатывает апдейт, доставленный webhook-запросом (см. эндпоинт в Program.cs).</summary>
    public Task HandleWebhookUpdateAsync(MaxUpdate update, CancellationToken ct) => LogAndHandleAsync(update, ct);

    private Task LogAndHandleAsync(MaxUpdate update, CancellationToken ct)
    {
        _logger.LogInformation("[MAX] апдейт type={Type} sender={Sender} text={Text}",
            update.UpdateType, update.Message?.Sender?.UserId, update.Message?.Body?.Text);
        return DoUpdate(update, ct);
    }

    /// <summary>
    /// Добавляет секрет (производную от токена бота, см. <see cref="WebhookSecret"/>)
    /// query-параметром ?token= к адресу подписки — у MAX нет штатной подписи webhook,
    /// поэтому подлинность входящего запроса проверяется по этому параметру.
    /// </summary>
    private static string AppendSecretToken(string url, string secretToken)
    {
        if (string.IsNullOrEmpty(secretToken))
            return url;

        var separator = url.Contains('?') ? "&" : "?";
        return $"{url}{separator}token={Uri.EscapeDataString(secretToken)}";
    }

    /// <summary>
    /// Обработка отдельного обновления
    /// </summary>
    private async Task DoUpdate(MaxUpdate update, CancellationToken ct)
    {
        // Добавление бота в группу/канал
        if (update.UpdateType == "bot_added")
        {
            var userId = update.User?.UserId?.ToString();
            var chatId = update.ChatId;
            if (!string.IsNullOrEmpty(userId) && chatId is { } addedChatId)
            {
                var chat = await _client.GetChatOrNullAsync(addedChatId, ct);
                var chatKind = chat?.Type == ChatKindExtensions.ChannelCode ? ChatKind.Channel : ChatKind.Chat;
                await _chatLinking.HandleAddBotToGroup(
                    userId, MemberType.Member, addedChatId.ToString(), MessengerType.Max, chatKind, chat?.Title, ct);
            }
            return;
        }

        // Удаление сообщения пользователем — переносим на пересланную копию.
        if (update.UpdateType == "message_removed")
        {
            if (update.MessageId is { Length: > 0 } removedMid && update.RemovedUserId?.ToString() != _botId)
                await _relay.RelayDeleteAsync(MessengerType.Max, update.ChatId?.ToString() ?? string.Empty, removedMid, ct);
            return;
        }

        var message = update.Message;
        if (message?.Sender?.UserId is not { } uid)
            return;

        var userId2 = uid.ToString();

        // Эхо собственного сообщения бота (например, отправленного при пересылке в этот же
        // чат) — не обрабатываем повторно, иначе связка с обеих сторон зациклится.
        if (!string.IsNullOrEmpty(_botId) && userId2 == _botId)
            return;

        // Правка сообщения пользователем — переносим на пересланную копию (а не пересылаем заново).
        if (update.UpdateType == "message_edited")
        {
            await HandleMaxEditAsync(message, ct);
            return;
        }

        //Это сообщение лично боту
        if (message.Recipient?.ChatType is null or ChatKindExtensions.DialogCode)
        {
            if (message.Body?.Text is not { } text)
                return;
            if (await _systemCommands.TryHandleAsync(MessengerType.Max, userId2, text, ct))
                return;

            await _linking.HandleAsync(MessengerType.Max, userId2, message.Sender.Name, text, ct);
            return;
        }

        //Это сообщение в группу/канал
        if (message.Recipient is { ChatId: { } chatId2, ChatType: ChatKindExtensions.ChatCode or ChatKindExtensions.ChannelCode } recipient)
        {
            var chatId2str = chatId2.ToString();
            var command = message.Body?.Text?.Trim().ToLower();
            if (command == "/link")
            {
                var chatKind = recipient.ChatType == ChatKindExtensions.ChannelCode ? ChatKind.Channel : ChatKind.Chat;
                var chat = await _client.GetChatOrNullAsync(chatId2, ct);
                await _chatLinking.HandleRepostAsync(MessengerType.Max, userId2, chatId2str, chatKind, chat?.Title, ct);
                return;
            }

            // Сообщения от ботов (не только от себя самого, но и от любых других
            // ботов в чате) не пересылаем — это не пользовательский контент.
            if (message.Sender.IsBot == true)
                return;

            var relay = await BuildRelayMessageAsync(message, ct);
            if (relay.IsEmpty)
                return;

            await _relay.RelayMessageAsync(MessengerType.Max, chatId2str, message.Sender.Name, relay, ct);
        }
    }

    /// <summary>
    /// Строит платформо-независимое <see cref="RelayMessage"/> из тела сообщения MAX: подпись
    /// (текст + markup) и медиа-вложения, скачанные во временные файлы по прямым url. Вложения,
    /// которые не удалось скачать или тип которых не поддерживается, опускаются.
    /// </summary>
    /// <summary>
    /// Переносит правку сообщения MAX на пересланную копию: новый текст/подпись (медиа не
    /// перезагружаем). Работает только для сообщений в связанных группах/каналах.
    /// </summary>
    private async Task HandleMaxEditAsync(MaxMessage message, CancellationToken ct)
    {
        if (message.Recipient is not { ChatId: { } chatId, ChatType: ChatKindExtensions.ChatCode or ChatKindExtensions.ChannelCode })
            return;
        if (message.Body?.Mid is not { Length: > 0 } mid)
            return;

        var caption = MaxFormatting.ToFormattedText(message.Body);
        var hasMedia = message.Body.Attachments?.Any(a => a.Type is "image" or "video" or "audio" or "file") == true;

        await _relay.RelayEditAsync(MessengerType.Max, chatId.ToString(), message.Sender?.Name, mid, caption, hasMedia, ct);
    }

    private async Task<RelayMessage> BuildRelayMessageAsync(MaxMessage message, CancellationToken ct)
    {
        var body = message.Body;
        var caption = MaxFormatting.ToFormattedText(body);

        var attachments = new List<MediaAttachment>();
        foreach (var a in body?.Attachments ?? [])
        {
            var media = await TryDownloadAttachmentAsync(a, ct);
            if (media is not null)
                attachments.Add(media);
        }

        // Ответ (reply) — берём mid исходного сообщения из link (forward нас не интересует).
        var replyToMid = message.Link is { Type: "reply", Message.Mid: { } mid } ? mid : null;

        return new RelayMessage
        {
            Caption = caption,
            Attachments = attachments,
            SourceMessageId = body?.Mid,
            ReplyToSourceMessageId = replyToMid
        };
    }

    private async Task<MediaAttachment?> TryDownloadAttachmentAsync(MaxAttachment attachment, CancellationToken ct)
    {
        (MediaKind? Kind, string Ext) map = attachment.Type switch
        {
            "image" => (MediaKind.Photo, ".jpg"),
            "video" => (MediaKind.Video, ".mp4"),
            "audio" => (MediaKind.Audio, ".mp3"),
            "file" => (MediaKind.Document, FileExt(attachment.Filename, ".bin")),
            _ => (null, string.Empty) // sticker/share/location/... — не пересылаем
        };
        if (map.Kind is not { } kind)
        {
            _logger.LogInformation("MAX: вложение type={Type} не поддерживается для пересылки — пропущено.", attachment.Type);
            return null;
        }

        if (attachment.Payload?.Url is not { Length: > 0 } url)
        {
            _logger.LogWarning("MAX: вложение type={Type} без url (token={HasToken}) — переслать нечем.",
                attachment.Type, attachment.Payload?.Token is { Length: > 0 });
            return null;
        }

        var path = await _client.DownloadUrlToTempAsync(url, map.Ext, ct);
        if (path is null)
        {
            _logger.LogWarning("MAX: не удалось скачать вложение type={Type} — пропущено.", attachment.Type);
            return null;
        }

        return new MediaAttachment { Kind = kind, FilePath = path, FileName = attachment.Filename };
    }

    private static string FileExt(string? fileName, string fallback)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        return string.IsNullOrEmpty(ext) ? fallback : ext;
    }
}
