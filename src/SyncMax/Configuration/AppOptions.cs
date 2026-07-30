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

/// <summary>
/// Параметры фоновой уборки БД (<see cref="Services.MessageLinkCleanupService"/>).
/// Значения по умолчанию рассчитаны так, чтобы удаление шло тонкой струйкой и не мешало
/// пересылке сообщений, которая пишет в ту же таблицу.
/// </summary>
public sealed class CleanupOptions
{
    public const string Section = "Cleanup";

    /// <summary>
    /// Сколько суток хранить карту «оригинал ↔ копия» (<c>message_links</c>). Записи нужны,
    /// пока на сообщение могут ответить, отредактировать или удалить его; дальше они только
    /// занимают место. 0 и меньше — уборка отключена.
    /// </summary>
    public int MessageLinkRetentionDays { get; set; } = 14;

    /// <summary>Сколько записей удалять за одну транзакцию.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>Пауза между партиями, пока есть что удалять.</summary>
    public int BatchPauseSeconds { get; set; } = 5;

    /// <summary>Пауза после того, как всё просроченное вычищено, — до следующей проверки.</summary>
    public int IdlePauseMinutes { get; set; } = 60;

    /// <summary>
    /// Задержка перед первым проходом после старта: даём приложению поднять ботов и
    /// применить миграции, не соревнуясь с ними за базу.
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 60;
}

/// <summary>
/// Параметры статистики пересылки (<see cref="Services.Stats.RelayStatsCollector"/> и
/// <see cref="Services.Stats.RelayStatsFlushService"/>). Показатели копятся в ОЗУ и
/// уходят в БД пачкой — интервал задаёт, какой период теряется при аварийном
/// завершении процесса.
/// </summary>
public sealed class StatsOptions
{
    public const string Section = "Stats";

    /// <summary>Как часто накопленное переносится в БД. Меньше минуты не бывает.</summary>
    public int FlushIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Задержка перед первой выгрузкой после старта: даём приложению поднять ботов
    /// и применить миграции, не соревнуясь с ними за базу.
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 30;
}

/// <summary>
/// Параметры мини-приложения (веб-интерфейс, открываемый из чата с ботом). Адрес один
/// на оба мессенджера: платформа определяется на фронте по доступному мосту, а на бэке —
/// по префиксу в заголовке Authorization (см. <c>WebApp/MiniAppAuth</c>).
/// </summary>
public sealed class MiniAppOptions
{
    public const string Section = "MiniApp";

    /// <summary>
    /// Публичный https-адрес мини-приложения, напр. https://ваш-домен/app. Пусто —
    /// кнопка запуска в ботах не выставляется (само приложение при этом всё равно
    /// раздаётся, что удобно при локальной отладке).
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Сколько часов данные запуска (auth_date в initData) считаются свежими. Подпись
    /// бессрочна сама по себе, поэтому окно ограничиваем: перехваченный initData
    /// не должен работать вечно.
    /// </summary>
    public int AuthMaxAgeHours { get; set; } = 24;

    /// <summary>
    /// ТОЛЬКО для локальной отладки: id пользователя, от имени которого работают запросы
    /// к API мини-приложения, когда подписанного initData нет (открыли /app в обычном
    /// браузере). Непусто — проверка подписи ОТКЛЮЧЕНА, на старте пишется предупреждение.
    /// В рабочей конфигурации должно быть пусто.
    /// </summary>
    public string DevUserId { get; set; } = string.Empty;

    /// <summary>Мессенджер отладочного пользователя ("tg" или "max"), см. <see cref="DevUserId"/>.</summary>
    public string DevUserMessenger { get; set; } = "tg";
}

/// <summary>
/// Параметры опциональной функции «видео из ссылок» (<see cref="Services.VideoEmbed.VideoEmbedRelayService"/>):
/// когда в пересылаемом сообщении есть ссылка на YouTube-видео/Shorts, бот дополнительно скачивает
/// само видео через внешний сервис (см. c:\sync_video) и публикует его отдельным сообщением в оба
/// чата связки. Включена/выключена per-связке (<c>chat_links.video_embed_enabled</c>, по умолчанию
/// включена) — эти настройки лишь про сам внешний сервис.
/// </summary>
public sealed class VideoEmbedOptions
{
    public const string Section = "VideoEmbed";

    /// <summary>
    /// Базовый адрес сервиса-загрузчика (напр. http://video.mishaserver.ru:5001). Пусто — функция
    /// отключена целиком, независимо от настроек связок (сервис не с кем работать).
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Заголовок X-API-Key, если на сервисе задан API_KEY. Пусто — заголовок не отправляется.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Пауза между опросами статуса задачи.</summary>
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>
    /// Сколько максимум ждать готовности видео (очередь + скачивание), прежде чем сдаться.
    /// Сервис держит задачу в очереди до 10 минут (QUEUE_TIMEOUT_SECONDS) — ждём чуть дольше.
    /// </summary>
    public int MaxWaitSeconds { get; set; } = 660;
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
