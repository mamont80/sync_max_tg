using SyncMax.Models;

namespace SyncMax.Services.VideoEmbed;

/// <summary>
/// Тексты сообщения «видео из ссылок»: общая подпись (она же остаётся у готового видео) и
/// строка текущего состояния, которая дописывается к ней, пока видео едет. Чистое
/// форматирование без обращений к сети и БД — вынесено отдельно от
/// <see cref="VideoEmbedRelayService"/> по тем же соображениям, что и <see cref="RelayHeader"/>.
/// </summary>
public static class VideoEmbedTexts
{
    /// <summary>
    /// Подпись сообщения: «шапка» с автором и стороной-источником плюс сама ссылка (кликабельная).
    /// Это же остаётся подписью готового видео, поэтому строку состояния сюда не включаем —
    /// её дописывает <see cref="WithStatus"/>, и на финальном шаге она просто исчезает.
    /// </summary>
    public static FormattedText Caption(MessengerType source, string? senderName, string url)
    {
        var sourceTag = source == MessengerType.Max ? "MAX" : "TG";
        var header = string.IsNullOrWhiteSpace(senderName)
            ? $"🎬 Видео по ссылке · (из {sourceTag})"
            : $"🎬 Видео по ссылке от {senderName} · (из {sourceTag})";

        return new FormattedText
        {
            Text = $"{header}\n{url}",
            Spans = [new TextSpan { Kind = TextSpanKind.Link, Offset = header.Length + 1, Length = url.Length, Url = url }]
        };
    }

    /// <summary>Подпись со строкой состояния под ней; разметка ссылки не сдвигается — она левее.</summary>
    public static FormattedText WithStatus(FormattedText caption, string statusLine) =>
        caption.WithSuffix($"\n{statusLine}");

    /// <summary>Состояние на момент опроса — то, что видит пользователь вместо ещё не готового видео.</summary>
    public static string StatusLine(VideoTaskProgress progress) => progress.Stage switch
    {
        VideoTaskStage.Queued => "⏳ В очереди…",
        VideoTaskStage.Probing => "⏳ Запрос метаданных…",
        VideoTaskStage.DownloadSource => Percent(progress.Percent) is { } percent ? $"⬇️ Скачивание ссылки… {percent}%" : "⬇️ Скачивание ссылки…",
        VideoTaskStage.Converting => "⚙️ Конвертация…",
        VideoTaskStage.ResultDownload => Percent(progress.Percent) is { } percent ? $"⬇️ Получение результата… {percent}%" : "⬇️ Получение результата…",
        VideoTaskStage.Uploading => Percent(progress.Percent) is { } percent ? $"⬇️ 📤 Отправляется… {percent}%" : "⬇️ 📤 Отправляется…",
    };

    /// <summary>Первое состояние, показываемое сразу при постановке задачи.</summary>
    public const string QueuedLine = "⏳ В очереди…";

    /// <summary>Строка вместо видео, если его не будет: с причиной, понятной без чтения логов.</summary>
    public static string FailureLine(string? errorCode) =>
        $"⚠️ Видео не загрузилось: {Reason(errorCode)}";

    private static string Reason(string? errorCode) => errorCode switch
    {
        "VIDEO_TOO_LONG" => "слишком длинное",
        "FILE_TOO_LARGE" => "файл слишком большой",
        "UNAVAILABLE" => "недоступно (приватное, удалено или с ограничениями)",
        "INVALID_URL" => "ссылка не распознана",
        "QUEUE_FULL" or "QUEUE_TIMEOUT" => "сервис загружен, попробуйте позже",
        VideoEmbedErrors.Timeout => "слишком долго — не дождались",
        VideoEmbedErrors.SendFailed => "мессенджер не принял файл",
        _ => "ошибка загрузки"
    };

    /// <summary>
    /// Проценты округляются вниз до кратных пяти: сообщение переписывается только при смене
    /// показанного текста, и без округления каждый опрос давал бы новую правку — лишние
    /// запросы к API обоих мессенджеров ради движения на процент. Меньше 5% не показываем
    /// вовсе: «скачивается 0%» выглядит как поломка.
    /// </summary>
    private static int? Percent(int? percent) =>
        percent is >= 5 and <= 100 ? percent.Value / 5 * 5 : null;
}
