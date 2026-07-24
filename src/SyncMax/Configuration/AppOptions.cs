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

/// <summary>Параметры пересылки медиа (скачивание/загрузка вложений, конвертация).</summary>
public sealed class MediaOptions
{
    public const string Section = "Media";

    /// <summary>
    /// Путь к ffmpeg для конвертации (аудио, при необходимости). Пусто/не найден — конвертация
    /// отключается, а несовместимые вложения пересылаются как есть либо файлом. "ffmpeg" —
    /// искать в PATH.
    /// </summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>
    /// Максимальный размер вложения для скачивания/пересылки, МБ. Для Telegram фактический
    /// потолок скачивания через Bot API всё равно 20 МБ (ограничение платформы).
    /// </summary>
    public int MaxFileMegabytes { get; set; } = 45;
}
