using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncMax.Models;
using SyncMax.Services;

namespace SyncMax.Messengers.Max;

/// <summary>
/// Фоновый сервис MAX-бота: long polling обновлений через <see cref="MaxApiClient"/>
/// и передача их в <see cref="LinkingService"/>. Отправка ответов — забота клиента,
/// этот класс только принимает и разбирает апдейты.
/// </summary>
public sealed class MaxBotService : BackgroundService
{
    private readonly MaxApiClient _client;
    private readonly LinkingService _linking;
    private readonly ChatLinkingService _chatLinking;
    private readonly SystemCommandService _systemCommands;
    private readonly MessageRelayService _relay;
    private readonly ILogger<MaxBotService> _logger;
    private string _botId = string.Empty;

    public MaxBotService(
        MaxApiClient client, LinkingService linking, ChatLinkingService chatLinking,
        SystemCommandService systemCommands, MessageRelayService relay, ILogger<MaxBotService> logger)
    {
        _client = client;
        _linking = linking;
        _chatLinking = chatLinking;
        _systemCommands = systemCommands;
        _relay = relay;
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
        _logger.LogInformation("MAX-бот запущен.");

        long? marker = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var response = await _client.GetUpdatesAsync(marker, ct);

                foreach (var update in response?.Updates ?? [])
                {
                    _logger.LogInformation("[MAX] апдейт type={Type} sender={Sender} text={Text}",
                        update.UpdateType, update.Message?.Sender?.UserId, update.Message?.Body?.Text);
                    await DoUpdate(update, ct);
                }

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

        var message = update.Message;
        if (message?.Sender?.UserId is not { } uid)
            return;

        var userId2 = uid.ToString();

        // Эхо собственного сообщения бота (например, отправленного при пересылке в этот же
        // чат) — не обрабатываем повторно, иначе связка с обеих сторон зациклится.
        if (!string.IsNullOrEmpty(_botId) && userId2 == _botId)
            return;

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

            var relay = await BuildRelayMessageAsync(message.Body, ct);
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
    private async Task<RelayMessage> BuildRelayMessageAsync(MaxMessageBody? body, CancellationToken ct)
    {
        var caption = MaxFormatting.ToFormattedText(body);

        var attachments = new List<MediaAttachment>();
        foreach (var a in body?.Attachments ?? [])
        {
            var media = await TryDownloadAttachmentAsync(a, ct);
            if (media is not null)
                attachments.Add(media);
        }

        return new RelayMessage { Caption = caption, Attachments = attachments };
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
