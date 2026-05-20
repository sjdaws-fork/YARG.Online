using System.Collections.Concurrent;

namespace YARG.Online.Lobbies.Hubs;

public sealed class ConnectionTracker : IConnectionTracker
{
    private readonly ConcurrentDictionary<string, State> _state = new(StringComparer.Ordinal);

    private readonly record struct State(string UserId, string LobbyId);

    public void SetLobby(string connectionId, string userId, string lobbyId)
    {
        var next = new State(userId, lobbyId);
        _state.AddOrUpdate(
            connectionId,
            next,
            (_, existing) =>
            {
                if (existing.LobbyId != lobbyId)
                {
                    throw new InvalidOperationException(
                        $"Connection {connectionId} is already in lobby {existing.LobbyId}, refusing to set {lobbyId}.");
                }
                return existing;
            });
    }

    public void ClearLobby(string connectionId) => _state.TryRemove(connectionId, out _);

    public string? GetLobby(string connectionId) => _state.TryGetValue(connectionId, out var s) ? s.LobbyId : null;

    public string? GetUserId(string connectionId) => _state.TryGetValue(connectionId, out var s) ? s.UserId : null;

    public IReadOnlyList<string> GetConnectionsForUser(string userId)
    {
        // Lobby populations are small (≤ MaxPlayers per lobby, with one entry per active
        // connection); a linear scan is cheaper than maintaining a reverse index.
        List<string>? matches = null;
        foreach (var (connId, state) in _state)
        {
            if (state.UserId == userId)
            {
                matches ??= new List<string>(2);
                matches.Add(connId);
            }
        }
        return matches ?? (IReadOnlyList<string>)Array.Empty<string>();
    }
}
