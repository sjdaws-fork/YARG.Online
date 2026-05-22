using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using YARG.Online.Game.Contracts.Enums;
using YARG.Online.Game.Contracts.Packets;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Contracts.Rest;

namespace YARG.Online.E2E.Tests;

public class FullFlowE2ETests : IClassFixture<E2EFixture>
{
    private const string Hash1 = "0000000000000000000000000000000000000001";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly E2EFixture _fixture;

    public FullFlowE2ETests(E2EFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Full_flow_runs_lobby_through_GameStart_cue_complete_and_back_to_SongSelect()
    {
        var aliceToken = await IssueTokenAsync("alice");
        var bobToken = await IssueTokenAsync("bob");

        var alice = new ClientHarness();
        var bob = new ClientHarness();

        await using var aliceConn = BuildConnection(aliceToken, alice);
        await using var bobConn = BuildConnection(bobToken, bob);

        await aliceConn.StartAsync();
        await bobConn.StartAsync();
        await alice.NextSnapshot();
        await bob.NextSnapshot();

        var created = await aliceConn.InvokeAsync<CreateLobbyResult>(
            nameof(ILobbyHub.CreateLobby),
            new CreateLobbyArgs("E2E Lobby", GameMode.Band, Region.UsEast, "Track", 4, new SongLibraryDto(new[] { Hash1 })));
        Assert.Equal(LobbyStatus.SongSelect, created.Lobby.Status);

        await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby),
            new EnterLobbyArgs(created.Lobby.Id, new SongLibraryDto(new[] { Hash1 })));
        await alice.NextPlayerJoined();

        await aliceConn.InvokeAsync<QueuedSongDto>(nameof(ILobbyHub.QueueSong), new QueueSongArgs(Hash1));
        await alice.NextSongQueued();
        await bob.NextSongQueued();

        await aliceConn.InvokeAsync(nameof(ILobbyHub.StartGame));

        // Starting is broadcast before allocation; GameStarted after.
        var aliceStarting = await alice.NextStatusChanged();
        var bobStarting = await bob.NextStatusChanged();
        Assert.Equal(LobbyStatus.Starting, aliceStarting.Status);
        Assert.Equal(LobbyStatus.Starting, bobStarting.Status);

        var aliceStart = await alice.NextGameStarted();
        var bobStart = await bob.NextGameStarted();
        Assert.Equal(created.Lobby.Id, aliceStart.LobbyId);
        Assert.Equal(created.Lobby.Id, bobStart.LobbyId);
        Assert.Equal($"127.0.0.1:{_fixture.GameEndpoint.Port}", aliceStart.Endpoint);
        Assert.Equal(E2EFixture.ConnectionKey, aliceStart.ConnectionKey);
        Assert.Equal(E2EFixture.ConnectionKey, bobStart.ConnectionKey);

        var aliceStatus = await alice.NextStatusChanged();
        var bobStatus = await bob.NextStatusChanged();
        Assert.Equal(LobbyStatus.GameStarted, aliceStatus.Status);
        Assert.Equal(LobbyStatus.GameStarted, bobStatus.Status);

        // Both clients hand off to the UDP game server. Charts are no longer transported —
        // ownership is gated by the lobby's library-intersection check, so the game server
        // only ever sees connect, loadouts, ready, inputs (omitted in this test), and
        // completion signals.
        await using var aliceUdp = new UdpGameClient();
        await using var bobUdp = new UdpGameClient();

        Assert.Equal(HandshakeOutcome.Connected,
            await aliceUdp.ConnectAsync(_fixture.GameEndpoint, aliceStart.ConnectionKey, aliceStart.GameToken));
        Assert.Equal(HandshakeOutcome.Connected,
            await bobUdp.ConnectAsync(_fixture.GameEndpoint, bobStart.ConnectionKey, bobStart.GameToken));

        // GameStart now fires on quorum + per-peer loadouts. No chart upload step.
        aliceUdp.SendLoadout(InstrumentId.FiveFretGuitar, DifficultyId.Expert);
        await Task.Delay(150);
        Assert.Empty(aliceUdp.ReceivedOpcodes);
        Assert.Empty(bobUdp.ReceivedOpcodes);

