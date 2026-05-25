using System.Collections.Concurrent;
using LiteNetLib;
using LiteNetLib.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YARG.Online.Game.Agones;
using YARG.Online.Game.Auth;
using YARG.Online.Game.Contracts.Packets;
using YARG.Online.Game.Lobbies;

namespace YARG.Online.Game.Networking;

public sealed class GameNetworkService : BackgroundService
{
    // Time between the cue broadcast and the resolved song-time origin. Each client computes its
    // local SongRunner start using SongOriginUtcMs, so this is essentially the pre-roll budget for
    // every client to see the cue + start their countdown UI.
    private static readonly TimeSpan StartCueLead = TimeSpan.FromMilliseconds(3000);
    private const int StartCueCountdownMs = 3000;

    // Once the first peer reports GameComplete, give the rest this long to finish before
    // force-ending the session. Covers crashed/lagged clients without leaving the live ones in
    // limbo forever.
    private static readonly TimeSpan StragglerTimeout = TimeSpan.FromSeconds(30);

    private readonly NetworkOptions _options;
    private readonly IGameJwtValidator _validator;
    private readonly AuthenticatedPeerRegistry _registry;
    private readonly GameSessionManager _sessions;
    private readonly ILobbiesClient _lobbies;
    private readonly AgonesReadinessSignal _readiness;
    private readonly IRelaySender _relay;
    private readonly ILogger<GameNetworkService> _logger;
    private NetManager? _manager;

    // Pending identities keyed by remote endpoint (as a string —
    // "ip:port") so the key is identical between a ConnectionRequest's
    // RemoteEndPoint.ToString() and a NetPeer.ToString(). Populated
    // *before* request.Accept() so OnPeerConnected can find the identity
    // even when LiteNetLib raises PeerConnectedEvent synchronously from
    // inside Accept() (which it does — the peer object is fully wired
    // before Accept returns). The registry add by peer.Id happens in
    // OnPeerConnected once we know the LiteNetLib-assigned peer id.
    private readonly ConcurrentDictionary<string, AuthenticatedPeer> _pendingByEndpoint = new();

    public GameNetworkService(
        IOptions<NetworkOptions> options,
        IGameJwtValidator validator,
        AuthenticatedPeerRegistry registry,
        GameSessionManager sessions,
        ILobbiesClient lobbies,
        AgonesReadinessSignal readiness,
        IRelaySender relay,
        ILogger<GameNetworkService> logger)
    {
        _options = options.Value;
        _validator = validator;
        _registry = registry;
        _sessions = sessions;
        _lobbies = lobbies;
        _readiness = readiness;
        _relay = relay;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new EventBasedNetListener();
        _manager = new NetManager(listener)
        {
            UnsyncedEvents = true,
            PingInterval = 1000,
            DisconnectTimeout = 15000,
        };

        listener.ConnectionRequestEvent += OnConnectionRequest;
        listener.PeerConnectedEvent += OnPeerConnected;
        listener.PeerDisconnectedEvent += OnPeerDisconnected;
        listener.NetworkReceiveEvent += OnNetworkReceive;
        listener.NetworkErrorEvent += OnNetworkError;

        if (!_manager.Start(_options.Port))
        {
            _logger.LogError("Failed to start NetManager on UDP port {Port}.", _options.Port);
            return;
        }

        _logger.LogInformation(
            "GameNetworkService listening on UDP port {Port} (max {MaxConnections} peers).",
            _manager.LocalPort, _options.MaxConnections);

        // Signals AgonesReadyService (if registered) that the socket is bound and the
        // pod is eligible to be marked Ready. No-op when Agones isn't wired in.
        _readiness.Set();

        try
        {
            // Hold here until the host shuts us down.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _manager.Stop();
            _logger.LogInformation("GameNetworkService stopped.");
        }
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (_registry.Count >= _options.MaxConnections)
        {
            _logger.LogWarning(
                "Rejected {Endpoint}: capacity ({Max}) reached.",
                request.RemoteEndPoint, _options.MaxConnections);
            request.Reject();
            return;
        }

        string? jwt;
        try
        {
            jwt = request.Data.GetString(4096);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Rejected {Endpoint}: malformed handshake payload.", request.RemoteEndPoint);
            request.Reject();
            return;
        }

        var result = _validator.Validate(jwt ?? string.Empty);
        if (!result.IsValid)
        {
            _logger.LogWarning(
                "Rejected {Endpoint}: JWT failed — {Reason}.", request.RemoteEndPoint, result.FailureReason);
            request.Reject();
            return;
        }

        var identity = new AuthenticatedPeer(
            result.UserId!,
            result.DisplayName ?? result.UserId!,
            result.LobbyId!,
            result.ExpectedMembers,
            result.IsHost);

        // Stage the identity by remote endpoint BEFORE Accept(). LiteNetLib
        // raises PeerConnectedEvent synchronously inside Accept() under
        // UnsyncedEvents — if we add to the peer-id-keyed registry only after
        // Accept returns, OnPeerConnected runs first and finds nothing. The
        // endpoint key is the only stable identifier we have before
        // LiteNetLib assigns peer.Id. Both ConnectionRequest.RemoteEndPoint
        // and NetPeer share the same "ip:port" ToString() form.
        var endpointKey = request.RemoteEndPoint.ToString();
        _pendingByEndpoint[endpointKey] = identity;

        NetPeer? peer = null;
        try
        {
            peer = request.Accept();
        }
        finally
        {
            // If Accept failed (peer == null) or OnPeerConnected has already
            // consumed the staged entry, this TryRemove no-ops.
            if (peer is null)
            {
                _pendingByEndpoint.TryRemove(endpointKey, out _);
            }
        }
    }

