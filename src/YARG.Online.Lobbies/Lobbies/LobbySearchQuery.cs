using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Domain;

namespace YARG.Online.Lobbies.Lobbies;

public sealed record LobbySearchQuery(
    int Skip,
    int Take,
    GameMode? GameMode,
    Region? Region,
    string? Q);

public sealed record LobbySearchResult(
    IReadOnlyList<Lobby> Items,
    long TotalKnown);
