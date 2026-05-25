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
    /// Store this peer's loadout selection. Idempotent per peer — re-submissions
    /// overwrite the previously-stored loadout (a player may "unready" on the
    /// DifficultySelect screen and re-Ready with different choices before the
    /// session starts). Returns false only if no session exists for the lobby
    /// or the peer isn't connected. Once <see cref="GameSession.Started"/> is
    /// true, <see cref="TryClaimStart"/> won't re-broadcast, so late re-submissions
    /// after the game has already begun are silently ignored on the broadcast side.
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

            // Upsert — duplicates are not an error, they replace the previous loadout.
            session.Loadouts[peerId] = loadout;

            result = TryClaimStart(session);
        }

        return true;
    }

    /// <summary>
    /// Drop a peer's previously-submitted loadout. Used by DifficultySelect "Unready" so
    /// the peer can resubmit with new selections. Returns false if the session is missing,
    /// the peer isn't connected, has no loadout stored, or the game has already started
    /// (post-start retraction is not allowed — see comment on <see cref="TryStoreLoadout"/>).
    /// </summary>
    public bool TryRemoveLoadout(string lobbyId, int peerId)
    {
        if (!_sessions.TryGetValue(lobbyId, out var session))
        {
            return false;
        }

        lock (session.Gate)
        {
            if (session.Started)
            {
                return false;
            }
            if (!session.Peers.ContainsKey(peerId))
            {
                return false;
            }
            return session.Loadouts.Remove(peerId);
        }
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
    /// Marks the peer as having reached end-of-chart. Returns whether all peers are now done
    /// (so the caller broadcasts GameEnd). A disconnected peer is implicitly out and doesn't
    /// block end-of-game — the dropped peer's <see cref="RemovePeer"/> removes them from both
    /// Peers and CompletedPeers, so the "all done" check sees the trimmed set.
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

            bool allDone = session.CompletedPeers.Count >= session.Peers.Count;
            if (allDone)
            {
                session.EndBroadcast = true;
            }

            result = new CompletionResult(
                ReadyToEnd: allDone,
                LobbyId: lobbyId,
                Peers: allDone ? SnapshotPeers(session) : Array.Empty<NetPeer>());
            return true;
        }
    }

    /// <summary>
    /// Force-end a session without waiting for natural completion. Used by the chart-hash-mismatch
    /// abort path in <see cref="TryClaimStart"/>: when peers submit incompatible chart hashes the
    /// session can't proceed, so we mark it ended and return the peer list for the GameEnd broadcast.
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
    /// Hot-path: called for every fan-out packet. Returns true and the list of *other* peers
    /// in the session (excluding the sender) only when the session is live (GameStartCue broadcast).
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
            // Gate: every peer must have submitted the same chart hash, and
            // it must not be all-zero ("not supplied"). Mismatch is fatal for
            // the session — note-index events would refer to different notes
            // on different clients, silently desyncing the prediction layer.
            byte[]? reference = null;
            foreach (var loadout in session.Loadouts.Values)
            {
                if (IsAllZero(loadout.ChartHash))
                {
                    session.Started = true; // claim once so we don't loop forever
                    return new TryStartResult(
                        ReadyToStart: false,
                        Peers: Array.Empty<PeerSession>(),
                        ChartMismatch: true);
                }
                if (reference is null)
                {
                    reference = loadout.ChartHash;
                }
                else if (!HashesEqual(reference, loadout.ChartHash))
                {
                    session.Started = true;
                    return new TryStartResult(
                        ReadyToStart: false,
                        Peers: Array.Empty<PeerSession>(),
                        ChartMismatch: true);
                }
            }

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

    private static bool IsAllZero(byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0) return false;
        }
        return true;
    }

    private static bool HashesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
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
        public bool Started { get; set; }
        public bool CueBroadcast { get; set; }
        public bool EndBroadcast { get; set; }
        public int? DurationMs { get; set; }
        public object Gate { get; } = new();
    }
}

public readonly record struct PeerSession(int PeerId, NetPeer Peer, SetLoadoutPacket Loadout);

public readonly record struct TryStartResult(
    bool ReadyToStart,
    IReadOnlyList<PeerSession> Peers,
    bool ChartMismatch = false);

public readonly record struct CueReadyResult(bool ReadyToCue, IReadOnlyList<NetPeer> Peers);

public readonly record struct CompletionResult(
    bool ReadyToEnd,
    string LobbyId,
    IReadOnlyList<NetPeer> Peers);

public readonly record struct DisconnectResult(
    string? LobbyId,
    IReadOnlyList<NetPeer> RemainingPeers,
    bool ReadyToCueNow,
    bool ReadyToEndNow);
