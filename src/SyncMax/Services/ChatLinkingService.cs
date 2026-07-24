using Microsoft.Extensions.Logging;
using SyncMax.Data.Repositories;
using SyncMax.Messengers;
using SyncMax.Models;

namespace SyncMax.Services;

/// <summary>
/// Второй этап: создание связок между чатами/каналами MAX и Telegram.
///
/// Пользователь пересылает («репостит») сообщение из чата/канала в бота — это выбор
/// чата с его стороны, он сохраняется в его профиле (<see cref="User.LinkingChatId"/>).
/// Как только то же самое делает связанный с ним аккаунт во втором мессенджере,
/// создаётся активная связка чатов, а "ожидающий" выбор с обеих сторон очищается.
///
/// Доступно только для уже связанных между собой аккаунтов — не связанным
/// предлагается сначала пройти <see cref="LinkingService"/>.
///
/// Как и <see cref="LinkingService"/>, не зависит от *BotService — работает
/// только через <see cref="IMessengerApiClient"/>.
/// </summary>
public sealed class ChatLinkingService
{
    private readonly UserRepository _users;
    private readonly ChatLinkRepository _chatLinks;
    private readonly IReadOnlyDictionary<MessengerType, IMessengerApiClient> _clients;
    private readonly ILogger<ChatLinkingService> _logger;

    public ChatLinkingService(
        UserRepository users,
        ChatLinkRepository chatLinks,
        IEnumerable<IMessengerApiClient> clients,
        ILogger<ChatLinkingService> logger)
    {
        _users = users;
        _chatLinks = chatLinks;
        _clients = clients.ToDictionary(c => c.Messenger);
        _logger = logger;
    }

    /// <summary>Обрабатывает репост сообщения из чата/канала <paramref name="chatId"/> в бота.</summary>
    public async Task HandleRepostAsync(
        MessengerType messenger, string userId, string chatId, ChatKind chatKind, string? chatTitle, CancellationToken ct)
    {
        var user = await _users.GetAsync(messenger, userId, ct);
        var lang = user?.Language ?? Localization.Fallback;

        if (user?.LinkedToUser is not { } counterpartUserId)
        {
            await SendAsync(messenger, userId, Localization.Get(lang, "chat_link_need_account_link"), ct);
            return;
        }

        var counterpartMessenger = Other(messenger);
        var title = string.IsNullOrWhiteSpace(chatTitle) ? chatId : chatTitle;

        await _users.SetLinkingChatAsync(messenger, userId, chatId, chatKind.ToCode(), title, ct);
        _logger.LogInformation("Выбран чат для связки: {Messenger}:{UserId} -> {ChatId} ({ChatKind}) \"{Title}\".",
            messenger, userId, chatId, chatKind, title);

        var counterpart = await _users.GetAsync(counterpartMessenger, counterpartUserId, ct);
        if (counterpart?.LinkingChatId is not { } counterpartChatId || counterpart.LinkingChatKind is not { } counterpartChatKind)
        {
            await SendAsync(messenger, userId, Localization.Format(lang, "chat_link_await_second_side", title), ct);
            return;
        }

        var counterpartTitle = counterpart.LinkingChatTitle ?? counterpartChatId;

        var maxChatId = messenger == MessengerType.Max ? chatId : counterpartChatId;
        var maxChatKind = messenger == MessengerType.Max ? chatKind : counterpartChatKind;
        var maxUserId = messenger == MessengerType.Max ? userId : counterpartUserId;
        var tgChatId = messenger == MessengerType.Telegram ? chatId : counterpartChatId;
        var tgChatKind = messenger == MessengerType.Telegram ? chatKind : counterpartChatKind;
        var tgUserId = messenger == MessengerType.Telegram ? userId : counterpartUserId;

        if (await _chatLinks.ExistsAsync(maxChatId, tgChatId, ct))
        {
            await SendAsync(messenger, userId, Localization.Get(lang, "chat_link_already_exists"), ct);
            await SendAsync(counterpartMessenger, counterpartUserId, Localization.Get(counterpart.Language, "chat_link_already_exists"), ct);
        }
        else
        {
            var link = new ChatLink
            {
                MaxChatId = maxChatId,
                MaxChatType = maxChatKind.ToCode(),
                MaxUserId = maxUserId,
                TgChatId = tgChatId,
                TgChatType = tgChatKind.ToCode(),
                TgUserId = tgUserId,
                Title = $"{counterpartTitle} <=> {title}",
                RepostType = RepostDirection.Both.ToCode(),
                CreatedAt = DateTimeOffset.UtcNow.ToString("o")
            };
            await _chatLinks.CreateAsync(link, ct);

            _logger.LogInformation("Создана связка чатов \"{Title}\": max={MaxChatId} <-> tg={TgChatId}.",
                link.Title, maxChatId, tgChatId);

            await SendAsync(messenger, userId, Localization.Format(lang, "chat_link_created", link.Title), ct);
            await SendAsync(counterpartMessenger, counterpartUserId,
                Localization.Format(counterpart.Language, "chat_link_created", link.Title), ct);
        }

        // Выбор чата использован (успешно или как дубликат) — очищаем ожидание у обеих сторон.
        await _users.SetLinkingChatAsync(messenger, userId, null, null, null, ct);
        await _users.SetLinkingChatAsync(counterpartMessenger, counterpartUserId, null, null, null, ct);
    }

    /// <summary>
    /// Обработка добавления бота в группу/канал.
    /// </summary>
    public async Task HandleAddBotToGroup(string userId, MemberType newMemberType, string chatId, MessengerType messenger, ChatKind chatKind, string? chatTitle, CancellationToken ct)
    {
        var user = await _users.GetAsync(messenger, userId, ct);
        if (user == null) { return; }
        var lang = user.Language ?? Localization.Fallback;
        
        if (newMemberType == MemberType.Administrator)
        {
            await SendAsync(messenger, userId, Localization.Format(lang, "admin_congratulation"), ct);
        }
        if (newMemberType == MemberType.Member)
        {
            await SendAsync(messenger, userId, Localization.Format(lang, "remember_admin"), ct);
        }
    }

    private Task SendAsync(MessengerType messenger, string userId, string text, CancellationToken ct)
    {
        if (_clients.TryGetValue(messenger, out var client))
            return client.SendTextAsync(userId, text, ct);

        _logger.LogWarning("Нет клиента для мессенджера {Messenger}.", messenger);
        return Task.CompletedTask;
    }

    private static MessengerType Other(MessengerType messenger) =>
        messenger == MessengerType.Max ? MessengerType.Telegram : MessengerType.Max;
}
