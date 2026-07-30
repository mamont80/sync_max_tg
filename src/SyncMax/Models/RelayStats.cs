using System.Text;

namespace SyncMax.Models;

/// <summary>
/// Вклад одной пересылки в статистику: сообщение плюс объём того, что реально ушло.
/// Считается из уже готового к отправке <see cref="RelayMessage"/> и размеров временных
/// файлов вложений — платформо-независимо, как и всё в пересылке.
///
/// Виды медиа сведены в четыре группы: подробнее (отдельно «кружок» и анимация)
/// в интерфейсе всё равно не показывается, а колонок в таблице стало бы вдвое больше.
/// </summary>
public readonly record struct RelayStatsDelta
{
    public long Messages { get; init; }

    /// <summary>Длина отправленного текста/подписи в UTF-8, включая служебную «шапку».</summary>
    public long TextBytes { get; init; }

    public long PhotoCount { get; init; }

    public long PhotoBytes { get; init; }

    /// <summary>Видео, анимации и «кружки» video_note.</summary>
    public long VideoCount { get; init; }

    public long VideoBytes { get; init; }

    /// <summary>Музыка и голосовые сообщения.</summary>
    public long AudioCount { get; init; }

    public long AudioBytes { get; init; }

    public long FileCount { get; init; }

    public long FileBytes { get; init; }

    /// <summary>
    /// Вклад одной отправки. <paramref name="sizes"/> — размеры файлов вложений в том же
    /// порядке, что и <paramref name="attachments"/>; недостающие считаются нулевыми
    /// (файл мог оказаться недоступен — статистика не повод терять сообщение).
    /// </summary>
    public static RelayStatsDelta From(
        FormattedText caption, IReadOnlyList<MediaAttachment> attachments, IReadOnlyList<long> sizes)
    {
        long photoCount = 0, photoBytes = 0;
        long videoCount = 0, videoBytes = 0;
        long audioCount = 0, audioBytes = 0;
        long fileCount = 0, fileBytes = 0;

        for (var i = 0; i < attachments.Count; i++)
        {
            var size = i < sizes.Count ? sizes[i] : 0;

            switch (attachments[i].Kind)
            {
                case MediaKind.Photo:
                    photoCount++;
                    photoBytes += size;
                    break;
                case MediaKind.Video or MediaKind.Animation or MediaKind.VideoNote:
                    videoCount++;
                    videoBytes += size;
                    break;
                case MediaKind.Audio or MediaKind.Voice:
                    audioCount++;
                    audioBytes += size;
                    break;
                default:
                    fileCount++;
                    fileBytes += size;
                    break;
            }
        }

        return new RelayStatsDelta
        {
            Messages = 1,
            TextBytes = Encoding.UTF8.GetByteCount(caption.Text),
            PhotoCount = photoCount,
            PhotoBytes = photoBytes,
            VideoCount = videoCount,
            VideoBytes = videoBytes,
            AudioCount = audioCount,
            AudioBytes = audioBytes,
            FileCount = fileCount,
            FileBytes = fileBytes
        };
    }
}

/// <summary>
/// Строка статистики за сутки по одной связке чатов в одном направлении — то, что
/// накопитель отдаёт на запись, а репозиторий прибавляет к уже лежащему в БД.
/// </summary>
public sealed class RelayStatsRow
{
    public long AccountId { get; init; }

    /// <summary>
    /// Связка чатов. Хранится как историческое значение: связку могли удалить, но её
    /// вклад в суммы аккаунта остаётся (см. миграцию M009).
    /// </summary>
    public long ChatLinkId { get; init; }

    /// <summary>Сутки в UTC, "YYYY-MM-DD".</summary>
    public string Day { get; init; } = string.Empty;

    /// <summary>Куда переслано: "max_to_tg" или "tg_to_max".</summary>
    public string Direction { get; init; } = string.Empty;

    public long Messages { get; set; }

    public long TextBytes { get; set; }

    public long PhotoCount { get; set; }

    public long PhotoBytes { get; set; }

    public long VideoCount { get; set; }

    public long VideoBytes { get; set; }

    public long AudioCount { get; set; }

    public long AudioBytes { get; set; }

    public long FileCount { get; set; }

    public long FileBytes { get; set; }

    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>Нечего записывать: за период по этому ключу не было ни одной пересылки.</summary>
    public bool IsEmpty =>
        Messages == 0 && TextBytes == 0
        && PhotoCount == 0 && PhotoBytes == 0
        && VideoCount == 0 && VideoBytes == 0
        && AudioCount == 0 && AudioBytes == 0
        && FileCount == 0 && FileBytes == 0;
}

/// <summary>
/// Итог по аккаунту за всё время. Считается из дневных строк — отдельно накопленных
/// итогов нет намеренно, иначе их пришлось бы держать в согласии с днями.
/// </summary>
public sealed class RelayStatsTotals
{
    public long Messages { get; init; }

    /// <summary>Весь перенесённый объём: текст плюс вложения всех видов.</summary>
    public long Bytes { get; init; }

    /// <summary>Сообщений, перенесённых из MAX в Telegram.</summary>
    public long MaxToTg { get; init; }

    /// <summary>Сообщений, перенесённых из Telegram в MAX.</summary>
    public long TgToMax { get; init; }

    public long TextBytes { get; init; }

    public long PhotoCount { get; init; }

    public long PhotoBytes { get; init; }

    public long VideoCount { get; init; }

    public long VideoBytes { get; init; }

    public long AudioCount { get; init; }

    public long AudioBytes { get; init; }

    public long FileCount { get; init; }

    public long FileBytes { get; init; }
}

/// <summary>Показатели за один период: сутки ("2026-07-29") либо месяц ("2026-07").</summary>
public sealed class RelayStatsPeriod
{
    public string Period { get; init; } = string.Empty;

    public long Messages { get; init; }

    public long Bytes { get; init; }

    public long MaxToTg { get; init; }

    public long TgToMax { get; init; }
}

/// <summary>Итог по одной связке чатов за всё время.</summary>
public sealed class RelayStatsLink
{
    public long ChatLinkId { get; init; }

    /// <summary>Название связки; null — связка уже удалена, а её вклад в сумму остался.</summary>
    public string? Title { get; init; }

    public long Messages { get; init; }

    public long Bytes { get; init; }
}
