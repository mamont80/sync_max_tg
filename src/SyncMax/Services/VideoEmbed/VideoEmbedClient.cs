using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;

namespace SyncMax.Services.VideoEmbed;

/// <summary>
/// Клиент внешнего сервиса-загрузчика YouTube-видео (исходники и протокол — см.
/// c:\sync_video\docs\AI_CONTEXT.md). Модель асинхронная: POST ставит задачу в очередь,
/// дальше нужно поллить статус, пока он не станет completed/failed, и только тогда скачивать
/// файл по <c>downloadUrl</c>. Никаких push-уведомлений сервис не даёт.
///
/// Каждый опрос отдаётся наружу колбэком <c>onProgress</c>: пока идёт скачивание, вызывающий
/// показывает этап и проценты в статусном сообщении чата (см. <see cref="VideoEmbedRelayService"/>).
///
/// Любая неудача (сеть, таймаут, ошибка сервиса, невалидная ссылка) возвращается кодом причины
/// в <see cref="VideoDownloadResult.ErrorCode"/> — это опциональное дополнение к пересылке,
/// а не критичная часть, поэтому исключения наружу отсюда никогда не выходят.
/// </summary>
public sealed class VideoEmbedClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly VideoEmbedOptions _options;
    private readonly ILogger<VideoEmbedClient> _logger;

    public VideoEmbedClient(HttpClient http, IOptions<VideoEmbedOptions> options, ILogger<VideoEmbedClient> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_options.BaseUrl))
            http.BaseAddress = new Uri(_options.BaseUrl);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            http.DefaultRequestHeaders.Add("X-API-Key", _options.ApiKey);

        _http = http;
    }

    /// <summary>Задан базовый адрес сервиса — без него функция отключена целиком.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.BaseUrl);

    /// <summary>
    /// Ставит ссылку в очередь на скачивание, дожидается результата и скачивает готовый файл
    /// во временный файл на диске (см. <see cref="TempFiles"/>). При неудаче (видео недоступно,
    /// слишком длинное/большое, очередь переполнена, сервис не ответил, не дождались за
    /// <see cref="VideoEmbedOptions.MaxWaitSeconds"/>) возвращает код причины вместо пути.
    /// <paramref name="onProgress"/> вызывается на каждом опросе статуса; его ошибки
    /// подавляются — показ хода работы не повод бросать саму загрузку.
    /// </summary>
    public async Task<VideoDownloadResult> TryDownloadAsync(
        string url, Func<VideoTaskProgress, CancellationToken, Task>? onProgress, CancellationToken ct)
    {
        if (!IsConfigured)
            return VideoDownloadResult.Failed(VideoEmbedErrors.ServiceError);

        try
        {
            var (taskId, submitError) = await SubmitAsync(url, ct);
            return taskId is null
                ? VideoDownloadResult.Failed(submitError ?? VideoEmbedErrors.ServiceError)
                : await PollAndDownloadAsync(taskId, url, onProgress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Видео-сервис: не удалось обработать ссылку {Url}.", url);
            return VideoDownloadResult.Failed(VideoEmbedErrors.ServiceError);
        }
    }

    private async Task<(string? TaskId, string? ErrorCode)> SubmitAsync(string url, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("/api/v1/tasks", new { url }, JsonOptions, ct);

        if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Видео-сервис: POST /tasks для {Url} вернул {Status}: {Body}", url, response.StatusCode, body);
            return (null, ParseErrorCode(body));
        }

        var created = await response.Content.ReadFromJsonAsync<TaskCreatedResponse>(JsonOptions, ct);
        return (created?.TaskId, created?.TaskId is null ? VideoEmbedErrors.ServiceError : null);
    }

    private async Task<VideoDownloadResult> PollAndDownloadAsync(
        string taskId, string url, Func<VideoTaskProgress, CancellationToken, Task>? onProgress, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_options.MaxWaitSeconds);
        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(pollInterval, ct);

            using var response = await _http.GetAsync($"/api/v1/tasks/{taskId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Видео-сервис: опрос задачи {TaskId} ({Url}) вернул {Status}.",
                    taskId, url, response.StatusCode);
                return VideoDownloadResult.Failed(VideoEmbedErrors.ServiceError);
            }

            var status = await response.Content.ReadFromJsonAsync<TaskStatusResponse>(JsonOptions, ct);
            switch (status?.Status)
            {
                case "completed" when status.DownloadUrl is { Length: > 0 } downloadUrl:
                    await ReportAsync(onProgress, new VideoTaskProgress(VideoTaskStage.Uploading, null), ct);
                    var path = await DownloadFileAsync(downloadUrl, ct);
                    return path is null
                        ? VideoDownloadResult.Failed(VideoEmbedErrors.ServiceError)
                        : VideoDownloadResult.Ok(path);

                // completed без downloadUrl — сервис противоречит сам себе; ждать нечего.
                case "completed":
                    _logger.LogWarning("Видео-сервис: задача {TaskId} ({Url}) завершена без downloadUrl.", taskId, url);
                    return VideoDownloadResult.Failed(VideoEmbedErrors.ServiceError);

                case "failed":
                    _logger.LogWarning("Видео-сервис: задача {TaskId} ({Url}) завершилась ошибкой {Code}: {Message}",
                        taskId, url, status.Error?.Code, status.Error?.Message);
                    return VideoDownloadResult.Failed(status.Error?.Code ?? VideoEmbedErrors.ServiceError);

                default:
                    // queued/running — продолжаем поллинг, попутно отдавая этап наружу.
                    await ReportAsync(onProgress, ToProgress(status), ct);
                    break;
            }
        }

        _logger.LogWarning("Видео-сервис: задача {TaskId} ({Url}) не завершилась за {Seconds} с.",
            taskId, url, _options.MaxWaitSeconds);
        return VideoDownloadResult.Failed(VideoEmbedErrors.Timeout);
    }

    /// <summary>
    /// Этап и проценты для показа пользователю. Поле <c>phase</c> у сервиса появилось позже
    /// самой функции, поэтому его отсутствие — не ошибка: старый сервис различает только
    /// «в очереди» и «в работе», и статус тогда показывается без деталей.
    /// </summary>
    private static VideoTaskProgress ToProgress(TaskStatusResponse? status)
    {
        var stage = status?.Phase switch
        {
            "queued" => VideoTaskStage.Queued,
            "probing" => VideoTaskStage.Probing,
            "downloading" => VideoTaskStage.DownloadSource,
            "converting" or "done" => VideoTaskStage.Converting,
            _ => status?.Status == "running" ? VideoTaskStage.DownloadSource : VideoTaskStage.Queued
        };

        return new VideoTaskProgress(stage, stage == VideoTaskStage.DownloadSource ? status?.Progress : null);
    }

    private async Task ReportAsync(
        Func<VideoTaskProgress, CancellationToken, Task>? onProgress, VideoTaskProgress progress, CancellationToken ct)
    {
        if (onProgress is null)
            return;

        try
        {
            await onProgress(progress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Видео-сервис: не удалось показать ход загрузки ({Stage}).", progress.Stage);
        }
    }

    /// <summary>Код причины из тела ответа сервиса; если тело не разобрать — по HTTP-статусу.</summary>
    private static string ParseErrorCode(string body)
    {
        try
        {
            var error = string.IsNullOrWhiteSpace(body)
                ? null
                : JsonSerializer.Deserialize<TaskErrorEnvelope>(body, JsonOptions);
            if (error?.Error?.Code is { Length: > 0 } code)
                return code;
        }
        catch (JsonException)
        {
            // Не JSON — значит, отвечал не сам сервис (прокси, заглушка): причина неизвестна.
        }

        return VideoEmbedErrors.ServiceError;
    }

    private async Task<string?> DownloadFileAsync(string downloadUrl, CancellationToken ct)
    {
        using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var path = TempFiles.NewPath(".mp4");
        try
        {
            await using var file = File.Create(path);
            await response.Content.CopyToAsync(file, ct);
            return path;
        }
        catch
        {
            TempFiles.TryDelete(path);
            throw;
        }
    }

    private sealed record TaskCreatedResponse([property: JsonPropertyName("taskId")] string TaskId);

    private sealed record TaskStatusResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("phase")] string? Phase,
        [property: JsonPropertyName("progress")] int? Progress,
        [property: JsonPropertyName("downloadUrl")] string? DownloadUrl,
        [property: JsonPropertyName("error")] TaskErrorResponse? Error);

    private sealed record TaskErrorEnvelope(
        [property: JsonPropertyName("error")] TaskErrorResponse? Error);

    private sealed record TaskErrorResponse(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message);
}
