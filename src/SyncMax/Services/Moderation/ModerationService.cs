using SyncMax.Models;

namespace SyncMax.Services.Moderation;

/// <summary>
/// Модерация отключена: <see cref="Check"/> всегда пропускает текст как есть.
/// Точка вызова (<see cref="MessageRelayService"/>) осталась прежней, поэтому включить
/// проверки обратно — вопрос только этого класса.
/// </summary>
public sealed class ModerationService
{
    public ModerationResult Check(FormattedText text) => ModerationResult.Allow(text);

    /// <summary>Текст заглушки для заблокированных сообщений; сейчас недостижим, т.к. блокировки нет.</summary>
    public const string BlockedPlaceholder =
        "🚫 Нам показалось, что сообщение возможно нарушает законы РФ, мы рисковать не будем "
        + "и вам не советуем. Смотрите оригинал сообщения в другом мессенджере.";
}
