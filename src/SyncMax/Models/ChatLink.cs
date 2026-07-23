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

    /// <summary>"{название чата первой стороны} <=> {название чата второй стороны}".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Направление пересылки, см. <see cref="RepostDirection"/>.</summary>
    public string RepostType { get; set; } = string.Empty;

    public string CreatedAt { get; set; } = string.Empty;

    public ChatKind MaxChatKind => ChatKindExtensions.FromCode(MaxChatType);

    public ChatKind TgChatKind => ChatKindExtensions.FromCode(TgChatType);

    public RepostDirection Direction => RepostDirectionExtensions.FromCode(RepostType);
}
