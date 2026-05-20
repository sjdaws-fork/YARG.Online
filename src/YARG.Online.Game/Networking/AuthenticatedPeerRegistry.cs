using System.Collections.Concurrent;

namespace YARG.Online.Game.Networking;

public sealed record AuthenticatedPeer(string UserId, string DisplayName, string LobbyId, int ExpectedMembers, bool IsHost);

public sealed class AuthenticatedPeerRegistry
{
    private readonly ConcurrentDictionary<int, AuthenticatedPeer> _peers = new();

    public int Count => _peers.Count;

    public bool TryAdd(int peerId, AuthenticatedPeer peer) => _peers.TryAdd(peerId, peer);

    public bool TryRemove(int peerId, out AuthenticatedPeer? peer) => _peers.TryRemove(peerId, out peer);

    public bool TryGet(int peerId, out AuthenticatedPeer? peer) => _peers.TryGetValue(peerId, out peer);
}
