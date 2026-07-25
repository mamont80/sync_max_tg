namespace SyncMax.Models;

/// <summary>
/// Вид медиа-вложения — универсальный набор, общий для MAX и Telegram. На отправке
/// целевой клиент отображает его в свой тип (напр. для MAX анимация и «кружок» video_note
/// уходят обычным видео, голос — как audio), при необходимости конвертируя или откатываясь
/// на отправку файлом.
/// </summary>
public enum MediaKind
{
    Photo,
    Video,
    Voice,
    Audio,
    Animation,
    VideoNote,
    Document
}

/// <summary>
/// Одно медиа-вложение, уже скачанное во временный файл на диске (чтобы не держать
/// содержимое в ОЗУ). Временем жизни файла управляет <see cref="Services.MessageRelayService"/>:
/// после отправки он удаляет все <see cref="FilePath"/>.
/// </summary>
public sealed class MediaAttachment
{
    public MediaKind Kind { get; init; }

    /// <summary>Путь к временному файлу с содержимым вложения.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Имя файла (для документов/аудио) — если известно.</summary>
    public string? FileName { get; init; }

    public string? MimeType { get; init; }

    public int? DurationSeconds { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }
}

/// <summary>
/// Платформо-независимое сообщение для пересылки: подпись (форматированный текст) плюс
/// список медиа-вложений. Для чисто текстового сообщения список пуст, для «медиа без
/// подписи» — пустой <see cref="Caption"/>.
/// </summary>
public sealed class RelayMessage
{
    public FormattedText Caption { get; init; } = FormattedText.Plain(string.Empty);

    public IReadOnlyList<MediaAttachment> Attachments { get; init; } = [];

    /// <summary>Id исходного сообщения в его мессенджере (Telegram message_id / MAX mid).</summary>
    public string? SourceMessageId { get; init; }

    /// <summary>Id сообщения-оригинала, на которое отвечает исходное (в его мессенджере), если это ответ.</summary>
    public string? ReplyToSourceMessageId { get; init; }

    /// <summary>Id сообщения в ЦЕЛЕВОМ чате, на которое надо оформить ответ (проставляет relay).</summary>
    public string? ReplyToTargetMessageId { get; init; }

    public bool HasMedia => Attachments.Count > 0;

    /// <summary>Есть ли что пересылать вообще (хоть текст, хоть вложение).</summary>
    public bool IsEmpty => Attachments.Count == 0 && string.IsNullOrEmpty(Caption.Text);

    /// <summary>
    /// Копия с подписью, заменённой на прошедшую модерацию (см. <c>ModerationService</c>).
    /// Вложения и идентификаторы переносятся как есть.
    /// </summary>
    public RelayMessage WithModeratedCaption(FormattedText caption) =>
        new()
        {
            Caption = caption,
            Attachments = Attachments,
            SourceMessageId = SourceMessageId,
            ReplyToSourceMessageId = ReplyToSourceMessageId,
            ReplyToTargetMessageId = ReplyToTargetMessageId
        };

    /// <summary>
    /// Копия с добавленной в начало подписи служебной «шапкой» и разрешённым целевым id ответа
    /// (см. relay). Остальные поля переносятся как есть.
    /// </summary>
    public RelayMessage WithCaptionPrefix(string prefix, string? replyToTargetMessageId) =>
        new()
        {
            Caption = Caption.WithPrefix(prefix),
            Attachments = Attachments,
            SourceMessageId = SourceMessageId,
            ReplyToSourceMessageId = ReplyToSourceMessageId,
            ReplyToTargetMessageId = replyToTargetMessageId
        };
}
