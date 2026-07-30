using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SyncMax.Messengers;
using SyncMax.Models;

namespace SyncMax.Services.VideoEmbed;

/// <summary>
/// Опциональное дополнение к обычной пересылке (см. <see cref="MessageRelayService"/>): если в
/// сообщении есть ссылка на YouTube-видео/Shorts, скачивает само видео через внешний сервис
/// (<see cref="VideoEmbedClient"/>) и публикует его отдельным сообщением в оба чата связки —
/// и в тот, откуда пришло сообщение, и в связанный, — чтобы по обе стороны видео можно было
/// посмотреть не переходя по ссылке.
///
/// Запускается в фоне, не блокируя основную пересылку: скачивание может занимать от секунд
/// до нескольких минут (очередь на сервисе, скачивание, конвертация), и ждать этого на
/// горячем пути (последовательная обработка апдейтов в *BotService) значило бы задерживать
/// обработку следующих сообщений того же бота. Поэтому <see cref="TryRelayInBackground"/>
/// только проверяет условия и синхронно возвращает управление, а вся работа уходит в
/// отдельную задачу; любая ошибка там — не более чем потерянное необязательное сообщение,
/// наружу не пробрасывается.
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
    /// они выполнены, запускает обработку в фоне. Вызывать только для сообщений, для которых
    /// обычная пересылка уже разрешена (модерацией не заблокирована, направление связки
    /// проверено вызывающим) — этот метод сам направление не проверяет.
    /// </summary>
    public void TryRelayInBackground(
        ChatLink link, MessengerType source, string sourceChatId, string? senderName, FormattedText originalCaption)
    {
        if (!link.VideoEmbedEnabled || !_client.IsConfigured)
            return;

        var url = YouTubeLinkExtractor.TryFind(originalCaption);
        if (url is null)
            return;

        _ = RunAsync(link, source, sourceChatId, senderName, url, _appStopping);
    }

    private async Task RunAsync(
        ChatLink link, MessengerType source, string sourceChatId, string? senderName, string url, CancellationToken ct)
    {
        string? filePath = null;
        try
        {
            filePath = await _client.TryDownloadAsync(url, ct);
            if (filePath is null)
                return;

            var targetMessenger = source == MessengerType.Max ? MessengerType.Telegram : MessengerType.Max;
            var targetChatId = source == MessengerType.Max ? link.TgChatId : link.MaxChatId;
            var message = BuildMessage(source, senderName, url, filePath);

            if (_clients.TryGetValue(source, out var sourceClient))
                await sourceClient.SendChatMessageAsync(sourceChatId, message, ct);

            if (_clients.TryGetValue(targetMessenger, out var targetClient))
                await targetClient.SendChatMessageAsync(targetChatId, message, ct);

            _logger.LogInformation("Видео-функция: ссылка {Url} из {Messenger}:{ChatId} разослана в оба чата связки {LinkId}.",
                url, source, sourceChatId, link.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Видео-функция: не удалось разослать видео по ссылке {Url}.", url);
        }
        finally
        {
            TempFiles.TryDelete(filePath);
        }
    }

    private static RelayMessage BuildMessage(MessengerType source, string? senderName, string url, string filePath)
    {
        var sourceTag = source == MessengerType.Max ? "MAX" : "TG";
        var header = string.IsNullOrWhiteSpace(senderName)
            ? $"🎬 Видео по ссылке · (из {sourceTag})"
            : $"🎬 Видео по ссылке от {senderName} · (из {sourceTag})";
        var text = $"{header}\n{url}";

        return new RelayMessage
        {
            Caption = new FormattedText
            {
                Text = text,
                Spans = [new TextSpan { Kind = TextSpanKind.Link, Offset = header.Length + 1, Length = url.Length, Url = url }]
            },
            Attachments = [new MediaAttachment { Kind = MediaKind.Video, FilePath = filePath }]
        };
    }
}
