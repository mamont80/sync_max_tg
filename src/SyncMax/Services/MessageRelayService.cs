using Microsoft.Extensions.Logging;
using SyncMax.Data.Repositories;
using SyncMax.Messengers;
using SyncMax.Models;
using SyncMax.Services.Moderation;

namespace SyncMax.Services;

/// <summary>
/// Третий этап: пересылка сообщений между уже связанными чатами/каналами MAX и Telegram
/// (см. <see cref="ChatLinkingService"/> — второй этап, создание самой связки).
/// Поддерживаются текст (с форматированием) и медиа: фото, видео, голос, аудио, анимации,
/// «кружки» video_note, файлы. Сами вложения скачиваются вызывающим *BotService во временные
/// файлы; этот сервис после отправки их удаляет.
///
/// Любой перенос — сообщение, правка, удаление — возможен только по активной связке, чьё
/// направление (<see cref="RepostDirection"/>) разрешает движение из мессенджера-источника;
/// общая проверка в <see cref="FindRelayableLinkAsync"/>.
///
/// Как и остальные Handle*-сервисы, не зависит от платформы и не знает о *BotService —
/// работает только через <see cref="IMessengerApiClient"/> и <see cref="ChatLinkRepository"/>.
/// </summary>
public sealed class MessageRelayService
{
    private readonly ChatLinkRepository _chatLinks;
    private readonly MessageLinkRepository _messageLinks;
    private readonly ModerationService _moderation;
    private readonly IReadOnlyDictionary<MessengerType, IMessengerApiClient> _clients;
    private readonly ILogger<MessageRelayService> _logger;

    public MessageRelayService(
        ChatLinkRepository chatLinks, MessageLinkRepository messageLinks,
        ModerationService moderation, IEnumerable<IMessengerApiClient> clients,
        ILogger<MessageRelayService> logger)
    {
        _chatLinks = chatLinks;
        _messageLinks = messageLinks;
        _moderation = moderation;
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
            var link = await FindRelayableLinkAsync(messenger, chatId, ct);
            if (link is null)
                return;

            var targetMessenger = Other(messenger);
            var targetChatId = TargetChatId(link, messenger);

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
                if (counterpart is { } reply && IsCurrentTarget(link, messenger, reply.ChatId))
                    targetReplyId = reply.MsgId;
            }

            // Модерация — до отправки и для всего сразу: и текста, и вложений (заблокированное
            // сообщение уходит заглушкой без медиа).
            var verdict = _moderation.Check(message.Caption);

            RelayMessage payload;
            if (verdict.Decision == ModerationDecision.Blocked)
            {
                _logger.LogWarning("Модерация: сообщение из {Messenger}:{ChatId} не переслано ({Reason}).",
                    messenger, chatId, verdict.Reason);

                var header = BuildHeader(messenger, senderName, modified: false);
                payload = new RelayMessage
                {
                    Caption = FormattedText.Plain($"{header}\n{ModerationService.BlockedPlaceholder}"),
                    SourceMessageId = message.SourceMessageId,
                    ReplyToTargetMessageId = targetReplyId
                };
            }
            else
            {
                // «Шапку» ставим первой строкой; если есть текст/подпись — перед ней с переносом.
                var header = BuildHeader(messenger, senderName, verdict.Decision == ModerationDecision.Masked);
                var prefix = string.IsNullOrEmpty(verdict.Text.Text) ? header : $"{header}\n";
                payload = message.WithModeratedCaption(verdict.Text).WithCaptionPrefix(prefix, targetReplyId);
            }

            var sentId = await client.SendChatMessageAsync(targetChatId, payload, ct);

            // Запоминаем связку «оригинал ↔ пересланная копия» для будущих ответов.
            if (sentId is not null && message.SourceMessageId is { } sourceId)
                await StoreMappingAsync(messenger, chatId, sourceId, targetChatId, sentId, ct);

