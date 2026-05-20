using YARG.Online.Lobbies.Contracts.Rest;

namespace YARG.Online.Lobbies.Lobbies;

public enum LobbyChangeKind
{
    Added,
    Updated,
    Removed,
}

public sealed record LobbyChange(string LobbyId, LobbyChangeKind Kind, LobbyDto? Lobby);
