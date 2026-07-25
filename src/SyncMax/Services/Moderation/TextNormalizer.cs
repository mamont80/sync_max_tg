using System.Globalization;
using System.Text;

namespace SyncMax.Services.Moderation;

/// <summary>
/// Приведение текста к единой форме перед проверками — первый слой модерации.
/// Без него фильтр обходится тривиально: «х у й», «хуū», «ху+й», «ХУЙ», «хууууй»,
/// «xyй» (латинские x и y) — для наивного сравнения это шесть разных строк.
///
/// Порядок этапов существен:
/// 1. NFKD — раскладывает совместимые и составные символы: ﬁ → fi, ４ → 4,
///    𝗑 → x, é → e + акут, й → и + кратка, ё → е + умлаут.
/// 2. Отбрасываются диакритические знаки (Mn) — то, что осталось от предыдущего шага,
///    и невидимые служебные символы (Cf, Cc): нулевой ширины пробел, мягкий перенос,
///    метки направления письма. Их вставляют внутрь слова именно ради обхода фильтров.
/// 3. Замена визуальных двойников (<see cref="Confusables"/>) — до lowercase,
///    иначе не поймать подмену заглавными.
/// 4. Нижний регистр.
/// 5. Схлопывание подряд идущих одинаковых букв: «хуууй» → «хуй».
///
/// Результат предназначен ТОЛЬКО для сравнения со словарём и пользователю не показывается
/// (так же оговорено и в UTS #39 про skeleton): текст после такой обработки уже не
/// соответствует исходному ни по длине, ни по написанию.
/// </summary>
internal static class TextNormalizer
{
    /// <summary>
    /// Нормализует слово и оставляет только буквы и цифры: знаки препинания внутри слова
    /// («х.у.й», «б*л*я») — тоже способ обхода, а для сравнения со словарём они не нужны.
    /// Пустая строка означает, что значимых символов в слове не было (например, эмодзи).
    /// </summary>
    public static string NormalizeWord(ReadOnlySpan<char> word)
    {
        if (word.IsEmpty)
            return string.Empty;

        string decomposed;
        try
        {
            decomposed = new string(word).Normalize(NormalizationForm.FormKD);
        }
        catch (ArgumentException)
        {
            // Битые суррогатные пары нормализовать нельзя — работаем с тем, что есть.
            decomposed = new string(word);
        }

        // Слово из букв разных алфавитов — само по себе признак подмены (mixed-script
        // detection в UTS #39). Только для таких слов имеет смысл заменять заглавные
        // латинские B, H, K, M, T на кириллические: см. Confusables.MixedScriptMap.
        var mixedScript = IsMixedScript(decomposed);

        var result = new StringBuilder(decomposed.Length);
        var previous = '\0';

        foreach (var raw in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(raw);

            // Диакритика (остаток разложения) и невидимые служебные символы.
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark
                or UnicodeCategory.Format
                or UnicodeCategory.Control)
            {
                continue;
            }

            var c = char.ToLowerInvariant(Confusables.Canonical(raw, mixedScript));

            if (!char.IsLetterOrDigit(c))
                continue;

            // Повтор одной и той же буквы ничего не добавляет к смыслу, но ломает
            // сравнение со словарём.
            if (c == previous)
                continue;

            result.Append(c);
            previous = c;
        }

        return result.ToString();
    }

    /// <summary>
    /// Содержит ли слово буквы и латиницы, и кириллицы одновременно. В обычном тексте
    /// такого не бывает — люди не смешивают алфавиты внутри слова, а вот при подмене букв
    /// смешение возникает само собой.
    /// </summary>
    private static bool IsMixedScript(string word)
    {
        var latin = false;
        var cyrillic = false;

        foreach (var c in word)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
                latin = true;
            else if (c is >= 'Ѐ' and <= 'ӿ')
                cyrillic = true;

            if (latin && cyrillic)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Нормализует текст целиком: каждое слово по отдельности, слова разделены одним
    /// пробелом. Нужен для поиска словосочетаний («быстрый заработок»), которые в одно
    /// слово не укладываются.
    /// </summary>
    public static string NormalizePhrase(string text)
    {
        var parts = new List<string>();

        foreach (var token in EnumerateWords(text))
        {
            var normalized = NormalizeWord(text.AsSpan(token.Start, token.Length));
            if (normalized.Length > 0)
                parts.Add(normalized);
        }

        return string.Join(' ', parts);
    }

    /// <summary>Границы слов исходного текста (разделитель — пробельные символы).</summary>
    public static IEnumerable<(int Start, int Length)> EnumerateWords(string text)
    {
        var start = -1;

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                if (start >= 0)
                {
                    yield return (start, i - start);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        if (start >= 0)
            yield return (start, text.Length - start);
    }
}
