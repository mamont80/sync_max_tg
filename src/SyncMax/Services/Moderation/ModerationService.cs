using System.Text;
using Microsoft.Extensions.Logging;
using SyncMax.Models;

namespace SyncMax.Services.Moderation;

/// <summary>
/// Модерация пересылаемого содержимого — единая точка, через которую проходит всё, что
/// уходит в связанный чат. Пока проверяется только текст; проверки медиа встраиваются сюда
/// же, не трогая <see cref="MessageRelayService"/>.
///
/// Проверка многослойная:
/// 1. <see cref="TextNormalizer"/> приводит текст к единой форме — снимает подмену букв
///    другими алфавитами, невидимые символы, leet и растянутые гласные.
/// 2. Запрещённое законом (наркотики, спам) — сообщение не пересылается вовсе.
/// 3. Мат — сообщение пересылается, но бранные слова маскируются.
///
/// Порядок именно такой: если сообщение и матерное, и запрещённое, важнее второе —
/// маскировать в нём уже нечего, оно не уйдёт целиком.
///
/// Как и остальные сервисы, от платформы не зависит.
/// </summary>
public sealed class ModerationService
{
    private readonly ILogger<ModerationService> _logger;

    private readonly string[] _profanityRoots;
    private readonly HashSet<string> _exceptions;
    private readonly string[] _drugStrong;
    private readonly string[] _drugWeak;
    private readonly string[] _spamStrong;
    private readonly string[] _spamWeak;

    public ModerationService(ILogger<ModerationService> logger)
    {
        _logger = logger;

        // Словари прогоняем через ту же нормализацию, что и проверяемый текст: только так
        // «хуй» из словаря совпадёт с «ХУ́Й», «xyй» и «х0й» из сообщения.
        _profanityRoots = NormalizeWords(ModerationDictionary.ProfanityRoots);
        _exceptions = [.. NormalizeWords(ModerationDictionary.Exceptions)];
        _drugStrong = NormalizePhrases(ModerationDictionary.DrugStrong);
        _drugWeak = NormalizePhrases(ModerationDictionary.DrugWeak);
        _spamStrong = NormalizePhrases(ModerationDictionary.SpamStrong);
        _spamWeak = NormalizePhrases(ModerationDictionary.SpamWeak);
    }

    /// <summary>
    /// Проверяет текст сообщения. При маскировке длина текста НЕ меняется (бранное слово
    /// заменяется на первую букву и звёздочки посимвольно), поэтому офсеты участков
    /// форматирования <see cref="FormattedText.Spans"/> остаются верными и переносятся как есть.
    /// </summary>
    public ModerationResult Check(FormattedText text)
    {
        if (string.IsNullOrWhiteSpace(text.Text))
            return ModerationResult.Allow(text);

        var phrase = TextNormalizer.NormalizePhrase(text.Text);

        if (FindIllegalReason(phrase) is { } illegal)
        {
            _logger.LogWarning("Модерация: сообщение заблокировано ({Reason}).", illegal);
            return new ModerationResult(ModerationDecision.Blocked, text, illegal);
        }

        var masked = MaskProfanity(text.Text);
        if (masked is null)
            return ModerationResult.Allow(text);

        _logger.LogInformation("Модерация: в сообщении замаскирована нецензурная лексика.");
        return new ModerationResult(
            ModerationDecision.Masked,
            new FormattedText { Text = masked, Spans = text.Spans },
            ModerationReason.Profanity);
    }

    /// <summary>
    /// Текст, который уходит вместо заблокированного сообщения. Причину намеренно не
    /// называем: сообщать «у вас нашли наркотики» человеку, чью фразу фильтр понял
    /// неправильно, — хуже, чем нейтральная формулировка. Причина остаётся в журнале.
    /// </summary>
    public const string BlockedPlaceholder =
        "🚫 Нам показалось, что сообщение возможно нарушает законы РФ, мы рисковать не будем "
        + "и вам не советуем. Смотрите оригинал сообщения в другом мессенджере.";

    /* ---------- Запрещённое законом ---------- */

    private ModerationReason? FindIllegalReason(string phrase)
    {
        if (ContainsAny(phrase, _drugStrong) || CountMatches(phrase, _drugWeak) >= ModerationDictionary.DrugWeakThreshold)
            return ModerationReason.Drugs;

        if (ContainsAny(phrase, _spamStrong) || CountMatches(phrase, _spamWeak) >= ModerationDictionary.SpamWeakThreshold)
            return ModerationReason.Spam;

        return null;
    }

    private static bool ContainsAny(string phrase, string[] terms)
    {
        foreach (var term in terms)
        {
            if (term.Length > 0 && phrase.Contains(term, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Сколько РАЗНЫХ слов из списка встретилось; повтор одного и того же не считается.</summary>
    private static int CountMatches(string phrase, string[] terms)
    {
        var count = 0;
        foreach (var term in terms)
        {
            if (term.Length > 0 && phrase.Contains(term, StringComparison.Ordinal))
                count++;
        }
        return count;
    }

    /* ---------- Мат ---------- */

    /// <summary>
    /// Возвращает текст с замаскированными бранными словами либо null, если маскировать
    /// нечего. Слова берутся из ИСХОДНОГО текста по их границам, а нормализованная форма
    /// используется только для решения — так замена не зависит от того, насколько сильно
    /// нормализация изменила слово.
    /// </summary>
    private string? MaskProfanity(string text)
    {
        StringBuilder? result = null;

        foreach (var (start, length) in TextNormalizer.EnumerateWords(text))
        {
            var normalized = TextNormalizer.NormalizeWord(text.AsSpan(start, length));
            if (normalized.Length == 0 || _exceptions.Contains(normalized))
                continue;

            if (!ContainsAny(normalized, _profanityRoots))
                continue;

            result ??= new StringBuilder(text);
            MaskWord(result, start, length);
        }

        return result?.ToString();
    }

    /// <summary>
    /// Заменяет слово на «первая буква + звёздочки»: «хуй» → «х**». Звёздочки ставятся
    /// только вместо букв и цифр, знаки препинания остаются на месте («хуй!» → «х**!»),
    /// а общая длина слова сохраняется — от неё зависят офсеты форматирования.
    /// </summary>
    private static void MaskWord(StringBuilder text, int start, int length)
    {
        var firstKept = false;

        for (var i = start; i < start + length; i++)
        {
            if (!char.IsLetterOrDigit(text[i]))
                continue;

            if (!firstKept)
            {
                firstKept = true;
                continue;
            }

            text[i] = '*';
        }
    }

    /* ---------- Подготовка словарей ---------- */

    private static string[] NormalizeWords(string[] words) =>
        [.. words.Select(w => TextNormalizer.NormalizeWord(w)).Where(w => w.Length > 0).Distinct()];

    private static string[] NormalizePhrases(string[] phrases) =>
        [.. phrases.Select(TextNormalizer.NormalizePhrase).Where(p => p.Length > 0).Distinct()];
}
