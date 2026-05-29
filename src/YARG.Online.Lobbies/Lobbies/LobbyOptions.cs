namespace YARG.Online.Lobbies.Lobbies;

public sealed class LobbyOptions
{
    public const string SectionName = "Lobbies";

    /// <summary>Number of base32 ID generations to attempt before giving up on collision.</summary>
    public int IdGenerationAttempts { get; init; } = 5;

    /// <summary>Maximum number of chat messages retained per lobby. Oldest are evicted FIFO.</summary>
    public int MaxChatHistorySize { get; init; } = 100;

    /// <summary>Maximum number of songs allowed in a lobby's queue at once.</summary>
    public int MaxQueueSize { get; init; } = 100;

    /// <summary>Minimum client version required to connect. Null or empty disables the gate.</summary>
    public string? MinClientVersion { get; init; }
}
