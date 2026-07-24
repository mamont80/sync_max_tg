using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;
using SyncMax.Models;
using SyncMax.Services;

namespace SyncMax.Messengers.Max;

/// <summary>
/// Тонкий HTTP-клиент над Bot API MAX. Токен передаётся как query-параметр
/// access_token. Базовый URL берётся из конфигурации (Max:ApiBaseUrl).
/// Реализует <see cref="IMessengerApiClient"/> — общий контракт отправки для LinkingService.
/// </summary>
public sealed class MaxApiClient : IMessengerApiClient
{
    /// <summary>Имя HttpClient для скачивания/загрузки медиа (тот же TLS-handler, но без Authorization).</summary>
    public const string MediaHttpClientName = "max-media";

    // Загруженное видео/аудио сервер обрабатывает асинхронно: пока не готово, POST /messages
    // отдаёт 400. Пробуем несколько раз с паузой.
    private const int SendAttempts = 6;
    private static readonly TimeSpan SendRetryDelay = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MaxOptions _options;
    private readonly MediaOptions _media;
    private readonly MediaConverter _converter;
    private readonly ILogger<MaxApiClient> _logger;

    public MaxApiClient(
        HttpClient http,
        IHttpClientFactory httpFactory,
        IOptions<MaxOptions> options,
        IOptions<MediaOptions> media,
        MediaConverter converter,
        ILogger<MaxApiClient> logger)
    {
        _http = http;
        _httpFactory = httpFactory;
        _options = options.Value;
        _media = media.Value;
        _converter = converter;
        _logger = logger;

        // Токен MAX Bot API передаётся заголовком Authorization (без "Bearer"),
        // передача через query-параметр access_token отключена платформой.
        if (IsConfigured)
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", _options.Token);
    }

