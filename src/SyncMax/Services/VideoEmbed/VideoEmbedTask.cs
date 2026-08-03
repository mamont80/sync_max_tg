namespace SyncMax.Services.VideoEmbed;

/// <summary>
/// Что происходит с задачей на сервисе-загрузчике прямо сейчас. Нужно только для показа
/// пользователю (см. <see cref="VideoEmbedTexts"/>): пока видео качается, статусное сообщение
/// в чате переписывается под текущий этап. Соответствует полю <c>phase</c> сервиса
/// (см. c:\sync_video\docs\API.md), кроме <see cref="Uploading"/> — это уже наш собственный
/// этап, скачивание готового файла с сервиса к себе.
/// </summary>
public enum VideoTaskStage
{
    Queued,
    Probing,
    DownloadSource,
    Converting,
    ResultDownload,
    Uploading
}

/// <summary>
/// Ход выполнения задачи. <see cref="Percent"/> есть только когда этап измерим (скачивание
/// с известным размером файла) — остальные этапы показываются без числа.
/// </summary>
public sealed record VideoTaskProgress(VideoTaskStage Stage, int? Percent);

/// <summary>
/// Итог: путь к скачанному временному файлу либо код причины, по которой видео не будет
/// (коды сервиса — <c>VIDEO_TOO_LONG</c>, <c>UNAVAILABLE</c> и т.п., плюс наши собственные
/// из <see cref="VideoEmbedErrors"/>). Код нужен, чтобы в чате вместо статуса показать
/// человеку внятную причину, а не молча оставить «скачивается…».
/// </summary>
public sealed record VideoDownloadResult(string? FilePath, string? ErrorCode)
{
    public static VideoDownloadResult Ok(string filePath) => new(filePath, null);

    public static VideoDownloadResult Failed(string errorCode) => new(null, errorCode);
}

/// <summary>Коды причин, которых нет у сервиса — они возникают на нашей стороне.</summary>
public static class VideoEmbedErrors
{
    /// <summary>Сервис не ответил, ответил не тем или оборвал соединение.</summary>
    public const string ServiceError = "SERVICE_ERROR";

    /// <summary>Задача не завершилась за <c>VideoEmbed:MaxWaitSeconds</c>.</summary>
    public const string Timeout = "TIMEOUT";

    /// <summary>Видео скачано, но мессенджер его не принял — ни правкой статуса, ни отправкой.</summary>
    public const string SendFailed = "SEND_FAILED";
}
