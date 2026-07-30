using System.Text.RegularExpressions;
using SyncMax.Models;

namespace SyncMax.Services.VideoEmbed;

/// <summary>
/// Ищет в пересылаемом тексте первую ссылку на YouTube-видео или Shorts — форматы те же,
/// что принимает внешний сервис-загрузчик (см. c:\sync_video\docs\API.md): watch?v=, youtu.be,
/// shorts, с www./m. или без. Возвращает уже нормализованный абсолютный URL без хвостовых
/// параметров (?si=..., &amp;t=... и т.п. отбрасываются) — сервису нужен только сам id видео.
/// </summary>
public static class YouTubeLinkExtractor
{
    // Протокол необязателен: в сообщении ссылка может быть набрана как "youtu.be/xyz" без
    // https://, мессенджер её всё равно подсветит как кликабельную у получателя.
    private static readonly Regex Pattern = new(
        @"(?:https?://)?(?:www\.|m\.)?(?:youtube\.com/(?:watch\?v=|shorts/)|youtu\.be/)[\w-]{6,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Первая найденная ссылка, либо null. Проверяются и участки разметки (<see cref="TextSpanKind.Link"/> —
    /// когда видимый текст ссылки не совпадает с самим URL), и сырой текст — обычная вставка ссылки.
    /// </summary>
    public static string? TryFind(FormattedText text)
    {
        foreach (var span in text.Spans)
        {
            if (span.Kind == TextSpanKind.Link && span.Url is { Length: > 0 } url && Pattern.IsMatch(url))
                return Normalize(Pattern.Match(url).Value);
        }

        var match = Pattern.Match(text.Text);
        return match.Success ? Normalize(match.Value) : null;
    }

    private static string Normalize(string match) =>
        match.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || match.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? match
            : "https://" + match;
}
