using Microsoft.Extensions.Logging;
using SyncMax.Data.Repositories;
using SyncMax.Messengers;
using SyncMax.Models;

namespace SyncMax.Services;

/// <summary>
/// Третий этап: пересылка сообщений между уже связанными чатами/каналами MAX и Telegram
/// (см. <see cref="ChatLinkingService"/> — второй этап, создание самой связки).
/// Пока поддерживаются только текстовые сообщения.
///
/// Как и остальные Handle*-сервисы, не зависит от платформы и не знает о *BotService —
/// работает только через <see cref="IMessengerApiClient"/> и <see cref="ChatLinkRepository"/>.
/// </summary>
public sealed class MessageRelayService
{
    private readonly ChatLinkRepository _chatLinks;
    private readonly IReadOnlyDictionary<MessengerType, IMessengerApiClient> _clients;
    private readonly ILogger<MessageRelayService> _logger;

    public MessageRelayService(
        ChatLinkRepository chatLinks, IEnumerable<IMessengerApiClient> clients, ILogger<MessageRelayService> logger)
    {
        _chatLinks = chatLinks;
        _clients = clients.ToDictionary(c => c.Messenger);
        _logger = logger;
    }

    /// <summary>
    /// Пересылает текстовое сообщение из чата <paramref name="chatId"/> (мессенджер
    /// <paramref name="messenger"/>) в связанный чат второго мессенджера, если такая
    /// активная связка есть и её направление (<see cref="RepostDirection"/>) это разрешает.
    /// Если связки нет — тихо ничего не делает (это обычный незнакомый чат, не ошибка).
    /// Форматирование передаётся платформо-независимо (<see cref="FormattedText"/>): вызывающий
    /// *BotService разобрал СВОЙ формат в участки, а целевой клиент закодирует их в СВОЙ.
    /// Решение, стоит ли вообще звать этот метод для сообщения от бота (не только своего,
    /// но и любого чужого), принимает вызывающий *BotService — у него есть данные конкретной
    /// платформы об отправителе, а этот сервис от платформы не зависит.
    /// </summary>
    public async Task RelayTextAsync(
        MessengerType messenger, string chatId, string? senderName, FormattedText body, CancellationToken ct)
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

        var payload = body.WithPrefix($"{BuildHeader(messenger, senderName)}\n");
        await client.SendChatTextAsync(targetChatId, payload, ct);

        _logger.LogInformation("Переслано сообщение {FromMessenger}:{FromChatId} -> {ToMessenger}:{ToChatId}.",
            messenger, chatId, targetMessenger, targetChatId);
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
