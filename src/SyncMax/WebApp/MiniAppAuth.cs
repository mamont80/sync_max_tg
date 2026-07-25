using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SyncMax.Configuration;
using SyncMax.Models;

namespace SyncMax.WebApp;

/// <summary>Пользователь, от имени которого выполняется запрос к API мини-приложения.</summary>
public sealed record MiniAppUser(MessengerType Messenger, string UserId, string? Name);

/// <summary>
/// Проверка данных запуска мини-приложения (initData). Класс один на обе платформы:
/// у MAX и Telegram схема подписи совпадает буквально —
/// <c>secret = HMAC-SHA256(key: "WebAppData", data: токен бота)</c>, затем
/// <c>hash = hex(HMAC-SHA256(key: secret, data: launch_params))</c>, где launch_params —
/// пары <c>ключ=значение</c> из initData (кроме самого <c>hash</c>), отсортированные по
/// ключу и склеенные через <c>\n</c>. Различается только токен бота и набор
/// необязательных полей (у MAX есть chat/ip, у Telegram — chat_type/chat_instance),
/// но на алгоритм это не влияет: сортируется и подписывается всё, что пришло.
///
/// Проверка выполняется на каждом запросе — сессий и токенов доступа своих не заводим.
/// </summary>
public sealed class MiniAppAuth
{
    /// <summary>Схема в заголовке Authorization: <c>TmaAuth {tg|max} {initData}</c>.</summary>
    public const string Scheme = "TmaAuth";

    private static readonly byte[] SecretKeySalt = Encoding.UTF8.GetBytes("WebAppData");

    private readonly TelegramOptions _telegram;
    private readonly MaxOptions _max;
    private readonly MiniAppOptions _options;
    private readonly ILogger<MiniAppAuth> _logger;

    public MiniAppAuth(
        IOptions<TelegramOptions> telegram,
        IOptions<MaxOptions> max,
        IOptions<MiniAppOptions> options,
        ILogger<MiniAppAuth> logger)
    {
        _telegram = telegram.Value;
        _max = max.Value;
        _options = options.Value;
        _logger = logger;

        if (DevUser is not null)
        {
            _logger.LogWarning(
                "МИНИ-ПРИЛОЖЕНИЕ: включён отладочный доступ (MiniApp:DevUserId={UserId}, " +
                "MiniApp:DevUserMessenger={Messenger}) — подпись initData НЕ проверяется. " +
                "В рабочей конфигурации эти настройки должны быть пустыми.",
                _options.DevUserId, _options.DevUserMessenger);
        }
    }

    /// <summary>
    /// Отладочный пользователь из настроек либо null. Пока он задан, запросы к API
    /// принимаются вообще без initData — см. <see cref="MiniAppOptions.DevUserId"/>.
    /// </summary>
    private MiniAppUser? DevUser
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_options.DevUserId))
                return null;

            var messenger = _options.DevUserMessenger switch
            {
                MessengerTypeExtensions.MaxCode => MessengerType.Max,
                _ => MessengerType.Telegram
            };
            return new MiniAppUser(messenger, _options.DevUserId.Trim(), "Отладочный пользователь");
        }
    }

    /// <summary>
    /// Достаёт пользователя из заголовка Authorization запроса. null — заголовка нет,
    /// он повреждён либо подпись не сошлась (вызывающий отвечает 401).
    /// </summary>
    public MiniAppUser? Authenticate(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header))
            return DevUser;

        // "TmaAuth {tg|max} {initData}" — initData сам содержит пробелы в percent-кодировке,
        // поэтому режем максимум на три части.
        var parts = header.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[0].Equals(Scheme, StringComparison.OrdinalIgnoreCase))
            return DevUser;

        MessengerType messenger;
        try
        {
            messenger = MessengerTypeExtensions.FromCode(parts[1]);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        return Validate(messenger, parts[2]) ?? DevUser;
    }

    /// <summary>
    /// Проверяет подпись <paramref name="initData"/> токеном бота <paramref name="messenger"/>
    /// и возвращает пользователя, либо null, если данные не подлинные/устарели.
    /// </summary>
    public MiniAppUser? Validate(MessengerType messenger, string initData)
    {
        if (string.IsNullOrWhiteSpace(initData))
            return null;

        var token = messenger == MessengerType.Max ? _max.Token : _telegram.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogWarning("[MiniApp] Нет токена {Messenger} — проверить подпись initData нечем.", messenger);
            return null;
        }

        Dictionary<string, string> fields;
        try
        {
            // Значения приходят percent-кодированными, а подписывались платформой уже
            // раскодированными — ParseQuery как раз возвращает раскодированные.
            fields = QueryHelpers.ParseQuery(initData)
                .ToDictionary(p => p.Key, p => p.Value.ToString(), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MiniApp] Не разобран initData от {Messenger}.", messenger);
            return null;
        }

        if (!fields.Remove("hash", out var hash) || string.IsNullOrWhiteSpace(hash))
            return null;

        // Ключом HMAC служит строка "WebAppData", а данными — токен бота (именно в таком
        // порядке, он у обеих платформ одинаковый и на первый взгляд «наоборот»).
        var secret = HMACSHA256.HashData(SecretKeySalt, Encoding.UTF8.GetBytes(token));

        var checkString = string.Join('\n', fields
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));

        var expected = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(checkString));

        byte[] actual;
        try
        {
            actual = Convert.FromHexString(hash);
        }
        catch (FormatException)
        {
            return null;
        }

        // Сравнение в постоянном времени: обычное посимвольное дало бы возможность
        // подбирать хеш по времени ответа.
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            _logger.LogWarning("[MiniApp] Подпись initData от {Messenger} не сошлась.", messenger);
            return null;
        }

        if (!IsFresh(fields))
        {
            _logger.LogWarning("[MiniApp] initData от {Messenger} устарел (auth_date старше {Hours} ч).",
                messenger, _options.AuthMaxAgeHours);
            return null;
        }

        return ReadUser(messenger, fields);
    }

    /// <summary>Проверка свежести auth_date (unix-секунды). Поля нет — считаем непригодным.</summary>
    private bool IsFresh(IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("auth_date", out var raw) || !long.TryParse(raw, out var unix))
            return false;

        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unix);
        return age >= TimeSpan.FromMinutes(-5) && age <= TimeSpan.FromHours(Math.Max(1, _options.AuthMaxAgeHours));
    }

    /// <summary>
    /// Разбирает поле user (JSON) — из него нужен только id, который в точности совпадает
    /// с users.user_id (open_id у MAX, chat id у Telegram), и имя для приветствия.
    /// </summary>
    private MiniAppUser? ReadUser(MessengerType messenger, IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("user", out var userJson) || string.IsNullOrWhiteSpace(userJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(userJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("id", out var idProp))
                return null;

            var userId = idProp.ValueKind == JsonValueKind.Number
                ? idProp.GetInt64().ToString()
                : idProp.GetString();

            if (string.IsNullOrWhiteSpace(userId))
                return null;

            var first = root.TryGetProperty("first_name", out var f) ? f.GetString() : null;
            var last = root.TryGetProperty("last_name", out var l) ? l.GetString() : null;
            var name = string.Join(' ', new[] { first, last }.Where(s => !string.IsNullOrWhiteSpace(s)));

            return new MiniAppUser(messenger, userId, string.IsNullOrWhiteSpace(name) ? null : name);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[MiniApp] Не разобрано поле user в initData от {Messenger}.", messenger);
            return null;
        }
    }
}
