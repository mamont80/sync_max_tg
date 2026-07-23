namespace SyncMax.Models;

/// <summary>
/// Пользователь одного из мессенджеров.
/// <c>UserId</c> — идентификатор в мессенджере (open_id для MAX, chat id для Telegram).
/// Первичный ключ — пара (<c>UserId</c>, <c>Messenger</c>).
/// Одна физическая персона может иметь до двух записей (по одной на мессенджер),
/// которые связываются друг с другом через колонку <c>LinkedToUser</c>.
/// </summary>
public sealed class User
{
    public string UserId { get; set; } = string.Empty;

    /// <summary>"tg" или "max".</summary>
    public string Messenger { get; set; } = string.Empty;

    public string RegisteredAt { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public string? Name { get; set; }

    public string Language { get; set; } = "ru";

    /// <summary>Текущий код связки (6 цифр) либо null.</summary>
    public string? LinkCode { get; set; }

    /// <summary>Момент формирования кода связки (ISO-8601) либо null.</summary>
    public string? LinkCodeCreatedAt { get; set; }

    /// <summary>
    /// <c>UserId</c> связанного аккаунта в другом мессенджере, либо null.
    /// Заполняется у обеих сторон связки.
    /// </summary>
    public string? LinkedToUser { get; set; }

    /// <summary>
    /// Чат/канал, выбранный этим пользователем репостом сообщения — ожидает,
    /// пока то же самое сделает связанный аккаунт во втором мессенджере,
    /// после чего пара очищается и превращается в запись <see cref="ChatLink"/>.
    /// </summary>
    public string? LinkingChatId { get; set; }

    /// <summary>"chat" или "channel", см. <see cref="LinkingChatId"/>.</summary>
    public string? LinkingChatType { get; set; }

    /// <summary>Название чата/канала на момент репоста (для составления title связки).</summary>
    public string? LinkingChatTitle { get; set; }

    public MessengerType MessengerType => MessengerTypeExtensions.FromCode(Messenger);

    public ChatKind? LinkingChatKind => LinkingChatType is null ? null : ChatKindExtensions.FromCode(LinkingChatType);
}
