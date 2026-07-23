namespace SyncMax.Models;

/// <summary>Тип связываемого чата: групповой чат или канал.</summary>
public enum ChatKind
{
    Chat,
    Channel
}

public static class ChatKindExtensions
{
    public const string ChatCode = "chat";
    public const string ChannelCode = "channel";
    public const string DialogCode = "dialog";

    public static string ToCode(this ChatKind kind) => kind switch
    {
        ChatKind.Chat => ChatCode,
        ChatKind.Channel => ChannelCode,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static ChatKind FromCode(string code) => code switch
    {
        ChatCode => ChatKind.Chat,
        ChannelCode => ChatKind.Channel,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown chat kind")
    };
}