        bobUdp.SendLoadout(InstrumentId.FourLaneDrums, DifficultyId.Hard);

        await aliceUdp.WaitForOpcodeCountAsync(1, TimeSpan.FromSeconds(10));
        await bobUdp.WaitForOpcodeCountAsync(1, TimeSpan.FromSeconds(10));
        Assert.Equal(PacketOpcode.GameStart, aliceUdp.ReceivedOpcodes.First());
        Assert.Equal(PacketOpcode.GameStart, bobUdp.ReceivedOpcodes.First());

        // GameStart payload should carry every peer's selection (with peer ids stamped).
        Assert.NotNull(aliceUdp.ReceivedGameStart);
        Assert.NotNull(bobUdp.ReceivedGameStart);
        var loadouts = aliceUdp.ReceivedGameStart!.Loadouts;
        Assert.Equal(2, loadouts.Length);
        Assert.Contains(loadouts, l =>
            l.DisplayName == "alice" && l.Instrument == InstrumentId.FiveFretGuitar && l.Difficulty == DifficultyId.Expert);
        Assert.Contains(loadouts, l =>
            l.DisplayName == "bob" && l.Instrument == InstrumentId.FourLaneDrums && l.Difficulty == DifficultyId.Hard);
        // Each peer must get a distinct id (LiteNetLib hands out 0, 1, ... so we can't just
        // check non-zero — instead, verify uniqueness which is what the client actually relies on).
        Assert.Equal(loadouts.Length, loadouts.Select(l => l.PeerId).Distinct().Count());

        // Both clients report ready -> server broadcasts GameStartCue.
        aliceUdp.SendPeerReady();
        bobUdp.SendPeerReady();
        await aliceUdp.WaitForOpcodeCountAsync(2, TimeSpan.FromSeconds(10));
        await bobUdp.WaitForOpcodeCountAsync(2, TimeSpan.FromSeconds(10));
        Assert.Equal(PacketOpcode.GameStartCue, aliceUdp.ReceivedOpcodes.ToArray()[1]);
        Assert.Equal(PacketOpcode.GameStartCue, bobUdp.ReceivedOpcodes.ToArray()[1]);

        // Both clients report complete -> server broadcasts GameEnd.
        aliceUdp.SendGameComplete();
        bobUdp.SendGameComplete();
        await aliceUdp.WaitForOpcodeCountAsync(3, TimeSpan.FromSeconds(10));
        await bobUdp.WaitForOpcodeCountAsync(3, TimeSpan.FromSeconds(10));
        Assert.Equal(PacketOpcode.GameEnd, aliceUdp.ReceivedOpcodes.ToArray()[2]);
        Assert.Equal(PacketOpcode.GameEnd, bobUdp.ReceivedOpcodes.ToArray()[2]);

        // After GameEnd the game server posts /game-finished, which (a) flips the lobby back to
        // SongSelect and (b) broadcasts the auto-removal of the played song with reason=Played.
        var aliceBack = await alice.NextStatusChanged();
        var bobBack = await bob.NextStatusChanged();
        Assert.Equal(LobbyStatus.SongSelect, aliceBack.Status);
        Assert.Equal(LobbyStatus.SongSelect, bobBack.Status);

        var aliceRemoved = await alice.NextSongRemoved();
        var bobRemoved = await bob.NextSongRemoved();
        Assert.Equal(SongRemovalReason.Played, aliceRemoved.Reason);
        Assert.Equal(SongRemovalReason.Played, bobRemoved.Reason);
    }

    private async Task<string> IssueTokenAsync(string displayName)
    {
        using var client = _fixture.Lobbies.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/dev", new DevAuthRequest(displayName), JsonOptions);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<DevAuthResponse>(JsonOptions);
        return payload!.Token;
    }

    private HubConnection BuildConnection(string token, ClientHarness harness)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl(_fixture.Lobbies.Server.BaseAddress + "hubs/lobby", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _fixture.Lobbies.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                o.PayloadSerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
            })
            .Build();

        harness.Bind(conn);
        return conn;
    }
}
