using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;
using SyncMax.Models;
using Telegram.Bot;
using TelegramUser = Telegram.Bot.Types.User;

namespace SyncMax.Messengers.Telegram;

/// <summary>
/// Тонкий клиент над Telegram Bot API. Владеет экземпляром <see cref="ITelegramBotClient"/>
/// и отдаёт его наружу через <see cref="BotClient"/> — он нужен <see cref="TelegramBotService"/>
/// для long polling входящих обновлений. Реализует <see cref="IMessengerApiClient"/> —
/// общий контракт отправки для LinkingService.
/// </summary>
public sealed class TelegramApiClient : IMessengerApiClient
{
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramApiClient> _logger;
    private readonly Lazy<ITelegramBotClient?> _bot;

    public TelegramApiClient(IOptions<TelegramOptions> options, ILogger<TelegramApiClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _bot = new Lazy<ITelegramBotClient?>(
            () => IsConfigured ? new TelegramBotClient(_options.Token) : null);
    }

    public MessengerType Messenger => MessengerType.Telegram;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Token);

    /// <summary>Низкоуровневый клиент библиотеки Telegram.Bot — для long polling в TelegramBotService.</summary>
    public ITelegramBotClient? BotClient => _bot.Value;

    public async Task SendTextAsync(string userId, string text, CancellationToken ct)
    {
        if (BotClient is not { } bot)
        {
            _logger.LogWarning("Telegram: клиент не инициализирован, сообщение не отправлено.");
            return;
        }

        await bot.SendMessage(long.Parse(userId), text, cancellationToken: ct);
    }

    /// <summary>
    /// В Telegram Bot API id пользователя и id чата/группы — одно адресное пространство
    /// (chat_id), поэтому отправка в группу ничем не отличается от личного сообщения.
    /// </summary>
    public Task SendChatTextAsync(string chatId, string text, CancellationToken ct) =>
        SendTextAsync(chatId, text, ct);

    /// <summary>
    /// Единая точка формирования читаемого имени пользователя Telegram: "{FirstName} {LastName}",
    /// если одно из имён пустое — только второе, если оба пустых — Username.
    /// </summary>
    public static string? BuildDisplayName(TelegramUser? user)
    {
        if (user is null)
            return null;

        var hasFirstName = !string.IsNullOrWhiteSpace(user.FirstName);
        var hasLastName = !string.IsNullOrWhiteSpace(user.LastName);

        var fullName = (hasFirstName, hasLastName) switch
        {
            (true, true) => $"{user.FirstName} {user.LastName}",
            (true, false) => user.FirstName,
            (false, true) => user.LastName,
            (false, false) => null
        };

        return fullName ?? user.Username;
    }
}
