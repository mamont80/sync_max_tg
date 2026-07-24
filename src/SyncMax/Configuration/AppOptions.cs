namespace SyncMax.Configuration;

/// <summary>Режим приёма входящих обновлений от мессенджера.</summary>
public enum BotMode
{
    /// <summary>Опрос сервера мессенджера в цикле (getUpdates/marker). Не требует публичного адреса.</summary>
    LongPolling,

    /// <summary>Мессенджер сам присылает обновления HTTP-запросом на публичный адрес.</summary>
    Webhook
}

/// <summary>
/// Настройки HTTP-сервера процесса, на который приходят webhook-запросы: сервер один на
/// оба мессенджера, поэтому и настройка одна (в отличие от <see cref="WebhookOptions"/>,
/// где у каждого бота свои адрес и путь). Слушает, только если хотя бы один мессенджер
/// работает в режиме <see cref="BotMode.Webhook"/>.
/// </summary>
public sealed class HttpServerOptions
{
    public const string Section = "HttpServer";

    /// <summary>Адрес, на котором Kestrel принимает входящие запросы, напр. http://0.0.0.0:8443.</summary>
    public string ListenUrl { get; set; } = "http://0.0.0.0:8443";
}

/// <summary>
/// Настройки webhook одного мессенджера (секция <c>{Мессенджер}:Webhook</c>) — учитываются
/// только при <see cref="BotMode.Webhook"/>. Класс один на оба мессенджера, потому что набор
/// настроек у них одинаковый, а значения у каждого свои: свой публичный адрес и свой путь.
/// Секрета здесь нет — для проверки входящих запросов используется токен самого бота
/// (<c>{Мессенджер}:Token</c>), см. <see cref="Messengers.WebhookSecret"/>.
/// </summary>
public sealed class WebhookOptions
{
    /// <summary>
    /// Публичный адрес, который регистрируется в самом мессенджере (Telegram — setWebhook,
    /// MAX — POST /subscriptions) и на который тот присылает обновления. Обязателен для режима Webhook.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Локальный путь, который слушает Kestrel (должен соответствовать пути в <see cref="Url"/>).</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>Настройки Telegram-бота. Токен подставляется в appsettings.json (оставлен пустым).</summary>
public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Базовый URL Bot API. Пусто — официальный сервер (https://api.telegram.org).
    /// Задаётся для собственного (локального) Bot API сервера, напр. http://localhost:8081.
    /// </summary>
    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>Режим приёма обновлений. По умолчанию — long polling.</summary>
    public BotMode Mode { get; set; } = BotMode.LongPolling;

    /// <summary>Свои настройки webhook Telegram-бота — учитываются только при <see cref="Mode"/> == Webhook.</summary>
    public WebhookOptions Webhook { get; set; } = new() { Path = "/webhook/telegram" };
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

    /// <summary>Режим приёма обновлений. По умолчанию — long polling.</summary>
    public BotMode Mode { get; set; } = BotMode.LongPolling;

    /// <summary>Свои настройки webhook MAX-бота — учитываются только при <see cref="Mode"/> == Webhook.</summary>
    public WebhookOptions Webhook { get; set; } = new() { Path = "/webhook/max" };
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
