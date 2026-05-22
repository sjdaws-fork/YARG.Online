using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using YARG.Online.Game.Agones;
using YARG.Online.Game.Auth;
using YARG.Online.Game.Contracts.Enums;
using YARG.Online.Game.Contracts.Packets;
using YARG.Online.Game.Lobbies;
using YARG.Online.Game.Networking;

namespace YARG.Online.Game.Tests.Networking;

public class GameNetworkServiceIntegrationTests : IAsyncLifetime
{
    private const string ConnectionKey = "yarg-online-game-test";
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private IHost _host = null!;
    private int _serverPort;
    private AuthenticatedPeerRegistry _registry = null!;
    private RecordingLobbiesClient _lobbies = null!;

    public Task InitializeAsync() => StartHostAsync(maxConnections: 8);

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private async Task StartHostAsync(int maxConnections)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _serverPort = GetFreeUdpPort();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Network:Port"] = _serverPort.ToString(),
            ["Network:ConnectionKey"] = ConnectionKey,
            ["Network:MaxConnections"] = maxConnections.ToString(),
            ["GameAuth:Issuer"] = TestJwt.DefaultIssuer,
            ["GameAuth:Audience"] = TestJwt.DefaultAudience,
            ["GameAuth:SigningSecret"] = TestJwt.DefaultSecret,
            ["GameAuth:ClockSkew"] = "00:00:05",
        });

        builder.Services.AddOptions<NetworkOptions>()
            .Bind(builder.Configuration.GetSection(NetworkOptions.SectionName));

        builder.Services.AddOptions<GameAuthOptions>()
            .Bind(builder.Configuration.GetSection(GameAuthOptions.SectionName));

        builder.Services.AddSingleton<IGameJwtValidator, GameJwtValidator>();
        builder.Services.AddSingleton<AuthenticatedPeerRegistry>();
        builder.Services.AddSingleton<GameSessionManager>();
        builder.Services.AddSingleton<AgonesReadinessSignal>();
        builder.Services.AddSingleton<RecordingLobbiesClient>();
        builder.Services.AddSingleton<ILobbiesClient>(sp => sp.GetRequiredService<RecordingLobbiesClient>());
        builder.Services.AddHostedService<GameNetworkService>();

        _host = builder.Build();
        await _host.StartAsync();
        _registry = _host.Services.GetRequiredService<AuthenticatedPeerRegistry>();
        _lobbies = _host.Services.GetRequiredService<RecordingLobbiesClient>();

        // Give the hosted service a moment to bind its socket before tests start connecting.
        await Task.Delay(100);
    }

    [Fact]
    public async Task Valid_token_is_accepted_and_added_to_registry()
    {
        // expected_members: 99 keeps the session below quorum so this test only asserts on registry state.
        var token = TestJwt.Mint(userId: "u_alice", displayName: "Alice", expectedMembers: 99);
        await using var client = new TestClient();

        var outcome = await client.ConnectAndWait(_serverPort, ConnectionKey, token);

        Assert.Equal(HandshakeOutcome.Connected, outcome);
        await WaitForAsync(() => _registry.Count == 1);
        var connectedPeerId = client.ConnectedPeer!.RemoteId;
        Assert.True(_registry.TryGet(connectedPeerId, out var peer));
        Assert.Equal("u_alice", peer!.UserId);
        Assert.Equal("Alice", peer.DisplayName);
    }

    [Fact]
    public async Task Wrong_connection_key_is_rejected()
    {
        var token = TestJwt.Mint();
        await using var client = new TestClient();

        var outcome = await client.ConnectAndWait(_serverPort, "wrong-key", token);

        Assert.Equal(HandshakeOutcome.Rejected, outcome);
        Assert.Equal(0, _registry.Count);
    }

    [Fact]
    public async Task Token_with_wrong_audience_is_rejected()
    {
        var token = TestJwt.Mint(audience: "yarg-api");
        await using var client = new TestClient();

        var outcome = await client.ConnectAndWait(_serverPort, ConnectionKey, token);

        Assert.Equal(HandshakeOutcome.Rejected, outcome);
        Assert.Equal(0, _registry.Count);
    }

    [Fact]
    public async Task Token_with_wrong_issuer_is_rejected()
    {
        var token = TestJwt.Mint(issuer: "evil-issuer");
        await using var client = new TestClient();

        var outcome = await client.ConnectAndWait(_serverPort, ConnectionKey, token);

        Assert.Equal(HandshakeOutcome.Rejected, outcome);
        Assert.Equal(0, _registry.Count);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var token = TestJwt.Mint(
            now: DateTimeOffset.UtcNow - TimeSpan.FromHours(2),
            lifetime: TimeSpan.FromMinutes(5));
        await using var client = new TestClient();

        var outcome = await client.ConnectAndWait(_serverPort, ConnectionKey, token);

        Assert.Equal(HandshakeOutcome.Rejected, outcome);
        Assert.Equal(0, _registry.Count);
    }

    [Fact]
    public async Task Token_signed_with_different_secret_is_rejected()
    {
        var token = TestJwt.Mint(secret: "a-completely-different-secret-32-bytes!!");
        await using var client = new TestClient();

        var outcome = await client.ConnectAndWait(_serverPort, ConnectionKey, token);

        Assert.Equal(HandshakeOutcome.Rejected, outcome);
        Assert.Equal(0, _registry.Count);
    }

    [Fact]
    public async Task MaxConnections_blocks_additional_authed_clients()
    {
        await StartHostAsync(maxConnections: 1);

        var token1 = TestJwt.Mint(userId: "u_alice", expectedMembers: 99);
        var token2 = TestJwt.Mint(userId: "u_bob", expectedMembers: 99);

        await using var client1 = new TestClient();
        var outcome1 = await client1.ConnectAndWait(_serverPort, ConnectionKey, token1);
        Assert.Equal(HandshakeOutcome.Connected, outcome1);
        await WaitForAsync(() => _registry.Count == 1);

        await using var client2 = new TestClient();
        var outcome2 = await client2.ConnectAndWait(_serverPort, ConnectionKey, token2);

        Assert.Equal(HandshakeOutcome.Rejected, outcome2);
        Assert.Equal(1, _registry.Count);
    }

    [Fact]
    public async Task Quorum_plus_loadouts_broadcasts_GameStart_only()
    {
        // GameEnd must NOT follow immediately — the session stays live until clients send GameComplete.
        const string lobby = "lob_start_only";
        var hostToken = TestJwt.Mint(userId: "u_alice", lobbyId: lobby, expectedMembers: 2, isHost: true);
        var clientToken = TestJwt.Mint(userId: "u_bob", lobbyId: lobby, expectedMembers: 2);

        await using var host = new TestClient();
        await using var other = new TestClient();

        Assert.Equal(HandshakeOutcome.Connected, await host.ConnectAndWait(_serverPort, ConnectionKey, hostToken));
        Assert.Equal(HandshakeOutcome.Connected, await other.ConnectAndWait(_serverPort, ConnectionKey, clientToken));

        host.SendLoadout();
        other.SendLoadout();

        await WaitForAsync(() => host.ReceivedOpcodes.Count >= 1 && other.ReceivedOpcodes.Count >= 1);

        Assert.Equal(PacketOpcode.GameStart, host.ReceivedOpcodes.First());
        Assert.Equal(PacketOpcode.GameStart, other.ReceivedOpcodes.First());

        // No GameEnd should follow — give the server a generous window to misbehave.
        await Task.Delay(300);
        Assert.DoesNotContain(PacketOpcode.GameEnd, host.ReceivedOpcodes);
        Assert.DoesNotContain(PacketOpcode.GameEnd, other.ReceivedOpcodes);
    }

    [Fact]
    public async Task Quorum_plus_loadouts_does_not_call_Lobbies_finish()
    {
        // Lobbies.FinishGame must only fire when the game ends, not when it starts —
        // otherwise the lobby flips back to SongSelect while the song is still being played.
        const string lobby = "lob_no_finish_on_start";
        var hostToken = TestJwt.Mint(userId: "u_alice", lobbyId: lobby, expectedMembers: 2, isHost: true);
        var clientToken = TestJwt.Mint(userId: "u_bob", lobbyId: lobby, expectedMembers: 2);

        await using var host = new TestClient();
        await using var other = new TestClient();

        Assert.Equal(HandshakeOutcome.Connected, await host.ConnectAndWait(_serverPort, ConnectionKey, hostToken));
        Assert.Equal(HandshakeOutcome.Connected, await other.ConnectAndWait(_serverPort, ConnectionKey, clientToken));

        host.SendLoadout();
        other.SendLoadout();

        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameStart)
                                 && other.ReceivedOpcodes.Contains(PacketOpcode.GameStart));

        // Give the server a generous window to misbehave (i.e. erroneously call FinishGame).
        await Task.Delay(300);
        Assert.Empty(_lobbies.FinishCalls);
    }

    [Fact]
    public async Task Quorum_not_reached_sends_no_packets()
    {
        var token = TestJwt.Mint(userId: "u_alice", lobbyId: "lob_lonely", expectedMembers: 2, isHost: true);

        await using var client = new TestClient();
        Assert.Equal(HandshakeOutcome.Connected, await client.ConnectAndWait(_serverPort, ConnectionKey, token));

        // Pre-quorum sessions should never broadcast — give the server a generous window to misbehave.
        await Task.Delay(300);

        Assert.Empty(client.ReceivedOpcodes);
    }

    [Fact]
    public async Task Quorum_without_all_loadouts_does_not_broadcast()
    {
        const string lobby = "lob_no_loadout";
        var hostToken = TestJwt.Mint(userId: "u_alice", lobbyId: lobby, expectedMembers: 2, isHost: true);
        var clientToken = TestJwt.Mint(userId: "u_bob", lobbyId: lobby, expectedMembers: 2);

        await using var host = new TestClient();
        await using var other = new TestClient();

        Assert.Equal(HandshakeOutcome.Connected, await host.ConnectAndWait(_serverPort, ConnectionKey, hostToken));
        Assert.Equal(HandshakeOutcome.Connected, await other.ConnectAndWait(_serverPort, ConnectionKey, clientToken));

        host.SendLoadout();
        // Bob never sends a loadout — server should hold the broadcast.
        await Task.Delay(300);

        Assert.Empty(host.ReceivedOpcodes);
        Assert.Empty(other.ReceivedOpcodes);
        Assert.Empty(_lobbies.FinishCalls);

        // Bob finally sends his loadout; GameStart fires.
        other.SendLoadout();
        await WaitForAsync(() => host.ReceivedOpcodes.Count >= 1 && other.ReceivedOpcodes.Count >= 1);
        Assert.Equal(PacketOpcode.GameStart, host.ReceivedOpcodes.First());
        Assert.Equal(PacketOpcode.GameStart, other.ReceivedOpcodes.First());
    }

    [Fact]
    public async Task PeerReady_from_all_peers_broadcasts_GameStartCue()
    {
        const string lobby = "lob_ready";
        var (host, other) = await ConnectBothAndStart(lobby);

        host.SendPeerReady();
        // Cue must NOT fire after only one ready
        await Task.Delay(200);
        Assert.DoesNotContain(PacketOpcode.GameStartCue, host.ReceivedOpcodes);
        Assert.DoesNotContain(PacketOpcode.GameStartCue, other.ReceivedOpcodes);

        other.SendPeerReady();
        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue)
                                 && other.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue));

        // The cue payload should carry a future song-origin timestamp.
        var cue = host.LastReceived(PacketOpcode.GameStartCue);
        var packet = new GameStartCuePacket();
        var reader = new NetDataReader(cue);
        packet.Deserialize(reader);
        Assert.True(packet.SongOriginUtcMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "Song origin should be in the future relative to test wall-clock.");
        Assert.Equal(3000, packet.CountdownMs);
    }

    [Fact]
    public async Task SongMetadata_from_host_is_forwarded_to_Lobbies_at_cue_time()
    {
        const string lobby = "lob_song_metadata";
        var (host, other) = await ConnectBothAndStart(lobby);

        host.SendSongMetadata(durationMs: 215_000);

        host.SendPeerReady();
        other.SendPeerReady();
        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue)
                                 && other.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue));

        // Once the cue fires, the game server posts to Lobbies with the cue's origin + duration.
        await WaitForAsync(() => _lobbies.SongStartedCalls.Count == 1);
        var call = _lobbies.SongStartedCalls.Single();
        Assert.Equal(lobby, call.LobbyId);
        Assert.Equal(215_000, call.DurationMs);
        Assert.True(call.SongOriginUtcMs > 0);
    }

    [Fact]
    public async Task SongMetadata_from_non_host_is_ignored()
    {
        const string lobby = "lob_song_metadata_nonhost";
        var (host, other) = await ConnectBothAndStart(lobby);

        // Non-host tries to set duration. Server must reject; subsequent cue post falls back to 0.
        other.SendSongMetadata(durationMs: 999_999);

        host.SendPeerReady();
        other.SendPeerReady();
        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue));

        await WaitForAsync(() => _lobbies.SongStartedCalls.Count == 1);
        var call = _lobbies.SongStartedCalls.Single();
        Assert.Equal(0, call.DurationMs);
    }

    [Fact]
    public async Task SongStarted_callback_fires_with_zero_duration_when_host_never_sent_metadata()
    {
        const string lobby = "lob_no_metadata";
        var (host, other) = await ConnectBothAndStart(lobby);

        host.SendPeerReady();
        other.SendPeerReady();
        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue));

        await WaitForAsync(() => _lobbies.SongStartedCalls.Count == 1);
        Assert.Equal(0, _lobbies.SongStartedCalls.Single().DurationMs);
    }

    [Fact]
    public async Task EngineInput_before_cue_is_not_fanned_out()
    {
        const string lobby = "lob_input_pre_cue";
        var (host, other) = await ConnectBothAndStart(lobby);

        host.SendEngineInput(time: 0.5, action: 7, value: 1);
        await Task.Delay(200);
        Assert.Empty(other.PayloadsByOpcode(PacketOpcode.EngineInputBatch));
    }

    [Fact]
    public async Task EngineInput_after_cue_is_fanned_out_to_other_peers_only()
    {
        const string lobby = "lob_input_post_cue";
        var (host, other) = await ConnectBothAndStart(lobby);

        host.SendPeerReady();
        other.SendPeerReady();
        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue)
                                 && other.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue));

        host.SendEngineInput(time: 1.25, action: 42, value: -7);

        await WaitForAsync(() => other.PayloadsByOpcode(PacketOpcode.EngineInputBatch).Count >= 1);

        // Sender must not receive its own input back.
        Assert.Empty(host.PayloadsByOpcode(PacketOpcode.EngineInputBatch));

        var fanout = other.PayloadsByOpcode(PacketOpcode.EngineInputBatch).Single();
        var packet = new EngineInputBatchPacket();
        packet.Deserialize(new NetDataReader(fanout));
        Assert.Equal(host.ConnectedPeer!.RemoteId, packet.PeerId);
        var record = Assert.Single(packet.Inputs);
        Assert.Equal(1.25, record.Time);
        Assert.Equal(42, record.Action);
        Assert.Equal(-7, record.Value);
    }

    [Fact]
    public async Task GameComplete_from_all_peers_broadcasts_GameEnd()
    {
        const string lobby = "lob_complete";
        var (host, other) = await ConnectBothAndStart(lobby);

        host.SendPeerReady();
        other.SendPeerReady();
        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue)
                                 && other.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue));

        host.SendGameComplete();
        await Task.Delay(200);
        Assert.DoesNotContain(PacketOpcode.GameEnd, host.ReceivedOpcodes);
        Assert.DoesNotContain(PacketOpcode.GameEnd, other.ReceivedOpcodes);
        Assert.Empty(_lobbies.FinishCalls);

        other.SendGameComplete();
        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameEnd)
                                 && other.ReceivedOpcodes.Contains(PacketOpcode.GameEnd));

        // Once the game has ended, the network service must tell the lobby hub so the lobby
        // status returns to SongSelect. Fire-and-forget so wait briefly for it to land.
        await WaitForAsync(() => _lobbies.FinishCalls.Count == 1);
        Assert.Equal(lobby, _lobbies.FinishCalls.Single());
    }

    [Fact]
    public async Task Lobbies_callback_failure_does_not_prevent_GameEnd_broadcast()
    {
        // Even if the lobby hub HTTP call fails (network blip, lobbies service flapping),
        // peers must still receive GameEnd so they can return to their lobby UI.
        const string lobby = "lob_finish_fail_on_end";
        _lobbies.OnFinishGame = (_, _) => Task.FromException(new HttpRequestException("boom"));

        var (host, other) = await ConnectBothAndStart(lobby);

        host.SendPeerReady();
        other.SendPeerReady();
        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue)
                                 && other.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue));

        host.SendGameComplete();
        other.SendGameComplete();

        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameEnd)
                                 && other.ReceivedOpcodes.Contains(PacketOpcode.GameEnd));
    }

    [Fact]
    public async Task Disconnect_broadcasts_RemotePeerLeft_to_remaining_peers()
    {
        const string lobby = "lob_disconnect";
        var (host, other) = await ConnectBothAndStart(lobby);

        host.SendPeerReady();
        other.SendPeerReady();
        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue)
                                 && other.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue));

        int leavingPeerId = other.ConnectedPeer!.RemoteId;
        await other.DisposeAsync();

        await WaitForAsync(() => host.PayloadsByOpcode(PacketOpcode.RemotePeerLeft).Count >= 1);
        var payload = host.PayloadsByOpcode(PacketOpcode.RemotePeerLeft).Single();
        var left = new RemotePeerLeftPacket();
        left.Deserialize(new NetDataReader(payload));
        Assert.Equal(leavingPeerId, left.PeerId);
    }

    [Fact]
    public async Task Disconnect_after_partial_complete_unblocks_GameEnd()
    {
        // host completes, then other disconnects without completing — GameEnd must still fire for host.
        const string lobby = "lob_disconnect_unblocks_end";
        var (host, other) = await ConnectBothAndStart(lobby);

        host.SendPeerReady();
        other.SendPeerReady();
        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue)
                                 && other.ReceivedOpcodes.Contains(PacketOpcode.GameStartCue));

        host.SendGameComplete();
        await Task.Delay(150);
        Assert.DoesNotContain(PacketOpcode.GameEnd, host.ReceivedOpcodes);

        await other.DisposeAsync();
        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameEnd));
    }

    private async Task<(TestClient host, TestClient other)> ConnectBothAndStart(string lobbyId)
    {
        var hostToken = TestJwt.Mint(userId: "u_alice", lobbyId: lobbyId, expectedMembers: 2, isHost: true);
        var clientToken = TestJwt.Mint(userId: "u_bob", lobbyId: lobbyId, expectedMembers: 2);

        var host = new TestClient();
        var other = new TestClient();

        Assert.Equal(HandshakeOutcome.Connected, await host.ConnectAndWait(_serverPort, ConnectionKey, hostToken));
        Assert.Equal(HandshakeOutcome.Connected, await other.ConnectAndWait(_serverPort, ConnectionKey, clientToken));

        host.SendLoadout();
        other.SendLoadout();

        await WaitForAsync(() => host.ReceivedOpcodes.Contains(PacketOpcode.GameStart)
                                 && other.ReceivedOpcodes.Contains(PacketOpcode.GameStart));
        return (host, other);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"Condition not met within {WaitTimeout}.");
    }

    private static int GetFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private enum HandshakeOutcome { Connected, Rejected, TimedOut }

    private sealed class TestClient : IAsyncDisposable
    {
        private readonly EventBasedNetListener _listener = new();
        private readonly NetManager _manager;
        private readonly CancellationTokenSource _cts = new();
        private Task? _pollLoop;
        private readonly TaskCompletionSource<HandshakeOutcome> _outcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NetPeer? ConnectedPeer { get; private set; }
        public ConcurrentQueue<PacketOpcode> ReceivedOpcodes { get; } = new();

        // For tests that need to inspect packet bodies, not just opcodes.
        private readonly object _payloadGate = new();
        private readonly Dictionary<PacketOpcode, List<byte[]>> _payloads = new();

        public IReadOnlyList<byte[]> PayloadsByOpcode(PacketOpcode op)
        {
            lock (_payloadGate)
            {
                return _payloads.TryGetValue(op, out var list)
                    ? list.ToArray()
                    : Array.Empty<byte[]>();
            }
        }

        public byte[] LastReceived(PacketOpcode op)
        {
            lock (_payloadGate)
            {
                return _payloads[op][^1];
            }
        }

        public TestClient()
        {
            _manager = new NetManager(_listener) { UnconnectedMessagesEnabled = false };
            _listener.PeerConnectedEvent += peer =>
            {
                ConnectedPeer = peer;
                _outcome.TrySetResult(HandshakeOutcome.Connected);
            };
            _listener.PeerDisconnectedEvent += (_, _) => _outcome.TrySetResult(HandshakeOutcome.Rejected);
            _listener.NetworkReceiveEvent += (_, reader, _, _) =>
            {
                if (reader.AvailableBytes >= 1)
                {
                    var op = (PacketOpcode)reader.GetByte();
                    ReceivedOpcodes.Enqueue(op);
                    var body = new byte[reader.AvailableBytes];
                    reader.GetBytes(body, body.Length);
                    lock (_payloadGate)
                    {
                        if (!_payloads.TryGetValue(op, out var list))
                        {
                            list = new List<byte[]>();
                            _payloads[op] = list;
                        }
                        list.Add(body);
                    }
                }
                reader.Recycle();
            };
        }

        public void SendLoadout(InstrumentId instrument = InstrumentId.FiveFretGuitar, DifficultyId difficulty = DifficultyId.Expert)
        {
            if (ConnectedPeer is null)
            {
                throw new InvalidOperationException("TestClient not connected; cannot send loadout.");
            }

            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.SetLoadout, new SetLoadoutPacket
            {
                Instrument = instrument,
                Difficulty = difficulty,
            });
            ConnectedPeer.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendPeerReady()
        {
            EnsureConnected();
            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.PeerReady, new PeerReadyPacket());
            ConnectedPeer!.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendGameComplete()
        {
            EnsureConnected();
            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.GameComplete, new GameCompletePacket());
            ConnectedPeer!.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendSongMetadata(int durationMs)
        {
            EnsureConnected();
            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.SongMetadata, new SongMetadataPacket
            {
                DurationMs = durationMs,
            });
            ConnectedPeer!.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        public void SendEngineInput(double time, int action, int value)
        {
            EnsureConnected();
            var writer = new NetDataWriter();
            GamePacketWriter.Write(writer, PacketOpcode.EngineInputBatch, new EngineInputBatchPacket
            {
                PeerId = 0, // server overwrites
                Inputs = new[] { new EngineInputRecord(time, action, value) },
            });
            ConnectedPeer!.Send(writer, DeliveryMethod.ReliableOrdered);
        }

        private void EnsureConnected()
        {
            if (ConnectedPeer is null)
            {
                throw new InvalidOperationException("TestClient not connected.");
            }
        }

        public async Task<HandshakeOutcome> ConnectAndWait(int port, string connectionKey, string jwt)
        {
            if (!_manager.Start())
            {
                throw new InvalidOperationException("Test client NetManager failed to start.");
            }

            _pollLoop = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
                try
                {
                    while (await timer.WaitForNextTickAsync(_cts.Token))
                    {
                        _manager.PollEvents();
                    }
                }
                catch (OperationCanceledException) { }
            });

            var writer = new NetDataWriter();
            writer.Put(connectionKey);
            writer.Put(jwt);
            _manager.Connect(new IPEndPoint(IPAddress.Loopback, port), writer);

            var completed = await Task.WhenAny(_outcome.Task, Task.Delay(WaitTimeout));
            return completed == _outcome.Task ? _outcome.Task.Result : HandshakeOutcome.TimedOut;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            if (_pollLoop is not null)
            {
                try { await _pollLoop; } catch { /* swallow */ }
            }
            _manager.Stop();
            _cts.Dispose();
        }
    }
}
