using System.Text;
using SyncMax.Models;

namespace SyncMax.Messengers.Max;

/// <summary>
/// Конвертация форматирования MAX ↔ универсальный <see cref="FormattedText"/>.
/// Приём: массив <see cref="MaxMarkupElement"/> (offset/length поверх plain-текста) → участки.
/// Отправка: участки → markdown-строка (у MAX нет entities на отправку, только format=markdown).
/// Неподдерживаемые стили (heading/highlighted/user_mention) отбрасываются.
/// </summary>
internal static class MaxFormatting
{
    /// <summary>Входящее тело сообщения MAX → универсальный форматированный текст.</summary>
    public static FormattedText ToFormattedText(MaxMessageBody? body)
    {
        var text = body?.Text ?? string.Empty;
        var markup = body?.Markup;
        if (markup is null || markup.Count == 0)
            return FormattedText.Plain(text);

        var spans = new List<TextSpan>(markup.Count);
        foreach (var m in markup)
        {
            var kind = m.Type switch
            {
                "strong" => TextSpanKind.Bold,
                "emphasized" => TextSpanKind.Italic,
                "underline" => TextSpanKind.Underline,
                "strikethrough" => TextSpanKind.Strikethrough,
                "monospaced" => TextSpanKind.Monospace,
                "link" => TextSpanKind.Link,
                _ => (TextSpanKind?)null // heading / highlighted / user_mention и прочее — отбрасываем
            };
            if (kind is null)
                continue;

            spans.Add(new TextSpan
            {
                Kind = kind.Value,
                Offset = m.From,
                Length = m.Length,
                Url = kind == TextSpanKind.Link ? m.Url : null
            });
        }

        return new FormattedText { Text = text, Spans = spans };
    }

    /// <summary>
    /// Универсальный текст → пара (текст, format) для <see cref="MaxSendMessageRequest"/>.
    /// Если участков нет — возвращает исходный текст без формата (plain, без экранирования).
    /// Иначе встраивает markdown-разметку и возвращает format = "markdown".
    /// </summary>
    public static (string Text, string? Format) ToRequestText(FormattedText content)
    {
        var text = content.Text;
        var n = text.Length;

        var spans = content.Spans
            .Where(s => s.Length > 0 && s.Offset >= 0 && s.Offset + s.Length <= n)
            .ToList();
        if (spans.Count == 0)
            return (text, null);

        var sb = new StringBuilder(n + spans.Count * 4);
        for (var i = 0; i <= n; i++)
        {
            // Закрываем участки, кончающиеся в i; позже открытый закрываем первым (LIFO).
            foreach (var s in spans.Where(s => s.Offset + s.Length == i).OrderByDescending(s => s.Offset))
                sb.Append(CloseToken(s));

            if (i == n)
                break;

            // Открываем участки, начинающиеся в i; более длинный (внешний) открываем первым.
            foreach (var s in spans.Where(s => s.Offset == i).OrderByDescending(s => s.Offset + s.Length))
                sb.Append(OpenToken(s));

            var c = text[i];
            // Внутри моноширинного участка спецсимволы не экранируются (в code-span markdown
            // экранирование не работает, обратный слэш отобразился бы буквально).
            if (IsInsideMonospace(spans, i))
                sb.Append(c);
            else
                AppendEscaped(sb, c);
        }

        return (sb.ToString(), "markdown");
    }

    private static bool IsInsideMonospace(List<TextSpan> spans, int i) =>
        spans.Any(s => s.Kind == TextSpanKind.Monospace && s.Offset <= i && i < s.Offset + s.Length);

    private static string OpenToken(TextSpan s) => s.Kind switch
    {
        TextSpanKind.Bold => "**",
        TextSpanKind.Italic => "_",
        TextSpanKind.Underline => "++",
        TextSpanKind.Strikethrough => "~~",
        TextSpanKind.Monospace => "`",
        TextSpanKind.Link => "[",
        _ => string.Empty
    };

    private static string CloseToken(TextSpan s) => s.Kind switch
    {
        TextSpanKind.Bold => "**",
        TextSpanKind.Italic => "_",
        TextSpanKind.Underline => "++",
        TextSpanKind.Strikethrough => "~~",
        TextSpanKind.Monospace => "`",
        TextSpanKind.Link => $"]({EscapeUrl(s.Url)})",
        _ => string.Empty
    };

    // Экранируем символы, которыми открываются/закрываются наши markdown-конструкции,
    // чтобы литеральный текст пользователя не превратился случайно в разметку.
    private static void AppendEscaped(StringBuilder sb, char c)
    {
        if (c is '\\' or '*' or '_' or '~' or '+' or '^' or '`' or '[' or ']')
            sb.Append('\\');
        sb.Append(c);
    }

    private static string EscapeUrl(string? url) =>
        (url ?? string.Empty).Replace("\\", "\\\\").Replace(")", "\\)");
}
