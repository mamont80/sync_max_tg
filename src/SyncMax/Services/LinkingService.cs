using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;
using SyncMax.Data.Repositories;
using SyncMax.Messengers;
using SyncMax.Models;

namespace SyncMax.Services;

/// <summary>
/// Ядро первого этапа: приём входящих сообщений от любого мессенджера,
/// выдача кода связки и собственно связывание двух аккаунтов.
/// Не зависит от конкретной платформы и не знает о *BotService — работает
/// только через <see cref="IMessengerApiClient"/>.
/// </summary>
public sealed class LinkingService
{
    /// <summary>Коды бессрочны, поэтому их можно подбирать — троттлим попытки ввода.</summary>
    private static readonly TimeSpan CodeAttemptCooldown = TimeSpan.FromSeconds(2);

    private readonly UserRepository _users;
    private readonly CodeGenerator _codes;
    private readonly IReadOnlyDictionary<MessengerType, IMessengerApiClient> _clients;
    private readonly LinkingOptions _options;
    private readonly ILogger<LinkingService> _logger;

    /// <summary>Время последней принятой в обработку попытки ввода кода, по пользователю.</summary>
    private readonly ConcurrentDictionary<(MessengerType Messenger, string UserId), DateTimeOffset> _lastCodeAttempt = new();

