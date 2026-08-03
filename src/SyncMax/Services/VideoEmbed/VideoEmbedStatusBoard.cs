using Microsoft.Extensions.Logging;
using SyncMax.Messengers;
using SyncMax.Models;

namespace SyncMax.Services.VideoEmbed;

/// <summary>
/// Пара статусных сообщений «видео из ссылок» — по одному в каждом чате связки, живущих один
/// цикл загрузки. Сообщение отправляется СРАЗУ после пересылки оригинала и тем самым занимает
/// своё место в ленте: пока видео качается, в нём переписывается строка состояния, а готовое
/// видео встаёт в это же сообщение (<see cref="IMessengerApiClient.TryReplaceChatMessageMediaAsync"/>),
/// а не уезжает вниз за все сообщения, пришедшие за время загрузки.
///
/// Правки идут не чаще <see cref="MinEditInterval"/> и только при смене показанного текста:
/// опрос сервиса частый (секунды), а лимиты на правку есть у обеих платформ. Терминальные
/// состояния (готово, ошибка) показываются без задержки.
///
/// Ошибка в одном чате не мешает второму: каждая операция обёрнута отдельно, и в худшем случае
/// теряется необязательное сообщение.
/// </summary>
public sealed class VideoEmbedStatusBoard
{
    private static readonly TimeSpan MinEditInterval = TimeSpan.FromSeconds(5);

    private readonly List<Slot> _slots;
    private readonly FormattedText _caption;
    private readonly ILogger _logger;
    private string _shownLine;
    private DateTime _lastEdit = DateTime.UtcNow;

    private VideoEmbedStatusBoard(List<Slot> slots, FormattedText caption, string shownLine, ILogger logger)
    {
        _slots = slots;
        _caption = caption;
        _shownLine = shownLine;
        _logger = logger;
    }

    /// <summary>
    /// Отправляет статусное сообщение в каждый из чатов. Чат, в котором отправить не удалось,
    /// остаётся в списке без id: видео туда уйдёт обычным сообщением в конце.
    /// </summary>
    public static async Task<VideoEmbedStatusBoard> PostAsync(
        IEnumerable<(IMessengerApiClient Client, string ChatId)> targets,
        FormattedText caption, string statusLine, ILogger logger, CancellationToken ct)
    {
        // Превью не разворачиваем: ту же ссылку уже развернул пересланный оригинал прямо над
        // этим сообщением, а само оно вот-вот станет видео.
        var payload = new RelayMessage
        {
            Caption = VideoEmbedTexts.WithStatus(caption, statusLine),
            DisableLinkPreview = true
        };
        var slots = new List<Slot>();

        foreach (var (client, chatId) in targets)
        {
            var slot = new Slot(client, chatId);
            try
            {
                slot.MessageId = await client.SendChatMessageAsync(chatId, payload, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Видео-функция: не удалось отправить статус в {Messenger}:{ChatId}.",
                    client.Messenger, chatId);
            }
            slots.Add(slot);
        }

        return new VideoEmbedStatusBoard(slots, caption, statusLine, logger);
    }

    /// <summary>Показать новое состояние (с троттлингом и без повторов одного и того же текста).</summary>
    public Task ShowAsync(string statusLine, CancellationToken ct) => ShowAsync(statusLine, force: false, ct);

    /// <summary>
    /// Заменить статус готовым видео: у сообщений, чьи платформы это позволили, — прямо в них;
    /// остальным приходится отправить видео отдельным сообщением (иначе в чате навсегда
    /// осталось бы «скачивается…»). Возвращает true, только если видео дошло во все чаты; там,
    /// где не дошло, на месте статуса остаётся причина, а не бесконечное «отправляется…».
    /// </summary>
    public async Task<bool> PublishAsync(MediaAttachment media, CancellationToken ct)
    {
        var delivered = true;
        foreach (var slot in _slots)
            delivered &= await PublishToAsync(slot, media, ct);

        return delivered;
    }

    private async Task<bool> PublishToAsync(Slot slot, MediaAttachment media, CancellationToken ct)
    {
        try
        {
            if (slot.MessageId is { } mid
                && await slot.Client.TryReplaceChatMessageMediaAsync(slot.ChatId, mid, media, _caption, ct))
                return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Видео-функция: не удалось заменить статус видео в {Messenger}:{ChatId}.",
                slot.Client.Messenger, slot.ChatId);
        }

        // Запасной путь. Статус удаляем только ПОСЛЕ удачной отправки: если отправить не выйдет,
        // сообщение должно остаться на месте, чтобы было куда написать причину.
        try
        {
            var payload = new RelayMessage { Caption = _caption, Attachments = [media] };
            if (await slot.Client.SendChatMessageAsync(slot.ChatId, payload, ct) is not null)
            {
                if (slot.MessageId is { } stale)
                {
                    await slot.Client.DeleteChatMessageAsync(slot.ChatId, stale, ct);
                    slot.MessageId = null;
                }
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Видео-функция: не удалось отправить видео в {Messenger}:{ChatId}.",
                slot.Client.Messenger, slot.ChatId);
        }

        _logger.LogError("Видео-функция: видео не доставлено в {Messenger}:{ChatId} — ни правкой, ни отправкой.",
            slot.Client.Messenger, slot.ChatId);
        await EditSlotAsync(slot, VideoEmbedTexts.FailureLine(VideoEmbedErrors.SendFailed), ct);
        return false;
    }

    /// <summary>Показать причину, по которой видео не будет, вместо строки состояния.</summary>
    public Task FailAsync(string? errorCode, CancellationToken ct) =>
        ShowAsync(VideoEmbedTexts.FailureLine(errorCode), force: true, ct);

    private async Task ShowAsync(string statusLine, bool force, CancellationToken ct)
    {
        if (statusLine == _shownLine || (!force && DateTime.UtcNow - _lastEdit < MinEditInterval))
            return;

        _shownLine = statusLine;
        _lastEdit = DateTime.UtcNow;

        foreach (var slot in _slots)
            await EditSlotAsync(slot, statusLine, ct);
    }

    /// <summary>Переписать строку состояния в одном чате; чат без статусного сообщения пропускается.</summary>
    private async Task EditSlotAsync(Slot slot, string statusLine, CancellationToken ct)
    {
        if (slot.MessageId is not { } mid)
            return;

        try
        {
            // isMediaCaption: false — до самого конца это обычное текстовое сообщение.
            await slot.Client.EditChatMessageAsync(
                slot.ChatId, mid, VideoEmbedTexts.WithStatus(_caption, statusLine),
                isMediaCaption: false, disableLinkPreview: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Видео-функция: не удалось обновить статус в {Messenger}:{ChatId}.",
                slot.Client.Messenger, slot.ChatId);
        }
    }

    private sealed class Slot(IMessengerApiClient client, string chatId)
    {
        public IMessengerApiClient Client { get; } = client;

        public string ChatId { get; } = chatId;

        /// <summary>Id статусного сообщения; null — отправить его не удалось либо оно уже удалено.</summary>
        public string? MessageId { get; set; }
    }
}
