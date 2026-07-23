using System.Text.Json.Serialization;

namespace SyncMax.Messengers.Max;

// Модели ответа Bot API MAX. Заполнены под типовой формат long polling
// (GET /updates -> { updates: [...], marker }). При расхождении с реальным
// API достаточно поправить эти DTO и MaxApiClient — остальной код не зависит.

public sealed class MaxUpdatesResponse
{
    [JsonPropertyName("updates")]
    public List<MaxUpdate>? Updates { get; set; }

    /// <summary>Курсор для следующего запроса /updates.</summary>
    [JsonPropertyName("marker")]
    public long? Marker { get; set; }
}

public sealed class MaxUpdate
{
    [JsonPropertyName("update_type")]
    public string? UpdateType { get; set; }

    [JsonPropertyName("message")]
    public MaxMessage? Message { get; set; }

    /// <summary>Пользователь, добавивший бота в чат/канал (только для update_type == "bot_added").</summary>
    [JsonPropertyName("user")]
    public MaxUser? User { get; set; }

    /// <summary>Id чата/канала, в который добавили бота (только для update_type == "bot_added").</summary>
    [JsonPropertyName("chat_id")]
    public long? ChatId { get; set; }
}

public sealed class MaxMessage
{
    [JsonPropertyName("sender")]
    public MaxUser? Sender { get; set; }

    [JsonPropertyName("body")]
    public MaxMessageBody? Body { get; set; }

    /// <summary>Чат/канал, в котором получено сообщение (id + тип).</summary>
    [JsonPropertyName("recipient")]
    public MaxRecipient? Recipient { get; set; }
}

/// <summary>Получатель сообщения — чат, в котором оно отправлено.</summary>
public sealed class MaxRecipient
{
    [JsonPropertyName("chat_id")]
    public long? ChatId { get; set; }

    /// <summary>"dialog" (личка с ботом) | "chat" | "channel".</summary>
    [JsonPropertyName("chat_type")]
    public string? ChatType { get; set; }
}

/// <summary>Ответ GET /chats/{chatId}. Заполнены только поля, нужные для связок чатов.</summary>
public sealed class MaxChat
{
    [JsonPropertyName("chat_id")]
    public long ChatId { get; set; }

    /// <summary>"dialog" | "chat" | "channel".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }
}

public sealed class MaxUser
{
    /// <summary>open_id пользователя MAX.</summary>
    [JsonPropertyName("user_id")]
    public long? UserId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>true, если отправитель — бот. Отсутствует/null, если платформа это не сообщает.</summary>
    [JsonPropertyName("is_bot")]
    public bool? IsBot { get; set; }
}

public sealed class MaxMessageBody
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>
    /// Разметка текста (жирный/курсив/ссылка и т.п.) — массив участков с офсетами.
    /// В MAX форматирование приходит отдельным списком, а не встроенным в text
    /// (в отличие от исходящего markdown, см. <see cref="MaxSendMessageRequest.Format"/>).
    /// </summary>
    [JsonPropertyName("markup")]
    public List<MaxMarkupElement>? Markup { get; set; }
}

/// <summary>Один участок разметки входящего сообщения MAX (см. MarkupElement в Bot API).</summary>
public sealed class MaxMarkupElement
{
    /// <summary>"strong" | "emphasized" | "monospaced" | "link" | "strikethrough" | "underline" | "heading" | "highlighted" | "user_mention".</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Начальный индекс участка в тексте (zero-based, единицы UTF-16).</summary>
    [JsonPropertyName("from")]
    public int From { get; set; }

    [JsonPropertyName("length")]
    public int Length { get; set; }

    /// <summary>URL — только для type == "link".</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>Тело запроса на отправку сообщения.</summary>
public sealed class MaxSendMessageRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Формат разметки в <see cref="Text"/>: "markdown" | "html". null — текст без разметки
    /// (обычный plain text). У MAX нет entities на отправку, поэтому форматирование
    /// встраивается прямо в текст markdown-синтаксисом.
    /// </summary>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; set; }
}
