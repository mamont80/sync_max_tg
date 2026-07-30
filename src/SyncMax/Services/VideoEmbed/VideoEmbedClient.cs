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
/// Любая неудача (сеть, таймаут, ошибка сервиса, невалидная ссылка) возвращает null — это
/// опциональное дополнение к пересылке, а не критичная часть, поэтому здесь никогда не
/// бросаем исключения наружу, только логируем.
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
    /// во временный файл на диске (см. <see cref="TempFiles"/>). null — сервис не настроен,
    /// ссылка не распознана, видео недоступно/слишком длинное/большое, очередь переполнена
    /// или не удалось дождаться за <see cref="VideoEmbedOptions.MaxWaitSeconds"/>. Причина
    /// в обоих случаях уходит только в лог — вызывающий код молча пропускает функцию.
    /// </summary>
    public async Task<string?> TryDownloadAsync(string url, CancellationToken ct)
    {
        if (!IsConfigured)
            return null;

        try
        {
            var taskId = await SubmitAsync(url, ct);
            return taskId is null ? null : await PollAndDownloadAsync(taskId, url, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Видео-сервис: не удалось обработать ссылку {Url}.", url);
            return null;
        }
    }

    private async Task<string?> SubmitAsync(string url, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("/api/v1/tasks", new { url }, JsonOptions, ct);

        if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            _logger.LogInformation("Видео-сервис: POST /tasks для {Url} вернул {Status}.", url, response.StatusCode);
            return null;
        }

        var created = await response.Content.ReadFromJsonAsync<TaskCreatedResponse>(JsonOptions, ct);
        return created?.TaskId;
    }

    private async Task<string?> PollAndDownloadAsync(string taskId, string url, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_options.MaxWaitSeconds);
        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(pollInterval, ct);

            using var response = await _http.GetAsync($"/api/v1/tasks/{taskId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Видео-сервис: опрос задачи {TaskId} ({Url}) вернул {Status}.",
                    taskId, url, response.StatusCode);
                return null;
            }

            var status = await response.Content.ReadFromJsonAsync<TaskStatusResponse>(JsonOptions, ct);
            switch (status?.Status)
            {
                case "completed" when status.DownloadUrl is { Length: > 0 } downloadUrl:
                    return await DownloadFileAsync(downloadUrl, ct);
                case "failed":
                    _logger.LogInformation("Видео-сервис: задача {TaskId} ({Url}) завершилась ошибкой {Code}: {Message}",
                        taskId, url, status.Error?.Code, status.Error?.Message);
                    return null;
                // queued/running — продолжаем поллинг.
            }
        }

        _logger.LogInformation("Видео-сервис: задача {TaskId} ({Url}) не завершилась за {Seconds} с.",
            taskId, url, _options.MaxWaitSeconds);
        return null;
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
        [property: JsonPropertyName("downloadUrl")] string? DownloadUrl,
        [property: JsonPropertyName("error")] TaskErrorResponse? Error);

    private sealed record TaskErrorResponse(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message);
}