            _logger.LogInformation("Переслано сообщение {FromMessenger}:{FromChatId} -> {ToMessenger}:{ToChatId} (вложений: {Count}).",
                messenger, chatId, targetMessenger, targetChatId, message.Attachments.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Не выпускаем ошибку наружу: в режиме webhook она дошла бы до HTTP-эндпоинта,
            // тот ответил бы 500, и мессенджер прислал бы тот же апдейт повторно — а значит,
            // при частично удавшейся отправке пошли бы дубли. Одно несостоявшееся сообщение
            // лучше, чем бесконечные повторы.
            _logger.LogError(ex, "Не удалось переслать сообщение {FromMessenger}:{FromChatId}.", messenger, chatId);
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
    /// <paramref name="modified"/> — текст правил бот (замаскирована брань): читатель должен
    /// понимать, что видит не дословную копию.
    /// </summary>
    private static string BuildHeader(MessengerType source, string? senderName, bool modified)
    {
        var sourceTag = source == MessengerType.Max ? "MAX" : "TG";
        var mark = modified ? " [изменено ботом]" : string.Empty;

        return string.IsNullOrWhiteSpace(senderName)
            ? $"👤 · (из {sourceTag}){mark}"
            : $"👤 {senderName} · (из {sourceTag}){mark}";
    }

    /// <summary>
    /// Переносит правку сообщения: находит по карте пересланную копию оригинала в целевом чате
    /// и редактирует её (новый текст/подпись с «шапкой»). Копии нет в карте (не пересылали или
    /// запись устарела) — тихо ничего не делает. Как и обычная пересылка, требует активной
    /// связки, направление которой разрешает перенос из этого мессенджера.
    /// </summary>
    public async Task RelayEditAsync(
        MessengerType messenger, string chatId, string? senderName, string sourceMsgId,
        FormattedText newCaption, bool sourceHasMedia, CancellationToken ct)
    {
        var link = await FindRelayableLinkAsync(messenger, chatId, ct);
        if (link is null)
            return;

        var counterpart = await _messageLinks.FindCounterpartAsync(messenger, chatId, sourceMsgId, ct);
        if (counterpart is not { } cp || !IsCurrentTarget(link, messenger, cp.ChatId))
            return;

        var targetMessenger = Other(messenger);
        if (!_clients.TryGetValue(targetMessenger, out var client))
        {
            _logger.LogWarning("Нет клиента для мессенджера {Messenger}.", targetMessenger);
            return;
        }

        // Правка проходит ту же модерацию, что и само сообщение: иначе запрет обходился бы
        // отправкой безобидного текста с последующей правкой на запрещённый.
        var verdict = _moderation.Check(newCaption);

        FormattedText payload;
        if (verdict.Decision == ModerationDecision.Blocked)
        {
            _logger.LogWarning("Модерация: правка из {Messenger}:{ChatId} не перенесена ({Reason}).",
                messenger, chatId, verdict.Reason);

            var blockedHeader = BuildHeader(messenger, senderName, modified: false);
            payload = FormattedText.Plain($"{blockedHeader}\n{ModerationService.BlockedPlaceholder}");
        }
        else
        {
            var header = BuildHeader(messenger, senderName, verdict.Decision == ModerationDecision.Masked);
            var prefix = string.IsNullOrEmpty(verdict.Text.Text) ? header : $"{header}\n";
            payload = verdict.Text.WithPrefix(prefix);
        }

        await client.EditChatMessageAsync(cp.ChatId, cp.MsgId, payload, sourceHasMedia, ct);
        _logger.LogInformation("Перенесена правка {FromMessenger}:{FromMsg} -> {ToMessenger}:{ToMsg}.",
            messenger, sourceMsgId, targetMessenger, cp.MsgId);
    }

    /// <summary>
    /// Переносит удаление сообщения: находит по карте пересланную копию и удаляет её, затем
    /// чистит запись карты. Копии нет — тихо ничего не делает. Как и обычная пересылка,
    /// требует активной связки, направление которой разрешает перенос из этого мессенджера.
    /// </summary>
    public async Task RelayDeleteAsync(MessengerType messenger, string chatId, string sourceMsgId, CancellationToken ct)
    {
        var link = await FindRelayableLinkAsync(messenger, chatId, ct);
        if (link is null)
            return;

        var counterpart = await _messageLinks.FindCounterpartAsync(messenger, chatId, sourceMsgId, ct);
        if (counterpart is not { } cp || !IsCurrentTarget(link, messenger, cp.ChatId))
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

    /// <summary>
    /// Находит связку, по которой сейчас разрешено переносить действие из чата
    /// <paramref name="chatId"/> мессенджера <paramref name="source"/>, — общая проверка для
    /// всех типов: обычного сообщения, правки и удаления. Возвращает null, если связки нет,
    /// она деактивирована (<c>active = 0</c>) либо её направление одностороннее и смотрит в
    /// другую сторону; во всех этих случаях переносить нельзя и мы тихо ничего не делаем.
    /// </summary>
    private async Task<ChatLink?> FindRelayableLinkAsync(MessengerType source, string chatId, CancellationToken ct)
    {
        var link = await _chatLinks.FindActiveByChatAsync(source, chatId, ct);
        return link is not null && AllowsDirection(link, source) ? link : null;
    }

    /// <summary>Чат второй стороны связки — куда переносим из <paramref name="source"/>.</summary>
    private static string TargetChatId(ChatLink link, MessengerType source) =>
        source == MessengerType.Max ? link.TgChatId : link.MaxChatId;

    /// <summary>
    /// Лежит ли найденная в карте копия именно в том чате, с которым связка действует сейчас.
    /// Записи <c>message_links</c> переживают удаление связки, поэтому если чат позже связали
    /// с другим — старая копия окажется в уже не связанном чате, и трогать её нельзя.
    /// </summary>
    private static bool IsCurrentTarget(ChatLink link, MessengerType source, string counterpartChatId) =>
        counterpartChatId == TargetChatId(link, source);

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
