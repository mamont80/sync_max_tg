using System.Security.Cryptography;
using System.Text;

namespace SyncMax.Messengers;

/// <summary>
/// Секрет для проверки входящих webhook-запросов. Отдельной настройки нет: секрет
/// вычисляется из токена самого бота (<c>{Мессенджер}:Token</c>) — он уже есть в конфиге
/// и известен только нам и мессенджеру.
///
/// Берётся не сам токен, а его SHA-256 (hex): во-первых, Telegram разрешает в secret_token
/// только <c>A-Z a-z 0-9 _ -</c>, а токен бота содержит двоеточие; во-вторых, у MAX секрет
/// уходит query-параметром в url подписки, и настоящий токен API светился бы в логах
/// reverse proxy. Значение стабильно — его не нужно нигде хранить, оба конца считают его
/// из одного и того же токена.
/// </summary>
public static class WebhookSecret
{
    /// <summary>Секрет для указанного токена бота. Пустой токен — пустой секрет (проверять нечего).</summary>
    public static string FromToken(string? botToken) =>
        string.IsNullOrWhiteSpace(botToken)
            ? string.Empty
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(botToken)));
}
