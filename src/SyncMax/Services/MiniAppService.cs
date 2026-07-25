using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;
using SyncMax.Data.Repositories;
using SyncMax.Models;
using SyncMax.WebApp;

namespace SyncMax.Services;

/// <summary>
/// Вся логика мини-приложения. Как <see cref="LinkingService"/> и
/// <see cref="ChatLinkingService"/>, не зависит от платформы и не знает о *BotService:
/// работает через репозитории и общий <see cref="LinkingService"/>. Эндпоинты
/// (<see cref="WebApp.MiniAppEndpoints"/>) остаются тонкими — здесь же и проверка прав.
/// </summary>
public sealed class MiniAppService
{
    private readonly UserRepository _users;
    private readonly ChatLinkRepository _chatLinks;
    private readonly MessageLinkRepository _messageLinks;
    private readonly LinkingService _linking;
    private readonly LinkingOptions _options;
    private readonly ILogger<MiniAppService> _logger;

    public MiniAppService(
        UserRepository users,
        ChatLinkRepository chatLinks,
        MessageLinkRepository messageLinks,
        LinkingService linking,
        IOptions<LinkingOptions> options,
        ILogger<MiniAppService> logger)
    {
        _users = users;
        _chatLinks = chatLinks;
        _messageLinks = messageLinks;
        _linking = linking;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Профиль для стартового экрана. Заодно регистрирует пользователя, если тот открыл
    /// мини-приложение раньше, чем написал боту, — иначе дальше не за что зацепиться.
    /// Пока аккаунты не связаны, сразу выдаётся код связки: это первое, что нужно на экране.
    /// </summary>
    public async Task<ProfileResponse> GetProfileAsync(MiniAppUser caller, CancellationToken ct)
    {
        await _users.UpsertAsync(caller.Messenger, caller.UserId, caller.Name, _options.DefaultLanguage, ct);

        var user = await _users.GetAsync(caller.Messenger, caller.UserId, ct);
        var linked = user?.LinkedToUser is not null;

        var code = user?.LinkCode;
        if (!linked && string.IsNullOrWhiteSpace(code))
            code = await _linking.IssueLinkCodeAsync(caller.Messenger, caller.UserId, ct);

        return new ProfileResponse
        {
            Messenger = caller.Messenger.ToCode(),
            Name = user?.Name ?? caller.Name,
            Linked = linked,
            LinkedMessenger = linked ? Other(caller.Messenger).ToCode() : null,
            LinkCode = linked ? null : code
        };
    }

    /// <summary>Перевыпуск кода связки по кнопке «Обновить код». null — аккаунт уже связан.</summary>
    public Task<string?> RefreshLinkCodeAsync(MiniAppUser caller, CancellationToken ct) =>
        _linking.IssueLinkCodeAsync(caller.Messenger, caller.UserId, ct);

    /// <summary>
    /// Разрывает связку аккаунтов: удаляет связки чатов этой пары и сбрасывает связь
    /// у обеих сторон, каждой отправляя уведомление и новое приглашение. В отличие от
    /// команды <c>/deleteAllLinks</c>, чужие связки не трогаются — удаляются только
    /// принадлежащие этой паре пользователей.
    /// </summary>
    public async Task UnlinkAccountsAsync(MiniAppUser caller, CancellationToken ct)
    {
        var user = await _users.GetAsync(caller.Messenger, caller.UserId, ct);
        var counterpartUserId = user?.LinkedToUser;

        foreach (var link in await ListOwnLinksAsync(caller, ct))
            await DeleteLinkWithMapAsync(link, ct);

        await _linking.ResetAndNotifyAsync(caller.Messenger, caller.UserId, ct);

        if (counterpartUserId is not null)
            await _linking.ResetAndNotifyAsync(Other(caller.Messenger), counterpartUserId, ct);

        _logger.LogInformation("[MiniApp] {Messenger}:{UserId} разорвал связку аккаунтов.",
            caller.Messenger, caller.UserId);
    }

    /// <summary>Связки чатов, доступные вызывающему.</summary>
    public async Task<IReadOnlyList<ChatLinkResponse>> ListChatLinksAsync(MiniAppUser caller, CancellationToken ct)
    {
        var links = await ListOwnLinksAsync(caller, ct);
        return links.Select(ToResponse).ToList();
    }

    /// <summary>
    /// Меняет активность и/или направление связки. Возвращает обновлённую связку либо null,
    /// если её нет или она принадлежит другому пользователю (эндпоинт отвечает 404).
    /// </summary>
    public async Task<ChatLinkResponse?> UpdateChatLinkAsync(
        MiniAppUser caller, long id, UpdateChatLinkRequest request, CancellationToken ct)
    {
        var link = await GetOwnLinkAsync(caller, id, ct);
        if (link is null)
            return null;

        if (request.Active is { } active && active != link.Active)
        {
            await _chatLinks.SetActiveAsync(id, active, ct);
            _logger.LogInformation("[MiniApp] Связка {Id} \"{Title}\" переведена в active={Active}.",
                id, link.Title, active);
        }

        if (request.Direction is { } directionCode)
        {
            // Разбор до записи: в repost_type должен попадать только код перечисления,
            // иначе RepostDirectionExtensions.FromCode позже свалится на чтении.
            RepostDirection direction;
            try
            {
                direction = RepostDirectionExtensions.FromCode(directionCode);
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new ArgumentException($"Неизвестное направление пересылки: {directionCode}");
            }

            if (direction != link.Direction)
            {
                await _chatLinks.SetDirectionAsync(id, direction, ct);
                _logger.LogInformation("[MiniApp] У связки {Id} \"{Title}\" направление изменено на {Direction}.",
                    id, link.Title, direction);
            }
        }

        var updated = await _chatLinks.GetByIdAsync(id, ct);
        return updated is null ? null : ToResponse(updated);
    }

    /// <summary>Удаляет связку. false — её нет или она не принадлежит вызывающему.</summary>
    public async Task<bool> DeleteChatLinkAsync(MiniAppUser caller, long id, CancellationToken ct)
    {
        var link = await GetOwnLinkAsync(caller, id, ct);
        if (link is null)
            return false;

        await DeleteLinkWithMapAsync(link, ct);
        _logger.LogInformation("[MiniApp] {Messenger}:{UserId} удалил связку {Id} \"{Title}\".",
            caller.Messenger, caller.UserId, id, link.Title);
        return true;
    }

    /// <summary>Связка и её карта «оригинал ↔ копия» удаляются только вместе.</summary>
    private async Task DeleteLinkWithMapAsync(ChatLink link, CancellationToken ct)
    {
        await _messageLinks.DeleteByChatPairAsync(link.MaxChatId, link.TgChatId, ct);
        await _chatLinks.DeleteAsync(link.Id, ct);
    }

    private async Task<IReadOnlyList<ChatLink>> ListOwnLinksAsync(MiniAppUser caller, CancellationToken ct)
    {
        var (maxUserId, tgUserId) = await ResolveSideIdsAsync(caller, ct);
        return await _chatLinks.ListForUserAsync(maxUserId, tgUserId, ct);
    }

    /// <summary>
    /// Связка по id — но только если вызывающий (или его связанный аккаунт) записан её
    /// стороной. Без этой проверки чужую связку можно было бы выключить или удалить,
    /// просто перебирая id.
    /// </summary>
    private async Task<ChatLink?> GetOwnLinkAsync(MiniAppUser caller, long id, CancellationToken ct)
    {
        var link = await _chatLinks.GetByIdAsync(id, ct);
        if (link is null)
            return null;

        var (maxUserId, tgUserId) = await ResolveSideIdsAsync(caller, ct);
        var mine = (maxUserId is not null && link.MaxUserId == maxUserId)
                || (tgUserId is not null && link.TgUserId == tgUserId);

        if (!mine)
        {
            _logger.LogWarning("[MiniApp] {Messenger}:{UserId} обратился к чужой связке {Id}.",
                caller.Messenger, caller.UserId, id);
            return null;
        }

        return link;
    }

    /// <summary>
    /// Раскладывает вызывающего и его связанный аккаунт по сторонам (id в MAX, id в Telegram).
    /// Сравнивать id нужно именно по своей стороне: идентификаторы двух мессенджеров
    /// независимы, и «мой id» из MAX не должен случайно совпасть с чьим-то tg_user_id.
    /// </summary>
    private async Task<(string? MaxUserId, string? TgUserId)> ResolveSideIdsAsync(
        MiniAppUser caller, CancellationToken ct)
    {
        var user = await _users.GetAsync(caller.Messenger, caller.UserId, ct);
        var counterpartUserId = user?.LinkedToUser;

        return caller.Messenger == MessengerType.Max
            ? (caller.UserId, counterpartUserId)
            : (counterpartUserId, caller.UserId);
    }

    private static ChatLinkResponse ToResponse(ChatLink link) => new()
    {
        Id = link.Id,
        Title = link.Title,
        MaxTitle = link.MaxChatTitle,
        TgTitle = link.TgChatTitle,
        MaxKind = link.MaxChatType,
        TgKind = link.TgChatType,
        Active = link.Active,
        Direction = link.RepostType,
        CreatedAt = link.CreatedAt
    };

    private static MessengerType Other(MessengerType messenger) =>
        messenger == MessengerType.Max ? MessengerType.Telegram : MessengerType.Max;
}
