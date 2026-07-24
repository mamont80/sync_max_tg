using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncMax.Data.Repositories;
using SyncMax.Models;
using SyncMax.Services;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace SyncMax.Messengers.Telegram;

/// <summary>
/// Фоновый сервис Telegram-бота: long polling входящих сообщений через
/// <see cref="TelegramApiClient"/> и передача их в <see cref="LinkingService"/>.
/// Отправка ответов — забота клиента, этот класс только принимает и разбирает апдейты.
/// </summary>
public sealed class TelegramBotService : BackgroundService
{
    // Отладочный дамп входящих апдейтов в человекочитаемом JSON.
    private static readonly JsonSerializerOptions DebugJson = new() { WriteIndented = true };

    private readonly TelegramApiClient _client;
    private readonly LinkingService _linking;
    private readonly ChatLinkingService _chatLinking;
    private readonly SystemCommandService _systemCommands;
    private readonly MessageRelayService _relay;
    private readonly ILogger<TelegramBotService> _logger;
    private string _botId = string.Empty;

    public TelegramBotService(
        TelegramApiClient client, LinkingService linking, ChatLinkingService chatLinking,
        SystemCommandService systemCommands, MessageRelayService relay, ILogger<TelegramBotService> logger)
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
        if (_client.BotClient is not { } bot)
        {
            _logger.LogWarning("Telegram: токен не задан (Telegram:Token). Бот отключён.");
            return;
        }

        var me = await bot.GetMe(ct);
        _botId = me.Id.ToString();
        _logger.LogInformation("Telegram-бот @{Username} запущен.", me.Username);

        var offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // allowedUpdates: [] -> получаем ВСЕ типы апдейтов (для отладки), а не только сообщения.
                var updates = await bot.GetUpdates(
                    offset: offset,
                    timeout: 30,
                    allowedUpdates: [UpdateType.Message, UpdateType.MyChatMember, UpdateType.CallbackQuery, UpdateType.ChannelPost],
                    cancellationToken: ct);

                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    LogIncomingUpdate(update);
                    await DoUpdate(update, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram: ошибка опроса, повтор через 3 c.");
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }
    /// <summary>
    /// Обработка отдельного обновления
    /// </summary>
    private async Task DoUpdate(Update update, CancellationToken ct)
    {
        //Добавление бота в группу/канал
        if (update.Type == UpdateType.MyChatMember)
        {
            //if (update.MyChatMember.Chat)
            var userId = update.MyChatMember?.From?.Id.ToString();
            var newStatus = update.MyChatMember?.NewChatMember.Status;
            var chatId = update.MyChatMember?.Chat?.Id.ToString();
            if (newStatus == ChatMemberStatus.Member || newStatus == ChatMemberStatus.Administrator)
            { 
                if (!string.IsNullOrEmpty(userId) && !string.IsNullOrEmpty(chatId))
                {
                    await _chatLinking.HandleAddBotToGroup(
                        userId,
                        newStatus == ChatMemberStatus.Administrator ? MemberType.Administrator : MemberType.Member,
                        chatId,
                        MessengerType.Telegram,
                        ChatKind.Chat,
                        update.MyChatMember?.Chat.Title,
                        ct);
                }
            }
        }
        var message = update.Message;
        if (message is null)
            return;

        // Эхо собственного сообщения бота (например, отправленного при пересылке в эту же
        // группу) — не обрабатываем повторно, иначе связка с обеих сторон зациклится.
        if (!string.IsNullOrEmpty(_botId) && message.From?.Id.ToString() == _botId)
            return;

        //Это сообщение лично боту
        if (message.Chat.Type == ChatType.Private)
        {
            if (message.Text is not { } text)
                return;
            var chatId = message.Chat.Id.ToString();
            if (await _systemCommands.TryHandleAsync(MessengerType.Telegram, chatId, text, ct))
                return;

            var name = TelegramApiClient.BuildDisplayName(message.From);
            await _linking.HandleAsync(MessengerType.Telegram, chatId, name, text, ct);
        }
        //Это сообщение в группу
        if (message.Chat.Type == ChatType.Group || message.Chat.Type == ChatType.Supergroup)
        {
            var chatId = message.Chat.Id.ToString();
            var command = message.Text?.Trim().ToLower();
            if (command == "/link")
            {
                var userId = message.From?.Id.ToString();
                var chatTitle = message.Chat.Title;
                if (!string.IsNullOrEmpty(userId))
                {
                    await _chatLinking.HandleRepostAsync(MessengerType.Telegram, userId, chatId, ChatKind.Chat, chatTitle, ct);
                }
                else _logger.LogWarning($"На команде /link пользователь не определён ChatTitle:{message.Chat.Title}");
                return;
            }

            // Сообщения от ботов (не только от себя самого, но и от любых других
            // ботов в группе) не пересылаем — это не пользовательский контент.
            if (message.From?.IsBot == true)
                return;

            var relay = await BuildRelayMessageAsync(message, ct);
            if (relay.IsEmpty)
                return;

            var senderName = TelegramApiClient.BuildDisplayName(message.From);
            await _relay.RelayMessageAsync(MessengerType.Telegram, chatId, senderName, relay, ct);
        }
    }

