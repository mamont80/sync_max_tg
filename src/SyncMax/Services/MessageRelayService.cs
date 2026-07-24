using Microsoft.Extensions.Logging;
using SyncMax.Data.Repositories;
using SyncMax.Messengers;
using SyncMax.Models;

namespace SyncMax.Services;

/// <summary>
/// Третий этап: пересылка сообщений между уже связанными чатами/каналами MAX и Telegram
/// (см. <see cref="ChatLinkingService"/> — второй этап, создание самой связки).
/// Поддерживаются текст (с форматированием) и медиа: фото, видео, голос, аудио, анимации,
/// «кружки» video_note, файлы. Сами вложения скачиваются вызывающим *BotService во временные
/// файлы; этот сервис после отправки их удаляет.
///
/// Как и остальные Handle*-сервисы, не зависит от платформы и не знает о *BotService —
/// работает только через <see cref="IMessengerApiClient"/> и <see cref="ChatLinkRepository"/>.
/// </summary>
public sealed class MessageRelayService
{
    private readonly ChatLinkRepository _chatLinks;
    private readonly MessageLinkRepository _messageLinks;
    private readonly IReadOnlyDictionary<MessengerType, IMessengerApiClient> _clients;
    private readonly ILogger<MessageRelayService> _logger;

    public MessageRelayService(
        ChatLinkRepository chatLinks, MessageLinkRepository messageLinks,
        IEnumerable<IMessengerApiClient> clients, ILogger<MessageRelayService> logger)
    {
        _chatLinks = chatLinks;
        _messageLinks = messageLinks;
        _clients = clients.ToDictionary(c => c.Messenger);
        _logger = logger;
    }

    /// <summary>
    /// Пересылает сообщение (текст и/или медиа) из чата <paramref name="chatId"/> (мессенджер
    /// <paramref name="messenger"/>) в связанный чат второго мессенджера, если такая активная
    /// связка есть и её направление (<see cref="RepostDirection"/>) это разрешает. Если связки
    /// нет — тихо ничего не делает (обычный незнакомый чат, не ошибка). Форматирование и медиа
    /// передаются платформо-независимо (<see cref="RelayMessage"/>): вызывающий *BotService
    /// разобрал СВОЙ формат и скачал вложения, а целевой клиент закодирует/загрузит их в СВОЙ.
    /// Временные файлы вложений удаляются здесь по завершении (в любом исходе).
    /// Решение, стоит ли вообще звать этот метод для сообщения от бота (не только своего,
    /// но и любого чужого), принимает вызывающий *BotService — у него есть данные конкретной
    /// платформы об отправителе, а этот сервис от платформы не зависит.
    /// </summary>
    public async Task RelayMessageAsync(
        MessengerType messenger, string chatId, string? senderName, RelayMessage message, CancellationToken ct)
    {
        try
        {
            var link = await _chatLinks.FindActiveByChatAsync(messenger, chatId, ct);
            if (link is null || !AllowsDirection(link, messenger))
                return;

            var targetMessenger = Other(messenger);
            var targetChatId = messenger == MessengerType.Max ? link.TgChatId : link.MaxChatId;

            if (!_clients.TryGetValue(targetMessenger, out var client))
            {
                _logger.LogWarning("Нет клиента для мессенджера {Messenger}.", targetMessenger);
                return;
            }

            // Если исходное сообщение — ответ, ищем в карте копию оригинала в целевом чате,
            // чтобы оформить пересланную копию как ответ на неё. Нет в карте — шлём без ответа.
            string? targetReplyId = null;
            if (message.ReplyToSourceMessageId is { } replySrc)
            {
                var counterpart = await _messageLinks.FindCounterpartAsync(messenger, chatId, replySrc, ct);
                targetReplyId = counterpart?.MsgId;
            }

            // «Шапку» ставим первой строкой; если есть текст/подпись — перед ней с переносом.
            var header = BuildHeader(messenger, senderName);
            var prefix = string.IsNullOrEmpty(message.Caption.Text) ? header : $"{header}\n";
            var payload = message.WithCaptionPrefix(prefix, targetReplyId);

            var sentId = await client.SendChatMessageAsync(targetChatId, payload, ct);

            // Запоминаем связку «оригинал ↔ пересланная копия» для будущих ответов.
            if (sentId is not null && message.SourceMessageId is { } sourceId)
                await StoreMappingAsync(messenger, chatId, sourceId, targetChatId, sentId, ct);

            _logger.LogInformation("Переслано сообщение {FromMessenger}:{FromChatId} -> {ToMessenger}:{ToChatId} (вложений: {Count}).",
                messenger, chatId, targetMessenger, targetChatId, message.Attachments.Count);
        }
        finally
        {
            foreach (var att in message.Attachments)
                TempFiles.TryDelete(att.FilePath);
        }
    }