    public LinkingService(
        UserRepository users,
        CodeGenerator codes,
        IEnumerable<IMessengerApiClient> clients,
        IOptions<LinkingOptions> options,
        ILogger<LinkingService> logger)
    {
        _users = users;
        _codes = codes;
        _clients = clients.ToDictionary(c => c.Messenger);
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Единая точка входа для сообщений из любого мессенджера.</summary>
    public async Task HandleAsync(
        MessengerType messenger, string userId, string? name, string text, CancellationToken ct)
    {
        // Отладка: показываем каждое входящее сообщение от любого мессенджера.
        _logger.LogInformation("[Входящее] {Messenger} userId={UserId} name={Name}: {Text}",
            messenger, userId, name ?? "-", text);

        text = text.Trim();

        // Регистрируем/обновляем пользователя при любом обращении.
        await _users.UpsertAsync(messenger, userId, name, _options.DefaultLanguage, ct);

        var user = await _users.GetAsync(messenger, userId, ct);
        var lang = user?.Language ?? _options.DefaultLanguage;

        if (IsStartCommand(text))
        {
            await SendCodeAsync(messenger, userId, user, lang, ct);
            return;
        }

        if (IsSixDigitCode(text))
        {
            if (IsRateLimited(messenger, userId))
            {
                await SendAsync(messenger, userId, Localization.Get(lang, "rate_limited"), ct);
                return;
            }

            await TryLinkAsync(messenger, userId, text, lang, ct);
            return;
        }

        await SendAsync(messenger, userId, Localization.Get(lang, "help"), ct);
    }

    /// <summary>
    /// Генерирует новый код связки и отправляет приглашение связать аккаунты — то же самое,
    /// что происходит по /start. Используется также после сброса связки (/clear).
    /// </summary>
    public async Task SendWelcomeInviteAsync(MessengerType messenger, string userId, CancellationToken ct)
    {
        var user = await _users.GetAsync(messenger, userId, ct);
        var lang = user?.Language ?? _options.DefaultLanguage;
        await SendCodeAsync(messenger, userId, user, lang, ct);
    }

    /// <summary>
    /// Выпускает новый код связки и возвращает его, ничего не отправляя в чат — для
    /// мини-приложения, которое показывает код прямо в интерфейсе, и дублирующее
    /// сообщение от бота было бы шумом. Возвращает null, если аккаунт уже связан.
    /// </summary>
    public async Task<string?> IssueLinkCodeAsync(MessengerType messenger, string userId, CancellationToken ct)
    {
        var user = await _users.GetAsync(messenger, userId, ct);
        if (user?.LinkedToUser is not null)
            return null;

        return await GenerateAndSaveCodeAsync(messenger, userId, ct);
    }

    /// <summary>
    /// Сбрасывает связку аккаунта со вторым мессенджером, сообщает об этом пользователю
    /// и сразу присылает новое приглашение связаться (как после /start). Вызывается и
    /// системной командой /clear, и мини-приложением — поведение для обоих должно быть
    /// одинаковым, поэтому логика живёт здесь, а не в вызывающих.
    /// </summary>
    public async Task ResetAndNotifyAsync(MessengerType messenger, string userId, CancellationToken ct)
    {
        var lang = (await _users.GetAsync(messenger, userId, ct))?.Language ?? Localization.Fallback;

        await _users.ClearLinkAsync(messenger, userId, ct);
        await SendAsync(messenger, userId, Localization.Get(lang, "settings_reset"), ct);
        await SendWelcomeInviteAsync(messenger, userId, ct);
    }

    private async Task SendCodeAsync(MessengerType messenger, string userId, User? user, string lang, CancellationToken ct)
    {
        if (user?.LinkedToUser is not null)
        {
            await SendAsync(messenger, userId, Localization.Get(lang, "already_linked"), ct);
            return;
        }

        var code = await GenerateAndSaveCodeAsync(messenger, userId, ct);
        await SendAsync(messenger, userId, Localization.Format(lang, "welcome", code), ct);
    }

    private async Task<string> GenerateAndSaveCodeAsync(MessengerType messenger, string userId, CancellationToken ct)
    {
        var code = _codes.Generate();
        await _users.SetLinkCodeAsync(messenger, userId, code, DateTimeOffset.UtcNow, ct);
        _logger.LogInformation("Выдан код связки пользователю {Messenger}:{UserId}.", messenger, userId);
        return code;
    }

    private async Task TryLinkAsync(
        MessengerType messenger, string userId, string code, string lang, CancellationToken ct)
    {
        var owner = await _users.FindActiveByCodeAsync(code, excludeMessenger: messenger, ct);
        if (owner is null)
        {
            await SendAsync(messenger, userId, Localization.Get(lang, "link_invalid"), ct);
            return;
        }

        var ownerMessenger = owner.MessengerType;

        // Записываем связку с обеих сторон.
        await _users.SetLinkedAsync(messenger, userId, owner.UserId, ct);
        await _users.SetLinkedAsync(ownerMessenger, owner.UserId, userId, ct);

        // Коды больше не нужны.
        await _users.ClearCodeAsync(ownerMessenger, owner.UserId, ct);
        await _users.ClearCodeAsync(messenger, userId, ct);

        // Сообщаем об успехе обоим сторонам.
        await SendAsync(messenger, userId, Localization.Get(lang, "link_success"), ct);
        await SendAsync(ownerMessenger, owner.UserId, Localization.Get(owner.Language, "link_success"), ct);

        _logger.LogInformation("Связаны {MessengerA}:{UserA} <-> {MessengerB}:{UserB}.",
            messenger, userId, ownerMessenger, owner.UserId);
    }

    private Task SendAsync(MessengerType messenger, string userId, string text, CancellationToken ct)
    {
        if (_clients.TryGetValue(messenger, out var client))
            return client.SendTextAsync(userId, text, ct);

        _logger.LogWarning("Нет клиента для мессенджера {Messenger}.", messenger);
        return Task.CompletedTask;
    }

    /// <summary>
    /// true, если для этого пользователя уже была принята попытка ввода кода
    /// менее <see cref="CodeAttemptCooldown"/> назад. Не продлевает окно повторными
    /// (отклонёнными) попытками — отсчёт идёт от последней ПРИНЯТОЙ попытки.
    /// </summary>
    private bool IsRateLimited(MessengerType messenger, string userId)
    {
        var key = (messenger, userId);
        var now = DateTimeOffset.UtcNow;
        var limited = false;

        _lastCodeAttempt.AddOrUpdate(key,
            _ => now,
            (_, last) =>
            {
                if (now - last < CodeAttemptCooldown)
                {
                    limited = true;
                    return last;
                }
                return now;
            });

        return limited;
    }

    private static bool IsStartCommand(string text) =>
        text.StartsWith("/start", StringComparison.OrdinalIgnoreCase) ||
        text.Equals("start", StringComparison.OrdinalIgnoreCase);

    private static bool IsSixDigitCode(string text) =>
        text.Length == 6 && text.All(char.IsDigit);
}