    /// <summary>
    /// Строит платформо-независимое <see cref="RelayMessage"/> из сообщения Telegram: подпись
    /// (текст или caption + entities) и, если есть, одно медиа-вложение, скачанное во временный
    /// файл. Если скачать вложение не удалось — оно опускается (текст всё равно перешлётся).
    /// </summary>
    private async Task<RelayMessage> BuildRelayMessageAsync(Message message, CancellationToken ct)
    {
        var text = message.Text ?? message.Caption ?? string.Empty;
        var entities = message.Entities ?? message.CaptionEntities;
        var caption = TelegramFormatting.ToFormattedText(text, entities);

        var attachment = await TryDownloadMediaAsync(message, ct);
        return new RelayMessage
        {
            Caption = caption,
            Attachments = attachment is null ? [] : [attachment]
        };
    }

    /// <summary>Определяет тип медиа в сообщении, скачивает его во временный файл и возвращает вложение (или null).</summary>
    private async Task<MediaAttachment?> TryDownloadMediaAsync(Message message, CancellationToken ct)
    {
        // Порядок важен: анимация в Telegram дублируется и в Document, поэтому проверяется раньше.
        if (message.Photo is { Length: > 0 } photo)
            return await MakeAttachmentAsync(photo[^1].FileId, MediaKind.Photo, ".jpg", null, "image/jpeg", ct);
        if (message.Animation is { } animation)
            return await MakeAttachmentAsync(animation.FileId, MediaKind.Animation, Ext(animation.FileName, ".mp4"), animation.FileName, animation.MimeType, ct);
        if (message.Video is { } video)
            return await MakeAttachmentAsync(video.FileId, MediaKind.Video, Ext(video.FileName, ".mp4"), video.FileName, video.MimeType, ct);
        if (message.VideoNote is { } videoNote)
            return await MakeAttachmentAsync(videoNote.FileId, MediaKind.VideoNote, ".mp4", null, "video/mp4", ct);
        if (message.Voice is { } voice)
            return await MakeAttachmentAsync(voice.FileId, MediaKind.Voice, ".ogg", null, voice.MimeType ?? "audio/ogg", ct);
        if (message.Audio is { } audio)
            return await MakeAttachmentAsync(audio.FileId, MediaKind.Audio, Ext(audio.FileName, ".mp3"), audio.FileName, audio.MimeType, ct);
        if (message.Document is { } document)
            return await MakeAttachmentAsync(document.FileId, MediaKind.Document, Ext(document.FileName, ".bin"), document.FileName, document.MimeType, ct);
        return null;
    }

    private async Task<MediaAttachment?> MakeAttachmentAsync(
        string fileId, MediaKind kind, string extension, string? fileName, string? mimeType, CancellationToken ct)
    {
        var path = await _client.DownloadFileToTempAsync(fileId, extension, ct);
        if (path is null)
            return null;

        return new MediaAttachment { Kind = kind, FilePath = path, FileName = fileName, MimeType = mimeType };
    }

    /// <summary>Расширение из имени файла, иначе — запасное.</summary>
    private static string Ext(string? fileName, string fallback)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        return string.IsNullOrEmpty(ext) ? fallback : ext;
    }

    /// <summary>Пишет в лог полный входящий апдейт (сырой JSON) — для отладки.</summary>
    private void LogIncomingUpdate(Update update)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(update, DebugJson);
        }
        catch (Exception ex)
        {
            json = $"(не удалось сериализовать: {ex.Message})";
        }

        _logger.LogInformation("[Telegram] RAW апдейт #{Id} type={Type}:\n{Json}",
            update.Id, update.Type, json);
    }

}
