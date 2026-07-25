using SyncMax.Models;

namespace SyncMax.Services.Moderation;

/// <summary>Что делать с сообщением по итогам проверки.</summary>
public enum ModerationDecision
{
    /// <summary>Пропустить как есть.</summary>
    Allow,

    /// <summary>Переслать, но с заменённым текстом (мат замаскирован).</summary>
    Masked,

    /// <summary>Не пересылать; вместо сообщения уходит заглушка.</summary>
    Blocked
}

/// <summary>Из-за чего сработала модерация — для журнала и для текста заглушки.</summary>
public enum ModerationReason
{
    None,
    Profanity,
    Drugs,
    Spam
}

/// <summary>
/// Итог проверки. <see cref="Text"/> — то, что следует отправить: исходный текст при
/// <see cref="ModerationDecision.Allow"/>, замаскированный при <see cref="ModerationDecision.Masked"/>
/// и исходный (неиспользуемый) при <see cref="ModerationDecision.Blocked"/> — в последнем
/// случае вызывающий подставляет заглушку сам.
/// </summary>
public sealed record ModerationResult(ModerationDecision Decision, FormattedText Text, ModerationReason Reason)
{
    public static ModerationResult Allow(FormattedText text) =>
        new(ModerationDecision.Allow, text, ModerationReason.None);
}