    /// <summary>
    /// Заголовок пересланного сообщения: "👤 {Имя} · (из MAX)" или "👤 {Имя} · (из TG)" —
    /// метка отражает мессенджер-источник <paramref name="source"/>, независимо от того,
    /// куда сообщение пересылается. Само сообщение идёт с новой строки (см. вызов выше).
    /// </summary>
    private static string BuildHeader(MessengerType source, string? senderName)
    {
        var sourceTag = source == MessengerType.Max ? "MAX" : "TG";
        return string.IsNullOrWhiteSpace(senderName)
            ? $"👤 · (из {sourceTag})"
            : $"👤 {senderName} · (из {sourceTag})";
    }

    /// <summary>
    /// Переносит правку сообщения: находит по карте пересланную копию оригинала в целевом чате
    /// и редактирует её (новый текст/подпись с «шапкой»). Копии нет в карте (не пересылали или
    /// запись устарела) — тихо ничего не делает.
    /// </summary>
    public async Task RelayEditAsync(
        MessengerType messenger, string chatId, string? senderName, string sourceMsgId,
        FormattedText newCaption, bool sourceHasMedia, CancellationToken ct)
    {
        var counterpart = await _messageLinks.FindCounterpartAsync(messenger, chatId, sourceMsgId, ct);
        if (counterpart is not { } cp)
            return;

        var targetMessenger = Other(messenger);
        if (!_clients.TryGetValue(targetMessenger, out var client))
        {
            _logger.LogWarning("Нет клиента для мессенджера {Messenger}.", targetMessenger);
            return;
        }

        var header = BuildHeader(messenger, senderName);
        var prefix = string.IsNullOrEmpty(newCaption.Text) ? header : $"{header}\n";
        var payload = newCaption.WithPrefix(prefix);

        await client.EditChatMessageAsync(cp.ChatId, cp.MsgId, payload, sourceHasMedia, ct);
        _logger.LogInformation("Перенесена правка {FromMessenger}:{FromMsg} -> {ToMessenger}:{ToMsg}.",
            messenger, sourceMsgId, targetMessenger, cp.MsgId);
    }

    /// <summary>
    /// Переносит удаление сообщения: находит по карте пересланную копию и удаляет её, затем
    /// чистит запись карты. Копии нет — тихо ничего не делает.
    /// </summary>
    public async Task RelayDeleteAsync(MessengerType messenger, string chatId, string sourceMsgId, CancellationToken ct)
    {
        var counterpart = await _messageLinks.FindCounterpartAsync(messenger, chatId, sourceMsgId, ct);
        if (counterpart is not { } cp)
            return;

        var targetMessenger = Other(messenger);
        if (!_clients.TryGetValue(targetMessenger, out var client))
        {
            _logger.LogWarning("Нет клиента для мессенджера {Messenger}.", targetMessenger);
            return;
        }

        await client.DeleteChatMessageAsync(cp.ChatId, cp.MsgId, ct);
        await _messageLinks.RemoveAsync(messenger, chatId, sourceMsgId, ct);
        _logger.LogInformation("Перенесено удаление {FromMessenger}:{FromMsg} -> {ToMessenger}:{ToMsg}.",
            messenger, sourceMsgId, targetMessenger, cp.MsgId);
    }

    /// <summary>Сохраняет соответствие оригинала (источник) и пересланной копии (цель) в карту.</summary>
    private Task StoreMappingAsync(
        MessengerType sourceMessenger, string sourceChatId, string sourceMsgId,
        string targetChatId, string targetMsgId, CancellationToken ct)
    {
        var maxChatId = sourceMessenger == MessengerType.Max ? sourceChatId : targetChatId;
        var maxMsgId = sourceMessenger == MessengerType.Max ? sourceMsgId : targetMsgId;
        var tgChatId = sourceMessenger == MessengerType.Telegram ? sourceChatId : targetChatId;
        var tgMsgId = sourceMessenger == MessengerType.Telegram ? sourceMsgId : targetMsgId;
        return _messageLinks.AddAsync(maxChatId, maxMsgId, tgChatId, tgMsgId, ct);
    }

    private static bool AllowsDirection(ChatLink link, MessengerType source) => link.Direction switch
    {
        RepostDirection.Both => true,
        RepostDirection.MaxToTg => source == MessengerType.Max,
        RepostDirection.TgToMax => source == MessengerType.Telegram,
        _ => false
    };

    private static MessengerType Other(MessengerType messenger) =>
        messenger == MessengerType.Max ? MessengerType.Telegram : MessengerType.Max;
}