    private void OnPeerConnected(NetPeer peer)
    {
        // Pull the staged identity by endpoint, then promote it into the
        // peer-id-keyed registry. From this point on, every other handler
        // (including the receive path) keys on peer.Id, so the endpoint
        // entry's job is done.
        var endpointKey = peer.ToString();
        AuthenticatedPeer? identity = null;
        if (_pendingByEndpoint.TryRemove(endpointKey, out var staged))
        {
            identity = staged;
        }

        if (identity is null)
        {
            _logger.LogWarning(
                "Peer {PeerId} connected without a staged identity — disconnecting.", peer.Id);
            peer.Disconnect();
            return;
        }

        if (!_registry.TryAdd(peer.Id, identity))
        {
            _logger.LogWarning(
                "Peer {PeerId} already in registry — disconnecting duplicate.", peer.Id);
            peer.Disconnect();
            return;
        }

        _logger.LogInformation(
            "Peer connected: {PeerId} user={UserId} lobby={LobbyId} from {Endpoint}.",
            peer.Id, identity.UserId, identity.LobbyId, peer);

        var addResult = _sessions.TryAddPeer(identity.LobbyId, identity.ExpectedMembers, peer);
        TryFireGameStart(identity.LobbyId, addResult);
    }

    private void TryFireGameStart(string lobbyId, TryStartResult result)
    {
        if (result.ChartMismatch)
        {
            // Chart hashes disagreed across peers.
            _logger.LogWarning(
                "Lobby {LobbyId}: chart hash mismatch across peers — aborting session.",
                lobbyId);
            var forced = _sessions.ForceEndSession(lobbyId);
            if (forced is { ReadyToEnd: true, Peers: { Count: > 0 } peers })
            {
                BroadcastGameEnd(peers, lobbyId);
            }
            return;
        }

        if (!result.ReadyToStart) return;

        BroadcastGameStart(lobbyId, result.Peers);
    }

    internal void BroadcastGameStart(string lobbyId, IReadOnlyList<PeerSession> peers)
    {
        _logger.LogInformation(
            "Lobby {LobbyId} quorum reached ({Count} peers) — broadcasting GameStart.",
            lobbyId, peers.Count);

        // Build the per-peer loadout array by joining each session's loadout with its registry
        // identity (UserId/DisplayName).
        var loadouts = new PeerLoadout[peers.Count];
        for (int i = 0; i < peers.Count; i++)
        {
            var ps = peers[i];
            string userId = ps.PeerId.ToString();
            string displayName = userId;
            if (_registry.TryGet(ps.PeerId, out var identity) && identity is not null)
            {
                userId = identity.UserId;
                displayName = identity.DisplayName;
            }
            loadouts[i] = new PeerLoadout
            {
                PeerId = ps.PeerId,
                UserId = userId,
                DisplayName = displayName,
                Instrument = ps.Loadout.Instrument,
                Difficulty = ps.Loadout.Difficulty,
                EnginePreset = ps.Loadout.EnginePreset,
                NoteSpeed = ps.Loadout.NoteSpeed,
                Modifiers = ps.Loadout.Modifiers,
            };
        }

        var startWriter = new NetDataWriter();
        GamePacketWriter.Write(startWriter, PacketOpcode.GameStart, new GameStartPacket { Loadouts = loadouts });
        foreach (var ps in peers)
        {
            ps.Peer.Send(startWriter, DeliveryMethod.ReliableOrdered);
        }
    }

