namespace SyncMax.Models;

/// <summary>Поддерживаемые мессенджеры.</summary>
public enum MessengerType
{
    Telegram,
    Max
}

public static class MessengerTypeExtensions
{
    /// <summary>Строковый код, как он хранится в БД ("tg" / "max").</summary>
    public const string TelegramCode = "tg";
    public const string MaxCode = "max";

    public static string ToCode(this MessengerType type) => type switch
    {
        MessengerType.Telegram => TelegramCode,
        MessengerType.Max => MaxCode,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static MessengerType FromCode(string code) => code switch
    {
        TelegramCode => MessengerType.Telegram,
        MaxCode => MessengerType.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown messenger code")
    };
}
