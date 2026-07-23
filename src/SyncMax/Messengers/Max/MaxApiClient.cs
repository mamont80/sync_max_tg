using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;
using SyncMax.Models;

namespace SyncMax.Messengers.Max;

/// <summary>
/// Тонкий HTTP-клиент над Bot API MAX. Токен передаётся как query-параметр
/// access_token. Базовый URL берётся из конфигурации (Max:ApiBaseUrl).
/// Реализует <see cref="IMessengerApiClient"/> — общий контракт отправки для LinkingService.
/// </summary>
public sealed class MaxApiClient : IMessengerApiClient
{
    private readonly HttpClient _http;
    private readonly MaxOptions _options;
    private readonly ILogger<MaxApiClient> _logger;

    public MaxApiClient(HttpClient http, IOptions<MaxOptions> options, ILogger<MaxApiClient> logger)
    {
        _http = http;
        _options = options.Value;
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

        // Читаем сырой ответ и пишем его в лог целиком — для отладки формата MAX API.
        var raw = await _http.GetStringAsync(url, ct);
        _logger.LogInformation("[MAX] RAW ответ /updates:\n{Raw}", raw);

        return string.IsNullOrWhiteSpace(raw)
            ? null
            : JsonSerializer.Deserialize<MaxUpdatesResponse>(raw);
    }

    /// <summary>Отправка текстового сообщения пользователю по его open_id.</summary>
    public async Task SendTextAsync(string userId, string text, CancellationToken ct)
    {
        var url = $"{BaseUrl}/messages?user_id={userId}";
        using var response = await _http.PostAsJsonAsync(url, new MaxSendMessageRequest { Text = text }, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Отправка текстового сообщения в групповой чат/канал по его chat_id.</summary>
    public async Task SendChatTextAsync(string chatId, string text, CancellationToken ct)
    {
        var url = $"{BaseUrl}/messages?chat_id={chatId}";
        using var response = await _http.PostAsJsonAsync(url, new MaxSendMessageRequest { Text = text }, ct);
        response.EnsureSuccessStatusCode();
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
