using Microsoft.Extensions.Logging;
using SyncMax.Data.Repositories;
using SyncMax.Messengers;
using SyncMax.Models;

namespace SyncMax.Services;

/// <summary>
/// Системные команды, отправленные лично боту (в личном чате, не в группе/канале).
/// Все команды начинаются с '/'. Не зависит от платформы и не знает о *BotService —
/// работает только через <see cref="IMessengerApiClient"/>, как <see cref="LinkingService"/>
/// и <see cref="ChatLinkingService"/>.
/// </summary>
public sealed class SystemCommandService
{
    private readonly UserRepository _users;
    private readonly ChatLinkRepository _chatLinks;
    private readonly LinkingService _linking;
    private readonly IReadOnlyDictionary<MessengerType, IMessengerApiClient> _clients;
    private readonly ILogger<SystemCommandService> _logger;

    public SystemCommandService(
        UserRepository users,
        ChatLinkRepository chatLinks,
        LinkingService linking,
        IEnumerable<IMessengerApiClient> clients,
        ILogger<SystemCommandService> logger)
    {
        _users = users;
        _chatLinks = chatLinks;
        _linking = linking;
        _clients = clients.ToDictionary(c => c.Messenger);
        _logger = logger;
    }

    /// <summary>
    /// Пытается обработать текст как системную команду. Возвращает false, если это не
    /// известная команда (в т.ч. если текст вовсе не начинается с '/') — тогда вызывающий
    /// код должен обработать текст как обычно (например, передать в <see cref="LinkingService"/>).
    /// </summary>
    public async Task<bool> TryHandleAsync(MessengerType messenger, string userId, string text, CancellationToken ct)
    {
        var command = text.Trim();
        if (!command.StartsWith('/'))
            return false;

        switch (command.ToLowerInvariant())
        {
            case "/deletealllinks":
                await HandleDeleteAllLinksAsync(messenger, userId, ct);
                return true;
            case "/clear":
                await HandleClearAsync(messenger, userId, ct);
                return true;
            default:
                return false;
        }
    }

    /// <summary>/deleteAllLinks — удаляет все связки чатов (глобально, не только текущего пользователя).</summary>
    private async Task HandleDeleteAllLinksAsync(MessengerType messenger, string userId, CancellationToken ct)
    {
        await _chatLinks.DeleteAllAsync(ct);
        _logger.LogInformation("{Messenger}:{UserId} удалил все связки чатов (/deleteAllLinks).", messenger, userId);

        var lang = (await _users.GetAsync(messenger, userId, ct))?.Language ?? Localization.Fallback;
        await SendAsync(messenger, userId, Localization.Get(lang, "all_links_deleted"), ct);
    }

    /// <summary>
    /// /clear — удаляет все связки чатов, а также связку аккаунтов и "ожидающий" выбор
    /// чата (<see cref="User.LinkedToUser"/>, <see cref="User.LinkingChatId"/>,
    /// <see cref="User.LinkingChatType"/>, <see cref="User.LinkingChatTitle"/>) у обеих
    /// сторон, если аккаунты были связаны. Обеим сторонам сообщается о сбросе и
    /// заново присылается приглашение связать аккаунты.
    /// </summary>
    private async Task HandleClearAsync(MessengerType messenger, string userId, CancellationToken ct)
    {
        await _chatLinks.DeleteAllAsync(ct);

        var user = await _users.GetAsync(messenger, userId, ct);
        var counterpartUserId = user?.LinkedToUser;

        await ResetAndNotifyAsync(messenger, userId, ct);

        if (counterpartUserId is not null)
        {
            var counterpartMessenger = Other(messenger);
            await ResetAndNotifyAsync(counterpartMessenger, counterpartUserId, ct);
        }

        _logger.LogInformation("{Messenger}:{UserId} сбросил настройки связки (/clear).", messenger, userId);
    }

    private async Task ResetAndNotifyAsync(MessengerType messenger, string userId, CancellationToken ct)
    {
        var lang = (await _users.GetAsync(messenger, userId, ct))?.Language ?? Localization.Fallback;

        await _users.ClearLinkAsync(messenger, userId, ct);
        await SendAsync(messenger, userId, Localization.Get(lang, "settings_reset"), ct);
        await _linking.SendWelcomeInviteAsync(messenger, userId, ct);
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
