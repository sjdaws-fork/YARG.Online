using System.Collections.Concurrent;
using LiteNetLib;
using YARG.Online.Game.Contracts.Packets;

namespace YARG.Online.Game.Networking;

public sealed class GameSessionManager
{
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new();
    private readonly ConcurrentDictionary<int, string> _peerToLobby = new();

    public TryStartResult TryAddPeer(string lobbyId, int expectedMembers, NetPeer peer)
    {
        var session = _sessions.GetOrAdd(lobbyId, _ => new GameSession(lobbyId, expectedMembers));
        lock (session.Gate)
        {
            session.Peers[peer.Id] = peer;
            _peerToLobby[peer.Id] = lobbyId;
            return TryClaimStart(session);
        }
    }

    /// <summary>
    /// Store this peer's loadout selection. First-write-wins per peer. Returns false if no session
    /// exists for the lobby, the peer isn't connected, or a loadout was already recorded.
    /// </summary>
    public bool TryStoreLoadout(
        string lobbyId,
        int peerId,
        SetLoadoutPacket loadout,
        out TryStartResult result)
    {
        result = default;

        if (!_sessions.TryGetValue(lobbyId, out var session))
        {
            return false;
        }

        lock (session.Gate)
        {
            if (!session.Peers.ContainsKey(peerId))
            {
                return false;
            }

            if (!session.Loadouts.TryAdd(peerId, loadout))
            {
                return false;
            }

            result = TryClaimStart(session);
        }

        return true;
    }

    /// <summary>
    /// Record the host's chart duration for the in-progress session. First-write-wins. Returns
    /// false if the session is missing, the cue has already been broadcast (too late to influence
    /// the lobby's stored value), or duration was already recorded.
    /// </summary>
    public bool TryStoreSongDuration(string lobbyId, int durationMs)
    {
        if (!_sessions.TryGetValue(lobbyId, out var session))
        {
            return false;
        }

        lock (session.Gate)
        {
            if (session.CueBroadcast || session.DurationMs.HasValue)
            {
                return false;
            }
            session.DurationMs = durationMs;
            return true;
        }
    }

    /// <summary>
    /// Read the host-supplied song duration (in ms). Returns 0 if the host never sent metadata —
    /// callers should treat 0 as "unknown" and still surface a start-time so the lobby's runtime
    /// fields stay coherent.
    /// </summary>
    public int GetSongDurationMs(string lobbyId)
    {
        if (!_sessions.TryGetValue(lobbyId, out var session))
        {
            return 0;
        }
        lock (session.Gate)
        {
            return session.DurationMs ?? 0;
        }
    }

    /// <summary>
    /// Marks the peer as having loaded the chart and reached the gameplay scene. Returns true and
    /// the full peer list when every session peer has reported ready (caller broadcasts the cue).
    /// </summary>
    public bool TryMarkReady(int peerId, out CueReadyResult result)
    {
        result = default;

        if (!_peerToLobby.TryGetValue(peerId, out var lobbyId)
            || !_sessions.TryGetValue(lobbyId, out var session))
        {
            return false;
        }

        lock (session.Gate)
        {
            if (session.CueBroadcast || !session.Peers.ContainsKey(peerId))
            {
                return false;
            }

            session.ReadyPeers.Add(peerId);

            // All currently-connected peers must be ready, AND we must be at full quorum.
            // Anything less is "still waiting".
            if (session.Peers.Count < session.ExpectedMembers
                || session.ReadyPeers.Count < session.Peers.Count)
            {
                return true;
            }

            session.CueBroadcast = true;
            result = new CueReadyResult(ReadyToCue: true, SnapshotPeers(session));
            return true;
        }
    }

