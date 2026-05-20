namespace YARG.Online.Lobbies.Hubs;

public interface IConnectionTracker
{
    /// <summary>
    /// Record that the given connection (representing the given user) is currently in the given lobby.
    /// Throws if the connection is already mapped to a different lobby — that's a hub-method bug.
    /// </summary>
    void SetLobby(string connectionId, string userId, string lobbyId);

    void ClearLobby(string connectionId);

    string? GetLobby(string connectionId);

    string? GetUserId(string connectionId);

    /// <summary>
    /// Returns every connection currently tracked for the given user (one user may have
    /// multiple connections in flight when reconnecting). Used when the hub needs to
    /// reach all of a user's sessions, e.g. to remove them from a lobby group after a kick.
    /// </summary>
    IReadOnlyList<string> GetConnectionsForUser(string userId);
}