    public MessengerType Messenger => MessengerType.Max;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Token);

    /// <summary>Long polling входящих обновлений.</summary>
    public async Task<MaxUpdatesResponse?> GetUpdatesAsync(long? marker, CancellationToken ct)
    {
        var url = $"{BaseUrl}/updates?timeout=30&limit=100";
        if (marker.HasValue)
            url += $"&marker={marker.Value}";

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(ct);

        var encoding = response.Content.Headers.ContentEncoding.Count > 0
            ? string.Join(",", response.Content.Headers.ContentEncoding)
            : "-";
        _logger.LogInformation("[MAX] RAW /updates ({Len} симв., Content-Length={CL}, Content-Encoding={CE}):\n{Raw}",
            raw.Length, response.Content.Headers.ContentLength, encoding, raw);
        // Дублируем сырой ответ в файл рядом с бинарником — консольные логи у пользователя
        // не сохраняются, а полный сырой JSON нужен для разбора реального формата (напр. аудио).
        await DumpRawAsync(raw, encoding, ct);

        return string.IsNullOrWhiteSpace(raw)
            ? null
            : JsonSerializer.Deserialize<MaxUpdatesResponse>(raw);
    }

    private static async Task DumpRawAsync(string raw, string encoding, CancellationToken ct)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "max-updates-raw.log");
            await File.AppendAllTextAsync(path, $"{DateTimeOffset.Now:O} len={raw.Length} enc={encoding}\n{raw}\n\n", ct);
        }
        catch
        {
            // Диагностический дамп — не критично, если не удалось записать.
        }
    }

    /// <summary>Отправка текстового сообщения пользователю по его open_id.</summary>
    public async Task SendTextAsync(string userId, string text, CancellationToken ct)
    {
        var url = $"{BaseUrl}/messages?user_id={userId}";
        using var response = await _http.PostAsJsonAsync(url, new MaxSendMessageRequest { Text = text }, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Отправка сообщения (текст и/или медиа) в чат/канал по chat_id. Текст уходит как
    /// markdown (format=markdown) либо plain. Каждое вложение загружается по upload-flow MAX
    /// и прикрепляется своим типом; подпись (с «шапкой») ставится только на первое вложение.
    /// Если родная отправка вложения не удалась — откат на отправку файлом (type=file).
    /// </summary>
    public async Task<string?> SendChatMessageAsync(string chatId, RelayMessage message, CancellationToken ct)
    {
        if (!message.HasMedia)
            return await SendMessageBodyAsync(chatId, message.Caption, attachments: null, message.ReplyToTargetMessageId, ct);

        string? firstId = null;
        var captionConsumed = false;
        foreach (var att in message.Attachments)
        {
            var caption = captionConsumed ? FormattedText.Plain(string.Empty) : message.Caption;
            var replyMid = captionConsumed ? null : message.ReplyToTargetMessageId;
            var mid = await SendOneAttachmentAsync(chatId, att, caption, replyMid, ct);
            firstId ??= mid;
            captionConsumed = true;
        }
        return firstId;
    }

    private async Task<string?> SendOneAttachmentAsync(string chatId, MediaAttachment att, FormattedText caption, string? replyMid, CancellationToken ct)
    {
        var maxType = MapType(att.Kind);
        var path = att.FilePath;
        var fileName = att.FileName;
        var mime = att.MimeType;
        string? converted = null;

        // Разрешена конвертация аудио: голос (ogg/opus) → mp3 для совместимости с MAX.
        if (att.Kind == MediaKind.Voice && _converter.IsAvailable)
        {
            converted = await _converter.TryConvertAudioToMp3Async(att.FilePath, ct);
            if (converted is not null)
            {
                path = converted;
                mime = "audio/mpeg";
                fileName = Path.ChangeExtension(fileName ?? Path.GetFileName(att.FilePath), ".mp3");
            }
        }

        try
        {
            try
            {
                var attach = await UploadAsync(maxType, path, fileName, mime, ct);
                return await SendMessageBodyAsync(chatId, caption, [attach], replyMid, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MAX: не удалось отправить {Kind} как {MaxType}, откат на файл.", att.Kind, maxType);
                var attach = await UploadAsync("file", att.FilePath, att.FileName ?? Path.GetFileName(att.FilePath), att.MimeType, ct);
                return await SendMessageBodyAsync(chatId, caption, [attach], replyMid, ct);
            }
        }
        finally
        {
            TempFiles.TryDelete(converted);
        }
    }

    /// <summary>Полный upload-flow одного файла: получить upload url → загрузить → собрать AttachmentRequest.</summary>
    private async Task<MaxAttachmentRequest> UploadAsync(string maxType, string path, string? fileName, string? mime, CancellationToken ct)
    {
        var endpoint = await GetUploadUrlAsync(maxType, ct);
        if (endpoint?.Url is not { Length: > 0 } uploadUrl)
            throw new InvalidOperationException($"MAX: не получен upload url для type={maxType}.");

        var uploaded = await UploadBinaryAsync(uploadUrl, path, fileName, mime, ct);

        var payload = new MaxAttachmentRequestPayload();
        if (maxType == "image")
        {
            // Для изображений токены приходят в теле ответа загрузки (photos-map).
            if (uploaded?.Photos is { Count: > 0 } photos)
                payload.Photos = photos;
            else
                payload.Token = uploaded?.Token ?? endpoint.Token;
        }
        else
        {
            // Для video/audio токен обычно приходит уже в ответе /uploads, для file — после загрузки.
            payload.Token = endpoint.Token ?? uploaded?.Token;
        }

        if (payload.Token is null && payload.Photos is null)
            throw new InvalidOperationException($"MAX: не удалось получить token вложения type={maxType}.");

        return new MaxAttachmentRequest { Type = maxType, Payload = payload };
    }

    private async Task<MaxUploadEndpoint?> GetUploadUrlAsync(string type, CancellationToken ct)
    {
        var url = $"{BaseUrl}/uploads?type={type}";
        using var response = await _http.PostAsync(url, content: null, ct);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<MaxUploadEndpoint>(raw);
    }

    private async Task<MaxUploadResult?> UploadBinaryAsync(string uploadUrl, string path, string? fileName, string? mime, CancellationToken ct)
    {
        // Отдельный клиент: тот же TLS-handler (Russian CA для CDN), но без заголовка Authorization.
        var media = _httpFactory.CreateClient(MediaHttpClientName);

        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(path);
        using var fileContent = new StreamContent(fs);
        if (MediaTypeHeaderValue.TryParse(mime ?? "application/octet-stream", out var contentType))
            fileContent.Headers.ContentType = contentType;
        // Имя поля формы — "data" (см. документацию загрузки MAX).
        form.Add(fileContent, "data", fileName ?? Path.GetFileName(path));

        using var response = await media.PostAsync(uploadUrl, form, ct);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(raw) ? new MaxUploadResult() : JsonSerializer.Deserialize<MaxUploadResult>(raw);
    }

    /// <summary>
    /// Отправка тела сообщения (текст+вложения, опционально ответ). Ретраи на 400, пока медиа
    /// обрабатывается сервером. Возвращает mid отправленного сообщения (для карты ответов) или null.
    /// </summary>
    private async Task<string?> SendMessageBodyAsync(
        string chatId, FormattedText caption, List<MaxAttachmentRequest>? attachments, string? replyMid, CancellationToken ct)
    {
        var (text, format) = MaxFormatting.ToRequestText(caption);
        var body = new MaxSendMessageRequest
        {
            Text = string.IsNullOrEmpty(text) ? null : text,
            Format = format,
            Attachments = attachments,
            Link = string.IsNullOrEmpty(replyMid) ? null : new MaxNewMessageLink { Type = "reply", Mid = replyMid }
        };

        var url = $"{BaseUrl}/messages?chat_id={chatId}";
        for (var attempt = 1; ; attempt++)
        {
            using var response = await _http.PostAsJsonAsync(url, body, ct);
            if (response.IsSuccessStatusCode)
            {
                var ok = await response.Content.ReadAsStringAsync(ct);
                return string.IsNullOrWhiteSpace(ok)
                    ? null
                    : JsonSerializer.Deserialize<MaxSendMessageResponse>(ok)?.Message?.Body?.Mid;
            }

            // 400 при наличии вложений — вероятно, файл ещё не обработан. Ждём и повторяем.
            if ((int)response.StatusCode == 400 && attachments is { Count: > 0 } && attempt < SendAttempts)
            {
                await Task.Delay(SendRetryDelay, ct);
                continue;
            }

            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("MAX: отправка сообщения не удалась ({Status}): {Body}", response.StatusCode, err);
            response.EnsureSuccessStatusCode();
            return null;
        }
    }

    /// <summary>
    /// Редактирует сообщение MAX (PUT /messages?message_id=). Чтобы правка подписи не стёрла
    /// медиа, дотягиваем текущие вложения сообщения (GET) и переотправляем их по token.
    /// Ошибки (в т.ч. истёкшее окно редактирования) логируются, не пробрасываются.
    /// </summary>
    public async Task EditChatMessageAsync(string chatId, string messageId, FormattedText caption, bool isMediaCaption, CancellationToken ct)
    {
        var (text, format) = MaxFormatting.ToRequestText(caption);
        var body = new MaxSendMessageRequest
        {
            Text = string.IsNullOrEmpty(text) ? null : text,
            Format = format,
            Attachments = await KeepExistingAttachmentsAsync(messageId, ct)
        };

        var url = $"{BaseUrl}/messages?message_id={messageId}";
        try
        {
            using var response = await _http.PutAsJsonAsync(url, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("MAX: не удалось отредактировать сообщение {Mid} ({Status}): {Body}", messageId, response.StatusCode, err);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MAX: ошибка редактирования сообщения {Mid}.", messageId);
        }
    }

    public async Task DeleteChatMessageAsync(string chatId, string messageId, CancellationToken ct)
    {
        var url = $"{BaseUrl}/messages?message_id={messageId}";
        try
        {
            using var response = await _http.DeleteAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("MAX: не удалось удалить сообщение {Mid} ({Status}).", messageId, response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MAX: ошибка удаления сообщения {Mid}.", messageId);
        }
    }

    /// <summary>
    /// Возвращает вложения существующего сообщения как запросы на переотправку (по token) —
    /// чтобы при правке текста медиа не потерялось. null/пусто, если вложений нет или GET не удался.
    /// </summary>
    private async Task<List<MaxAttachmentRequest>?> KeepExistingAttachmentsAsync(string messageId, CancellationToken ct)
    {
        try
        {
            var url = $"{BaseUrl}/messages?message_ids={messageId}";
            var raw = await _http.GetStringAsync(url, ct);
            var message = string.IsNullOrWhiteSpace(raw)
                ? null
                : JsonSerializer.Deserialize<MaxMessagesResponse>(raw)?.Messages?.FirstOrDefault();

            var kept = message?.Body?.Attachments?
                .Where(a => a.Type is "image" or "video" or "audio" or "file" && a.Payload?.Token is { Length: > 0 })
                .Select(a => new MaxAttachmentRequest { Type = a.Type!, Payload = new MaxAttachmentRequestPayload { Token = a.Payload!.Token } })
                .ToList();

            return kept is { Count: > 0 } ? kept : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MAX: не удалось получить вложения сообщения {Mid} для сохранения при правке.", messageId);
            return null;
        }
    }

    /// <summary>
    /// Скачивает вложение MAX по прямому url во временный файл. null при ошибке/превышении лимита.
    /// Сначала пробуем без авторизации (так отдаётся публичный CDN, напр. изображения); если не
    /// вышло — повторяем с заголовком Authorization API (url аудио/видео/файлов может требовать токен).
    /// </summary>
    public async Task<string?> DownloadUrlToTempAsync(string mediaUrl, string? extension, CancellationToken ct)
    {
        var path = TempFiles.NewPath(extension);
        var maxBytes = (long)_media.MaxFileMegabytes * 1024 * 1024;

        if (await TryDownloadAsync(_httpFactory.CreateClient(MediaHttpClientName), mediaUrl, path, maxBytes, ct))
            return path;

        _logger.LogInformation("MAX: повтор скачивания {Url} с авторизацией API.", mediaUrl);
        if (await TryDownloadAsync(_http, mediaUrl, path, maxBytes, ct))
            return path;

        TempFiles.TryDelete(path);
        return null;
    }

    private async Task<bool> TryDownloadAsync(HttpClient client, string url, string path, long maxBytes, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MAX: скачивание {Url} вернуло {Status}.", url, (int)response.StatusCode);
                return false;
            }

            if (response.Content.Headers.ContentLength is { } len && len > maxBytes)
            {
                _logger.LogWarning("MAX: вложение {Url} больше лимита ({Len} байт) — пропущено.", url, len);
                return false;
            }

            await using var fs = File.Create(path);
            await response.Content.CopyToAsync(fs, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MAX: не удалось скачать {Url}.", url);
            return false;
        }
    }

    private static string MapType(MediaKind kind) => kind switch
    {
        MediaKind.Photo => "image",
        MediaKind.Video or MediaKind.Animation or MediaKind.VideoNote => "video",
        MediaKind.Voice or MediaKind.Audio => "audio",
        _ => "file"
    };

    /// <summary>
    /// Подписывает бота на webhook: MAX начнёт слать обновления POST-запросом на <paramref name="url"/>
    /// вместо необходимости их вычитывать через /updates. Формат эндпоинта — по аналогии с
    /// подпиской Bot API (см. примечание в MaxModels.cs); если у реального API отличается —
    /// правится только этот метод.
    /// </summary>
    public async Task SubscribeWebhookAsync(string url, CancellationToken ct)
    {
        var requestUrl = $"{BaseUrl}/subscriptions";
        using var response = await _http.PostAsJsonAsync(requestUrl, new MaxSubscribeRequest { Url = url }, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("MAX: не удалось подписаться на webhook {Url} ({Status}): {Body}", url, response.StatusCode, err);
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>Отписывает ранее зарегистрированный webhook (используется при переключении обратно на long polling).</summary>
    public async Task UnsubscribeWebhookAsync(string url, CancellationToken ct)
    {
        try
        {
            var requestUrl = $"{BaseUrl}/subscriptions?url={Uri.EscapeDataString(url)}";
            using var response = await _http.DeleteAsync(requestUrl, ct);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning("MAX: не удалось отписаться от webhook {Url} ({Status}).", url, response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MAX: ошибка отписки от webhook {Url}.", url);
        }
    }

    /// <summary>Информация о самом боте (свой user_id) — нужна, чтобы не пересылать эхо собственных сообщений.</summary>
    public async Task<MaxUser?> GetMeAsync(CancellationToken ct)
    {
        var url = $"{BaseUrl}/me";
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[MAX] Не удалось получить информацию о боте: {Status}.", response.StatusCode);
            return null;
        }

        var raw = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<MaxUser>(raw);
    }

    /// <summary>
    /// Информация о групповом чате/канале по его id (для определения типа и названия
    /// при создании связки чатов). null, если чат недоступен боту или запрос не удался —
    /// это ожидаемая ситуация (бот мог никогда не состоять в этом чате), а не сбой опроса.
    /// </summary>
    public async Task<MaxChat?> GetChatOrNullAsync(long chatId, CancellationToken ct)
    {
        var url = $"{BaseUrl}/chats/{chatId}";
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[MAX] Не удалось получить чат {ChatId}: {Status}.", chatId, response.StatusCode);
            return null;
        }

        var raw = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<MaxChat>(raw);
    }

    private string BaseUrl => _options.ApiBaseUrl.TrimEnd('/');
}