    /// <summary>
    /// Marks the peer as having reached end-of-chart. Returns whether this was the first completion
    /// in the session (so the caller can start the straggler timer) and whether all peers are now
    /// done (so the caller broadcasts GameEnd).
    /// </summary>
    public bool TryMarkCompleted(int peerId, out CompletionResult result)
    {
        result = default;

        if (!_peerToLobby.TryGetValue(peerId, out var lobbyId)
            || !_sessions.TryGetValue(lobbyId, out var session))
        {
            return false;
        }

        lock (session.Gate)
        {
            if (session.EndBroadcast || !session.Peers.ContainsKey(peerId))
            {
                return false;
            }

            if (!session.CompletedPeers.Add(peerId))
            {
                return false;
            }

            bool wasFirst = session.FirstCompletionAt is null;
            if (wasFirst)
            {
                session.FirstCompletionAt = DateTimeOffset.UtcNow;
            }

            // All currently-connected peers must have completed (a disconnected peer is implicitly
            // out and shouldn't block end-of-game).
            bool allDone = session.CompletedPeers.Count >= session.Peers.Count;
            if (allDone)
            {
                session.EndBroadcast = true;
            }

            result = new CompletionResult(
                WasFirstCompletion: wasFirst,
                ReadyToEnd: allDone,
                LobbyId: lobbyId,
                Peers: allDone ? SnapshotPeers(session) : Array.Empty<NetPeer>());
            return true;
        }
    }

    /// <summary>
    /// Used by the straggler timer to force-end a session if not all peers have reported done.
    /// Returns null if the session was already ended (or never existed).
    /// </summary>
    public CompletionResult? ForceEndSession(string lobbyId)
    {
        if (!_sessions.TryGetValue(lobbyId, out var session))
        {
            return null;
        }

        lock (session.Gate)
        {
            if (session.EndBroadcast)
            {
                return null;
            }

            session.EndBroadcast = true;
            return new CompletionResult(
                WasFirstCompletion: false,
                ReadyToEnd: true,
                LobbyId: lobbyId,
                Peers: SnapshotPeers(session));
        }
    }

    /// <summary>
    /// Drops the session entirely (called after GameEnd is broadcast and we no longer need the
    /// per-peer state).
    /// </summary>
    public void DisposeSession(string lobbyId)
    {
        if (!_sessions.TryRemove(lobbyId, out var session))
        {
            return;
        }

        lock (session.Gate)
        {
            foreach (var peerId in session.Peers.Keys)
            {
                _peerToLobby.TryRemove(peerId, out _);
            }
        }
    }

    /// <summary>
    /// Hot-path: called for every EngineInputBatch. Returns true and the list of *other* peers in
    /// the session (excluding the sender) only when the session is live (GameStartCue broadcast).
    /// </summary>
    public bool TryGetFanoutTargets(int senderPeerId, out IReadOnlyList<NetPeer> targets)
    {
        targets = Array.Empty<NetPeer>();

        if (!_peerToLobby.TryGetValue(senderPeerId, out var lobbyId)
            || !_sessions.TryGetValue(lobbyId, out var session))
        {
            return false;
        }

        lock (session.Gate)
        {
            if (!session.CueBroadcast)
            {
                return false;
            }

            var list = new List<NetPeer>(session.Peers.Count - 1);
            foreach (var (peerId, peer) in session.Peers)
            {
                if (peerId != senderPeerId)
                {
                    list.Add(peer);
                }
            }
            targets = list;
            return true;
        }
    }

