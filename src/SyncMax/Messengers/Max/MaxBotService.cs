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
        if (message.Recipient is { ChatId: { } chatId2, ChatType: ChatKindExtensions.ChatCode or ChatKindExtensions.ChannelCode } recipient
            && message.Body?.Text is { } groupText)
        {
            if (groupText.Trim().ToLower() == "/link")
            {
                var chatKind = recipient.ChatType == ChatKindExtensions.ChannelCode ? ChatKind.Channel : ChatKind.Chat;
                var chat = await _client.GetChatOrNullAsync(chatId2, ct);
                await _chatLinking.HandleRepostAsync(MessengerType.Max, userId2, chatId2.ToString(), chatKind, chat?.Title, ct);
            }
            else if (message.Sender.IsBot != true)
            {
                // Сообщения от ботов (не только от себя самого, но и от любых других
                // ботов в чате) не пересылаем — это не пользовательский контент.
                await _relay.RelayTextAsync(MessengerType.Max, chatId2.ToString(), message.Sender.Name, groupText, ct);
            }
        }
    }
}
