using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncMax.Messengers;
using SyncMax.Models;

namespace SyncMax.Services.VideoEmbed;

/// <summary>
/// Опциональное дополнение к обычной пересылке (см. <see cref="MessageRelayService"/>): если в
/// сообщении есть ссылка на YouTube-видео/Shorts, скачивает само видео через внешний сервис
/// (<see cref="VideoEmbedClient"/>) и публикует его в оба чата связки — и в тот, откуда пришло
/// сообщение, и в связанный, — чтобы по обе стороны видео можно было посмотреть не переходя
/// по ссылке.
///
/// Порядок в чате важен и задан жёстко: сначала уходит сам репост оригинала (его этот сервис
/// не касается — <see cref="TryRelayInBackground"/> вызывается уже ПОСЛЕ отправки), затем
/// сразу же — статусное сообщение, которое занимает место в ленте и показывает ход загрузки
/// («в очереди», «скачивается 45%»), и в него же по готовности встаёт само видео
/// (<see cref="VideoEmbedStatusBoard"/>). Публиковать видео «когда скачается» отдельным
/// сообщением нельзя: за минуты загрузки чат уходит вперёд, и видео теряет связь с ссылкой.
///
/// Вся работа идёт в фоне, не блокируя пересылку: скачивание может занимать от секунд до
/// нескольких минут (очередь на сервисе, скачивание, конвертация), и ждать этого на горячем
/// пути (последовательная обработка апдейтов в *BotService) значило бы задерживать обработку
/// следующих сообщений того же бота. Поэтому <see cref="TryRelayInBackground"/> только
/// проверяет условия и уводит всё остальное — включая отправку статусного сообщения — в
/// <see cref="Task.Run(Func{Task}, CancellationToken)"/>, чтобы на вызывающем потоке не
/// осталось даже начала запроса; любая ошибка там — не более чем потерянное необязательное
/// сообщение, наружу не пробрасывается.
///
/// Фоновая задача намеренно берёт токен отмены из <see cref="IHostApplicationLifetime"/>, а не
/// токен вызывающего: в режиме Webhook это <c>HttpContext.RequestAborted</c>, который отменяется
/// вскоре после завершения HTTP-ответа на апдейт — задача, ещё скачивающая видео (минуты),
/// оборвалась бы почти сразу же после старта. Переживать нужно только остановку приложения.
/// </summary>
public sealed class VideoEmbedRelayService
{
    private readonly VideoEmbedClient _client;
    private readonly IReadOnlyDictionary<MessengerType, IMessengerApiClient> _clients;
    private readonly CancellationToken _appStopping;
    private readonly ILogger<VideoEmbedRelayService> _logger;

    public VideoEmbedRelayService(
        VideoEmbedClient client, IEnumerable<IMessengerApiClient> clients,
        IHostApplicationLifetime lifetime, ILogger<VideoEmbedRelayService> logger)
    {
        _client = client;
        _clients = clients.ToDictionary(c => c.Messenger);
        _appStopping = lifetime.ApplicationStopping;
        _logger = logger;
    }

    /// <summary>
    /// Проверяет условия (связка допускает фичу, сервис настроен, в тексте есть ссылка) и, если
    /// они выполнены, запускает обработку в фоне. Вызывать только ПОСЛЕ того, как сам оригинал
    /// уже переслан, и только для сообщений, для которых пересылка разрешена (модерацией не
    /// заблокирована, направление связки проверено вызывающим) — этот метод сам направление
    /// не проверяет.
    /// </summary>
    public void TryRelayInBackground(
        ChatLink link, MessengerType source, string sourceChatId, string? senderName, FormattedText originalCaption)
    {
        if (!link.VideoEmbedEnabled || !_client.IsConfigured)
            return;

        var url = YouTubeLinkExtractor.TryFind(originalCaption);
        if (url is null)
            return;

        _ = Task.Run(() => RunAsync(link, source, sourceChatId, senderName, url, _appStopping), CancellationToken.None);
    }

    private async Task RunAsync(
        ChatLink link, MessengerType source, string sourceChatId, string? senderName, string url, CancellationToken ct)
    {
        string? filePath = null;
        VideoEmbedStatusBoard? board = null;
        try
        {
            var caption = VideoEmbedTexts.Caption(source, senderName, url);
            board = await VideoEmbedStatusBoard.PostAsync(
                Targets(link, source, sourceChatId), caption, VideoEmbedTexts.QueuedLine, _logger, ct);

            var result = await _client.TryDownloadAsync(
                url, (progress, token) => board.ShowAsync(VideoEmbedTexts.StatusLine(progress), token), ct);

            if (result.FilePath is not { } path)
            {
                await board.FailAsync(result.ErrorCode, ct);
                _logger.LogWarning("Видео-функция: видео по ссылке {Url} не получено ({Code}).", url, result.ErrorCode);
                return;
            }

            filePath = path;
            var delivered = await board.PublishAsync(new MediaAttachment { Kind = MediaKind.Video, FilePath = path }, ct);

            if (delivered)
                _logger.LogInformation("Видео-функция: ссылка {Url} из {Messenger}:{ChatId} разослана в оба чата связки {LinkId}.",
                    url, source, sourceChatId, link.Id);
            else
                _logger.LogWarning("Видео-функция: видео по ссылке {Url} (связка {LinkId}) дошло не во все чаты — подробности выше.",
                    url, link.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Видео-функция: не удалось разослать видео по ссылке {Url}.", url);

            // Статусное сообщение уже висит в чатах — оставлять его с «скачивается…» нельзя.
            if (board is not null)
                await board.FailAsync(VideoEmbedErrors.ServiceError, CancellationToken.None);
        }
        finally
        {
            TempFiles.TryDelete(filePath);
        }
    }

    /// <summary>Оба чата связки: тот, откуда пришла ссылка, и связанный с ним.</summary>
    private IEnumerable<(IMessengerApiClient Client, string ChatId)> Targets(
        ChatLink link, MessengerType source, string sourceChatId)
    {
        var targetMessenger = source == MessengerType.Max ? MessengerType.Telegram : MessengerType.Max;
        var targetChatId = source == MessengerType.Max ? link.TgChatId : link.MaxChatId;

        if (_clients.TryGetValue(source, out var sourceClient))
            yield return (sourceClient, sourceChatId);

        if (_clients.TryGetValue(targetMessenger, out var targetClient))
            yield return (targetClient, targetChatId);
    }
}