    /// <summary>
    /// Removes the peer from its session. Returns the peer's lobby and the remaining peers (so the
    /// caller can broadcast RemotePeerLeft). The peer is dropped from ready/completed tracking too;
    /// remaining peers' completion checks then no longer block on the dropped peer.
    /// </summary>
    public DisconnectResult RemovePeer(int peerId)
    {
        if (!_peerToLobby.TryRemove(peerId, out var lobbyId)
            || !_sessions.TryGetValue(lobbyId, out var session))
        {
            return new DisconnectResult(null, Array.Empty<NetPeer>(), false, false);
        }

        lock (session.Gate)
        {
            session.Peers.Remove(peerId);
            session.Loadouts.Remove(peerId);
            session.ReadyPeers.Remove(peerId);
            session.CompletedPeers.Remove(peerId);

            if (session.Peers.Count == 0)
            {
                _sessions.TryRemove(lobbyId, out _);
                return new DisconnectResult(lobbyId, Array.Empty<NetPeer>(), false, false);
            }

            // If the dropped peer was the last we were waiting on, surface the corresponding
            // transition so the caller can broadcast cue/end without further input.
            bool readyToCueNow = !session.CueBroadcast
                                 && session.Peers.Count >= session.ExpectedMembers
                                 && session.ReadyPeers.Count >= session.Peers.Count;
            if (readyToCueNow)
            {
                session.CueBroadcast = true;
            }

            bool readyToEndNow = session.CueBroadcast
                                 && !session.EndBroadcast
                                 && session.CompletedPeers.Count >= session.Peers.Count
                                 && session.CompletedPeers.Count > 0;
            if (readyToEndNow)
            {
                session.EndBroadcast = true;
            }

            return new DisconnectResult(
                lobbyId,
                SnapshotPeers(session),
                readyToCueNow,
                readyToEndNow);
        }
    }

    private static TryStartResult TryClaimStart(GameSession session)
    {
        if (!session.Started
            && session.Peers.Count >= session.ExpectedMembers
            && session.Loadouts.Count >= session.ExpectedMembers)
        {
            session.Started = true;
            var peerLoadouts = new PeerSession[session.Peers.Count];
            int i = 0;
            foreach (var (peerId, peer) in session.Peers)
            {
                // session.Loadouts is guaranteed to have an entry for every peer because
                // Loadouts.Count == ExpectedMembers and TryStoreLoadout requires the peer to
                // be present.
                peerLoadouts[i++] = new PeerSession(peerId, peer, session.Loadouts[peerId]);
            }
            return new TryStartResult(ReadyToStart: true, peerLoadouts);
        }

        return new TryStartResult(ReadyToStart: false, Array.Empty<PeerSession>());
    }

    private static IReadOnlyList<NetPeer> SnapshotPeers(GameSession session)
    {
        var arr = new NetPeer[session.Peers.Count];
        int i = 0;
        foreach (var peer in session.Peers.Values)
        {
            arr[i++] = peer;
        }
        return arr;
    }

    private sealed class GameSession
    {
        public GameSession(string lobbyId, int expectedMembers)
        {
            LobbyId = lobbyId;
            ExpectedMembers = expectedMembers;
        }

        public string LobbyId { get; }
        public int ExpectedMembers { get; }
        public Dictionary<int, NetPeer> Peers { get; } = new();
        public Dictionary<int, SetLoadoutPacket> Loadouts { get; } = new();
        public HashSet<int> ReadyPeers { get; } = new();
        public HashSet<int> CompletedPeers { get; } = new();
        public DateTimeOffset? FirstCompletionAt { get; set; }
        public bool Started { get; set; }
        public bool CueBroadcast { get; set; }
        public bool EndBroadcast { get; set; }
        // Set when the host sends SongMetadataPacket. Read at cue-broadcast time and forwarded to
        // the lobbies service so the lobby browser can render a progress bar. Null until set;
        // GameNetworkService treats a missing value as "host never sent metadata" and falls back
        // to 0 so the lobbies service can still record a start time.
        public int? DurationMs { get; set; }
        public object Gate { get; } = new();
    }
}

public readonly record struct PeerSession(int PeerId, NetPeer Peer, SetLoadoutPacket Loadout);

public readonly record struct TryStartResult(bool ReadyToStart, IReadOnlyList<PeerSession> Peers);

public readonly record struct CueReadyResult(bool ReadyToCue, IReadOnlyList<NetPeer> Peers);

public readonly record struct CompletionResult(
    bool WasFirstCompletion,
    bool ReadyToEnd,
    string LobbyId,
    IReadOnlyList<NetPeer> Peers);

public readonly record struct DisconnectResult(
    string? LobbyId,
    IReadOnlyList<NetPeer> RemainingPeers,
    bool ReadyToCueNow,
    bool ReadyToEndNow);
