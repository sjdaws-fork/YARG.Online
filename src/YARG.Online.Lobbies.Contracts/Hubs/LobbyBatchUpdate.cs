using YARG.Online.Lobbies.Contracts.Rest;

namespace YARG.Online.Lobbies.Contracts.Hubs;

/// <summary>
/// A coalesced batch of lobby browser changes sent to clients in the "browse" group every tick.
/// Apply order on the client: Added/Updated first, Removed last.
/// </summary>
public sealed record LobbyBatchUpdate(
    LobbyDto[] Added,
    LobbyDto[] Updated,
    string[] Removed,
    long Sequence);
