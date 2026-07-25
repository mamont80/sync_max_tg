namespace SyncMax.Models;

/// <summary>
/// Направление пересылки сообщений между связанными чатами. Соблюдается для всех типов
/// переноса — обычного сообщения, правки и удаления (см. MessageRelayService).
/// Связки создаются как <see cref="Both"/>; сменить направление можно пока только
/// правкой <c>repost_type</c> в БД — команды для пользователя ещё нет.
/// </summary>
public enum RepostDirection
{
    MaxToTg,
    TgToMax,
    Both
}

public static class RepostDirectionExtensions
{
    public const string MaxToTgCode = "max_to_tg";
    public const string TgToMaxCode = "tg_to_max";
    public const string BothCode = "both";

    public static string ToCode(this RepostDirection direction) => direction switch
    {
        RepostDirection.MaxToTg => MaxToTgCode,
        RepostDirection.TgToMax => TgToMaxCode,
        RepostDirection.Both => BothCode,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null)
    };

    public static RepostDirection FromCode(string code) => code switch
    {
        MaxToTgCode => RepostDirection.MaxToTg,
        TgToMaxCode => RepostDirection.TgToMax,
        BothCode => RepostDirection.Both,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown repost direction")
    };
}