    private void BroadcastStartCue(IReadOnlyList<NetPeer> peers, string lobbyId)
    {
        var cue = new GameStartCuePacket
        {
            SongOriginUtcMs = DateTimeOffset.UtcNow.Add(StartCueLead).ToUnixTimeMilliseconds(),
            CountdownMs = StartCueCountdownMs,
        };
        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.GameStartCue, cue);
        foreach (var peer in peers)
        {
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }
        _logger.LogInformation(
            "Lobby {LobbyId}: GameStartCue broadcast to {Count} peers (origin={OriginUtcMs}).",
            lobbyId, peers.Count, cue.SongOriginUtcMs);

        // Tell the lobbies service when the song actually starts so the browser can render a
        // progress bar. DurationMs comes from the host's SongMetadataPacket; if the host crashed
        // before sending we still post (with duration=0) so SongStartedAt is at least set.
        var durationMs = _sessions.GetSongDurationMs(lobbyId);
        var originUtcMs = cue.SongOriginUtcMs;
        _ = Task.Run(() => NotifySongStartedAsync(lobbyId, originUtcMs, durationMs));
    }

    private void BroadcastGameEnd(IReadOnlyList<NetPeer> peers, string lobbyId)
    {
        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.GameEnd, new GameEndPacket());
        foreach (var peer in peers)
        {
            peer.Send(writer, DeliveryMethod.ReliableOrdered);
        }
        _sessions.DisposeSession(lobbyId);
        _logger.LogInformation(
            "Lobby {LobbyId}: GameEnd broadcast to {Count} peers; session disposed.",
            lobbyId, peers.Count);

        // Tell the lobby hub the game finished so the lobby status returns to SongSelect.
        // Fire-and-forget — the HTTP call must not block the LiteNetLib polling thread.
        _ = Task.Run(() => NotifyLobbiesFinishedAsync(lobbyId));
    }

    private async Task NotifyLobbiesFinishedAsync(string lobbyId)
    {
        try
        {
            await _lobbies.FinishGameAsync(lobbyId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify Lobbies of game-finished for lobby {LobbyId}.", lobbyId);
        }
    }

    private async Task NotifySongStartedAsync(string lobbyId, long songOriginUtcMs, int durationMs)
    {
        try
        {
            await _lobbies.SongStartedAsync(lobbyId, songOriginUtcMs, durationMs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify Lobbies of song-started for lobby {LobbyId}.", lobbyId);
        }
    }

    private void StartStragglerTimer(string lobbyId)
    {
        var stragglerToken = _sessions.GetStragglerCancellation(lobbyId);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StragglerTimeout, stragglerToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            var forced = _sessions.ForceEndSession(lobbyId);
            if (forced is { ReadyToEnd: true, Peers: { Count: > 0 } peers })
            {
                _logger.LogWarning(
                    "Lobby {LobbyId}: straggler timeout elapsed; force-ending session ({Count} peers).",
                    lobbyId, peers.Count);
                BroadcastGameEnd(peers, lobbyId);
            }
        });
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        // Clean up the staging slot in case the disconnect fired before
        // OnPeerConnected ever ran (extremely rare — but cheap to guard).
        _pendingByEndpoint.TryRemove(peer.ToString(), out _);

        _registry.TryRemove(peer.Id, out _);
        var dc = _sessions.RemovePeer(peer.Id);
        _logger.LogInformation(
            "Peer disconnected: {PeerId} reason={Reason}.", peer.Id, info.Reason);

        if (dc.LobbyId is null || dc.RemainingPeers.Count == 0)
        {
            return;
        }

        // Tell the survivors which peer dropped, so they can mark its highway DNF and stop
        // expecting EngineInput packets for that PeerId.
        var leftWriter = new NetDataWriter();
        GamePacketWriter.Write(leftWriter, PacketOpcode.RemotePeerLeft, new RemotePeerLeftPacket { PeerId = peer.Id });
        foreach (var remaining in dc.RemainingPeers)
        {
            remaining.Send(leftWriter, DeliveryMethod.ReliableOrdered);
        }

        // The disconnect may have unblocked a cue (last unready peer left) or end (last
        // un-completed peer left). Both transitions are handled inline by RemovePeer; act on them.
        if (dc.ReadyToCueNow)
        {
            BroadcastStartCue(dc.RemainingPeers, dc.LobbyId);
        }
        if (dc.ReadyToEndNow)
        {
            BroadcastGameEnd(dc.RemainingPeers, dc.LobbyId);
        }
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        try
        {
            if (reader.AvailableBytes < 1)
            {
                _logger.LogWarning("Peer {PeerId} sent an empty packet.", peer.Id);
                return;
            }

            var opcode = GamePacketReader.ReadOpcode(reader);
            switch (opcode)
            {
                case PacketOpcode.SetLoadout:
                    HandleSetLoadout(peer, reader);
                    break;
                case PacketOpcode.ClearLoadout:
                    HandleClearLoadout(peer);
                    break;
                case PacketOpcode.NoteMissed:
                    HandleNoteMissed(peer, reader);
                    break;
                case PacketOpcode.StarPowerActivated:
                    HandleStarPowerActivated(peer, reader);
                    break;
                case PacketOpcode.Whammy:
                    HandleWhammy(peer, reader);
                    break;
                case PacketOpcode.VocalPitch:
                    HandleVocalPitch(peer, reader);
                    break;
                case PacketOpcode.FreePlayInput:
                    HandleFreePlayInput(peer, reader);
                    break;
                case PacketOpcode.SustainReleased:
                    HandleSustainReleased(peer, reader);
                    break;
                case PacketOpcode.Overstrum:
                    HandleOverstrum(peer, reader);
                    break;
                case PacketOpcode.NoteHit:
                    HandleNoteHit(peer, reader);
                    break;
                case PacketOpcode.EngineStateSnapshot:
                    HandleEngineStateSnapshot(peer, reader);
                    break;
                case PacketOpcode.PeerReady:
                    HandlePeerReady(peer);
                    break;
                case PacketOpcode.GameComplete:
                    HandleGameComplete(peer);
                    break;
                case PacketOpcode.SongMetadata:
                    HandleSongMetadata(peer, reader);
                    break;
                case PacketOpcode.Ping:
                    HandlePing(peer, reader);
                    break;
                default:
                    _logger.LogWarning("Peer {PeerId} sent unknown/unexpected opcode {Opcode}.", peer.Id, opcode);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process packet from {PeerId}.", peer.Id);
        }
        finally
        {
            reader.Recycle();
        }
    }

    private void HandleSetLoadout(NetPeer peer, NetPacketReader reader)
    {
        if (!_registry.TryGet(peer.Id, out var identity) || identity is null)
        {
            _logger.LogWarning("SetLoadout from unregistered peer {PeerId}; ignoring.", peer.Id);
            return;
        }

        var packet = new SetLoadoutPacket();
        packet.Deserialize(reader);

        if (!_sessions.TryStoreLoadout(identity.LobbyId, peer.Id, packet, out var result))
        {
            _logger.LogWarning(
                "SetLoadout rejected for peer {PeerId} lobby {LobbyId} (no session or peer not connected).",
                peer.Id, identity.LobbyId);
            return;
        }

        _logger.LogInformation(
            "Loadout received from {UserId} ({Instrument}/{Difficulty}) for lobby {LobbyId}.",
            identity.UserId, packet.Instrument, packet.Difficulty, identity.LobbyId);

        TryFireGameStart(identity.LobbyId, result);
    }

    private void HandleClearLoadout(NetPeer peer)
    {
        if (!_registry.TryGet(peer.Id, out var identity) || identity is null)
        {
            _logger.LogWarning("ClearLoadout from unregistered peer {PeerId}; ignoring.", peer.Id);
            return;
        }

        if (_sessions.TryRemoveLoadout(identity.LobbyId, peer.Id))
        {
            _logger.LogInformation(
                "Loadout cleared (unready) by {UserId} for lobby {LobbyId}.",
                identity.UserId, identity.LobbyId);
        }
        else
        {
            // Either the session is missing, the peer wasn't connected, or the game already
            // started. None are fatal — this is a passive request and a silent failure
            // matches the "if you're quick enough" UX (after game start, unready can't apply).
            _logger.LogDebug(
                "ClearLoadout no-op for peer {PeerId} lobby {LobbyId} (no stored loadout, or game already started).",
                peer.Id, identity.LobbyId);
        }
    }

    private void HandleNoteMissed(NetPeer peer, NetPacketReader reader)
    {
        var packet = new NoteMissedPacket();
        packet.Deserialize(reader);

        if (!_sessions.TryGetFanoutTargets(peer.Id, out var targets) || targets.Count == 0)
        {
            return;
        }

        packet.PeerId = peer.Id;
        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.NoteMissed, packet);
        foreach (var target in targets)
        {
            _relay.Send(target, writer, DeliveryMethod.ReliableOrdered);
        }
    }

    private void HandleStarPowerActivated(NetPeer peer, NetPacketReader reader)
    {
        var packet = new StarPowerActivatedPacket();
        packet.Deserialize(reader);

        if (!_sessions.TryGetFanoutTargets(peer.Id, out var targets) || targets.Count == 0)
        {
            return;
        }

        packet.PeerId = peer.Id;
        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.StarPowerActivated, packet);
        foreach (var target in targets)
        {
            _relay.Send(target, writer, DeliveryMethod.ReliableOrdered);
        }
    }

    private void HandleWhammy(NetPeer peer, NetPacketReader reader)
    {
        var packet = new WhammyPacket();
        packet.Deserialize(reader);

        if (!_sessions.TryGetFanoutTargets(peer.Id, out var targets) || targets.Count == 0)
        {
            return;
        }

        packet.PeerId = peer.Id;
        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.Whammy, packet);
        foreach (var target in targets)
        {
            _relay.Send(target, writer, DeliveryMethod.ReliableOrdered);
        }
    }

    private void HandleVocalPitch(NetPeer peer, NetPacketReader reader)
    {
        var packet = new VocalPitchPacket();
        packet.Deserialize(reader);

        if (!_sessions.TryGetFanoutTargets(peer.Id, out var targets) || targets.Count == 0)
        {
            return;
        }

        packet.PeerId = peer.Id;
        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.VocalPitch, packet);
        foreach (var target in targets)
        {
            _relay.Send(target, writer, DeliveryMethod.ReliableOrdered);
        }
    }

    private void HandleFreePlayInput(NetPeer peer, NetPacketReader reader)
    {
        var packet = new FreePlayInputPacket();
        packet.Deserialize(reader);

        if (!_sessions.TryGetFanoutTargets(peer.Id, out var targets) || targets.Count == 0)
        {
            return;
        }

        packet.PeerId = peer.Id;
        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.FreePlayInput, packet);
        foreach (var target in targets)
        {
            _relay.Send(target, writer, DeliveryMethod.ReliableOrdered);
        }
    }

    private void HandleSustainReleased(NetPeer peer, NetPacketReader reader)
    {
        var packet = new SustainReleasedPacket();
        packet.Deserialize(reader);

        if (!_sessions.TryGetFanoutTargets(peer.Id, out var targets) || targets.Count == 0)
        {
            return;
        }

        packet.PeerId = peer.Id;
        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.SustainReleased, packet);
        foreach (var target in targets)
        {
            _relay.Send(target, writer, DeliveryMethod.ReliableOrdered);
        }
    }

    private void HandleOverstrum(NetPeer peer, NetPacketReader reader)
    {
        var packet = new OverstrumPacket();
        packet.Deserialize(reader);

        if (!_sessions.TryGetFanoutTargets(peer.Id, out var targets) || targets.Count == 0)
        {
            return;
        }

        packet.PeerId = peer.Id;
        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.Overstrum, packet);
        foreach (var target in targets)
        {
            _relay.Send(target, writer, DeliveryMethod.ReliableOrdered);
        }
    }

    private void HandleNoteHit(NetPeer peer, NetPacketReader reader)
    {
        var packet = new NoteHitPacket();
        packet.Deserialize(reader);

        if (!_sessions.TryGetFanoutTargets(peer.Id, out var targets) || targets.Count == 0)
        {
            return;
        }

        packet.PeerId = peer.Id;
        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.NoteHit, packet);
        foreach (var target in targets)
        {
            _relay.Send(target, writer, DeliveryMethod.ReliableOrdered);
        }
    }

    private void HandleEngineStateSnapshot(NetPeer peer, NetPacketReader reader)
    {
        var packet = new EngineStateSnapshotPacket();
        packet.Deserialize(reader);

        if (!_sessions.TryGetFanoutTargets(peer.Id, out var targets) || targets.Count == 0)
        {
            return;
        }

        packet.PeerId = peer.Id;
        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.EngineStateSnapshot, packet);
        foreach (var target in targets)
        {
            _relay.Send(target, writer, DeliveryMethod.ReliableOrdered);
        }
    }

    private void HandlePeerReady(NetPeer peer)
    {
        if (!_registry.TryGet(peer.Id, out var identity) || identity is null)
        {
            _logger.LogWarning("PeerReady from unregistered peer {PeerId}; ignoring.", peer.Id);
            return;
        }

        if (!_sessions.TryMarkReady(peer.Id, out var cue))
        {
            return;
        }

        _logger.LogInformation(
            "PeerReady received from {UserId} for lobby {LobbyId}.",
            identity.UserId, identity.LobbyId);

        if (cue.ReadyToCue)
        {
            BroadcastStartCue(cue.Peers, identity.LobbyId);
        }
    }

    private void HandlePing(NetPeer peer, NetPacketReader reader)
    {
        var packet = new PingPacket();
        packet.Deserialize(reader);

        // Sample server UTC as late as possible before serialization so the value the
        // client sees is as close as possible to "the instant the server saw the ping."
        var pong = new PongPacket
        {
            ClientTickMs = packet.ClientTickMs,
            ServerUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.Pong, pong);
        peer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private void HandleSongMetadata(NetPeer peer, NetPacketReader reader)
    {
        if (!_registry.TryGet(peer.Id, out var identity) || identity is null)
        {
            _logger.LogWarning("SongMetadata from unregistered peer {PeerId}; ignoring.", peer.Id);
            return;
        }

        if (!identity.IsHost)
        {
            _logger.LogWarning(
                "SongMetadata from non-host peer {PeerId} user={UserId} lobby={LobbyId}; ignoring.",
                peer.Id, identity.UserId, identity.LobbyId);
            return;
        }

        var packet = new SongMetadataPacket();
        packet.Deserialize(reader);

        if (!_sessions.TryStoreSongDuration(identity.LobbyId, packet.DurationMs))
        {
            _logger.LogWarning(
                "SongMetadata rejected for lobby {LobbyId} (no session, already set, or cue already broadcast).",
                identity.LobbyId);
            return;
        }

        _logger.LogInformation(
            "SongMetadata recorded for lobby {LobbyId}: DurationMs={DurationMs}.",
            identity.LobbyId, packet.DurationMs);
    }

    private void HandleGameComplete(NetPeer peer)
    {
        if (!_registry.TryGet(peer.Id, out var identity) || identity is null)
        {
            _logger.LogWarning("GameComplete from unregistered peer {PeerId}; ignoring.", peer.Id);
            return;
        }

        if (!_sessions.TryMarkCompleted(peer.Id, out var done))
        {
            return;
        }

        _logger.LogInformation(
            "GameComplete received from {UserId} for lobby {LobbyId} (first={First}, allDone={AllDone}).",
            identity.UserId, identity.LobbyId, done.WasFirstCompletion, done.ReadyToEnd);

        if (done.WasFirstCompletion && !done.ReadyToEnd)
        {
            StartStragglerTimer(done.LobbyId);
        }

        if (done.ReadyToEnd)
        {
            BroadcastGameEnd(done.Peers, done.LobbyId);
        }
    }

    private void OnNetworkError(System.Net.IPEndPoint endpoint, System.Net.Sockets.SocketError error)
    {
        _logger.LogError("Network error from {Endpoint}: {Error}.", endpoint, error);
    }
}
