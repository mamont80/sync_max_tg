namespace SyncMax.Configuration;

/// <summary>Настройки Telegram-бота. Токен подставляется в appsettings.json (оставлен пустым).</summary>
public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    public string Token { get; set; } = string.Empty;
}

/// <summary>Настройки MAX-бота. Токен подставляется в appsettings.json (оставлен пустым).</summary>
public sealed class MaxOptions
{
    public const string Section = "Max";

    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Базовый URL Bot API MAX. Вынесен в конфиг на случай смены хоста/окружения.
    /// Старый домен botapi.max.ru/platform-api.max.ru отключён 19.07.2026.
    /// </summary>
    public string ApiBaseUrl { get; set; } = "https://platform-api2.max.ru";
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    public string ConnectionString { get; set; } = "Data Source=syncmax.db";
}

/// <summary>Параметры процесса связывания аккаунтов.</summary>
public sealed class LinkingOptions
{
    public const string Section = "Linking";

    /// <summary>Язык интерфейса по умолчанию для новых пользователей.</summary>
    public string DefaultLanguage { get; set; } = "ru";
}
