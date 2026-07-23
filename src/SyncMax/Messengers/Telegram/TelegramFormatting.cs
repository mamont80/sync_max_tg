using SyncMax.Models;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace SyncMax.Messengers.Telegram;

/// <summary>
/// Конвертация форматирования Telegram ↔ универсальный <see cref="FormattedText"/>.
/// В обе стороны используются entities (offset/length поверх plain-текста), поэтому
/// не нужно ни парсить, ни собирать markdown-строку с экранированием. Стили без пары
/// в универсальной модели (spoiler, blockquote, mention и т.п.) отбрасываются.
/// </summary>
internal static class TelegramFormatting
{
    /// <summary>Входящий текст + entities Telegram → универсальный форматированный текст.</summary>
    public static FormattedText ToFormattedText(string text, MessageEntity[]? entities)
    {
        if (entities is null || entities.Length == 0)
            return FormattedText.Plain(text);

        var spans = new List<TextSpan>(entities.Length);
        foreach (var e in entities)
        {
            var kind = e.Type switch
            {
                MessageEntityType.Bold => TextSpanKind.Bold,
                MessageEntityType.Italic => TextSpanKind.Italic,
                MessageEntityType.Underline => TextSpanKind.Underline,
                MessageEntityType.Strikethrough => TextSpanKind.Strikethrough,
                MessageEntityType.Code => TextSpanKind.Monospace,
                MessageEntityType.Pre => TextSpanKind.Monospace,
                MessageEntityType.TextLink => TextSpanKind.Link,
                _ => (TextSpanKind?)null // url/mention/spoiler/blockquote и прочее — отбрасываем
            };
            if (kind is null)
                continue;

            spans.Add(new TextSpan
            {
                Kind = kind.Value,
                Offset = e.Offset,
                Length = e.Length,
                Url = kind == TextSpanKind.Link ? e.Url : null
            });
        }

        return new FormattedText { Text = text, Spans = spans };
    }

    /// <summary>Универсальный текст → entities Telegram (null, если форматирования нет).</summary>
    public static MessageEntity[]? ToEntities(FormattedText content)
    {
        if (content.Spans.Count == 0)
            return null;

        var entities = new List<MessageEntity>(content.Spans.Count);
        foreach (var s in content.Spans)
        {
            var type = s.Kind switch
            {
                TextSpanKind.Bold => MessageEntityType.Bold,
                TextSpanKind.Italic => MessageEntityType.Italic,
                TextSpanKind.Underline => MessageEntityType.Underline,
                TextSpanKind.Strikethrough => MessageEntityType.Strikethrough,
                TextSpanKind.Monospace => MessageEntityType.Code,
                TextSpanKind.Link => MessageEntityType.TextLink,
                _ => (MessageEntityType?)null
            };
            if (type is null)
                continue;

            entities.Add(new MessageEntity
            {
                Type = type.Value,
                Offset = s.Offset,
                Length = s.Length,
                Url = s.Kind == TextSpanKind.Link ? s.Url : null
            });
        }

        return entities.Count == 0 ? null : entities.ToArray();
    }
}
