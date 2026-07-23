namespace SyncMax.Models;

/// <summary>
/// Тип разметки участка текста — универсальный набор, общий для MAX и Telegram.
/// Платформо-специфичные стили, у которых нет пары на другой стороне (спойлер/цитата
/// Telegram, заголовок/выделение MAX и т.п.), в этот перечень не входят: при разборе
/// входящего сообщения они просто отбрасываются, текст остаётся без стиля.
/// </summary>
public enum TextSpanKind
{
    Bold,
    Italic,
    Underline,
    Strikethrough,
    Monospace,
    Link
}

/// <summary>
/// Один непрерывный участок форматирования поверх <see cref="FormattedText.Text"/>.
/// <see cref="Offset"/> и <see cref="Length"/> измеряются в единицах UTF-16 (как индексы
/// в .NET-строке и как entities Telegram) — от этого зависит совпадение офсетов при
/// конвертации между платформами.
/// </summary>
public sealed class TextSpan
{
    public TextSpanKind Kind { get; init; }

    public int Offset { get; init; }

    public int Length { get; init; }

    /// <summary>URL — только для <see cref="TextSpanKind.Link"/>.</summary>
    public string? Url { get; init; }
}

/// <summary>
/// Платформо-независимое представление сообщения: чистый текст плюс список участков
/// форматирования. Служит "посредником" при пересылке — каждый мессенджер конвертирует
/// СВОЙ формат в <see cref="FormattedText"/> на приёме и из него — на отправке
/// (Telegram → entities, MAX → markdown-строка). Сам <see cref="Services.MessageRelayService"/>
/// работает только с этой моделью и о конкретных форматах не знает.
/// </summary>
public sealed class FormattedText
{
    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<TextSpan> Spans { get; init; } = [];

    public static FormattedText Plain(string text) => new() { Text = text };

    /// <summary>
    /// Возвращает копию с добавленным в начало простым (неформатированным) префиксом;
    /// офсеты всех участков сдвигаются на длину префикса. Используется для служебной
    /// "шапки" пересылаемого сообщения.
    /// </summary>
    public FormattedText WithPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return this;

        var shift = prefix.Length;
        var shifted = new List<TextSpan>(Spans.Count);
        foreach (var s in Spans)
            shifted.Add(new TextSpan { Kind = s.Kind, Offset = s.Offset + shift, Length = s.Length, Url = s.Url });

        return new FormattedText { Text = prefix + Text, Spans = shifted };
    }
}
