namespace SyncMax.Models;

/// <summary>
/// Связка чата/канала MAX с чатом/каналом Telegram для пересылки сообщений между ними.
/// Создаётся, когда оба (уже связанных между собой) пользователя репостнули сообщение
/// из соответствующего чата в своего бота.
/// </summary>
public sealed class ChatLink
{
    public long Id { get; set; }

    public string MaxChatId { get; set; } = string.Empty;

    /// <summary>"chat" или "channel".</summary>
    public string MaxChatType { get; set; } = string.Empty;

    /// <summary>Пользователь MAX, чьим репостом была выбрана сторона MAX этой связки.</summary>
    public string MaxUserId { get; set; } = string.Empty;

    public string TgChatId { get; set; } = string.Empty;

    /// <summary>"chat" или "channel".</summary>
    public string TgChatType { get; set; } = string.Empty;

    /// <summary>Пользователь Telegram, чьим репостом была выбрана сторона Telegram этой связки.</summary>
    public string TgUserId { get; set; } = string.Empty;

    public bool Active { get; set; } = true;

    /// <summary>
    /// "{название чата первой стороны} &lt;=&gt; {название чата второй стороны}". Порядок
    /// зависит от того, кто из двоих сделал репост первым, поэтому определить по нему
    /// сторону нельзя — для этого есть <see cref="MaxChatTitle"/>/<see cref="TgChatTitle"/>.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Название чата на стороне MAX. null у связок, созданных до миграции M005.</summary>
    public string? MaxChatTitle { get; set; }

    /// <summary>Название чата на стороне Telegram. null у связок, созданных до миграции M005.</summary>
    public string? TgChatTitle { get; set; }

    /// <summary>Направление пересылки, см. <see cref="RepostDirection"/>.</summary>
    public string RepostType { get; set; } = string.Empty;

    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>
    /// Аккаунт пары, создавшей связку. По нему копится статистика пересылки, поэтому он
    /// хранится прямо здесь: резолвить его через <c>users</c> на каждом сообщении значило
    /// бы лишний запрос к БД на горячем пути. null — у связок, чей аккаунт распался
    /// (<c>ON DELETE SET NULL</c>), и у не подхваченных бэкфилом миграции M008; статистика
    /// по таким связкам не пишется.
    /// </summary>
    public long? AccountId { get; set; }

    /// <summary>
    /// Включена ли для связки функция «видео из ссылок» (см. <see cref="Services.VideoEmbed.VideoEmbedRelayService"/>).
    /// По умолчанию включена (столбец добавлен миграцией M010 с DEFAULT 1) — это опциональное
    /// дополнение к пересылке, а не обязательная часть, поэтому его можно выключить per-связке.
    /// </summary>
    public bool VideoEmbedEnabled { get; set; } = true;

    public ChatKind MaxChatKind => ChatKindExtensions.FromCode(MaxChatType);

    public ChatKind TgChatKind => ChatKindExtensions.FromCode(TgChatType);

    public RepostDirection Direction => RepostDirectionExtensions.FromCode(RepostType);
}
