using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Contracts.Rest;

namespace YARG.Online.Lobbies.Tests;

public class StartGameIntegrationTests : IClassFixture<StartGameIntegrationTests.GameAuthFactory>
{
    private const string GameSecret = "test-secret-must-be-32-bytes-or-more-please";
    private const string GameIssuer = "yarg-server-browser";
    private const string GameAudience = "yarg-game";
    private const string GameServerEndpoint = "127.0.0.1:9050";
    private const string GameServerConnectionKey = "yarg-online-game-test-key";
    private const string Hash1 = "0000000000000000000000000000000000000001";

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;

    public StartGameIntegrationTests(GameAuthFactory factory)
    {
        _factory = factory;
    }

    public sealed class GameAuthFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GameAuth:Issuer"] = GameIssuer,
                    ["GameAuth:Audience"] = GameAudience,
                    ["GameAuth:SigningSecret"] = GameSecret,
                    ["GameServer:Endpoint"] = GameServerEndpoint,
                    ["GameServer:ConnectionKey"] = GameServerConnectionKey,
                });
            });
        }
    }

    [Fact]
    public async Task StartGame_issues_per_player_tokens_and_FinishGame_returns_to_song_select()
    {
        var (aliceToken, aliceId) = await IssueIdentityAsync("alice");
        var (bobToken, bobId) = await IssueIdentityAsync("bob");

        var alice = new ClientHarness();
        var bob = new ClientHarness();

        await using var aliceConn = BuildConnection(aliceToken, alice);
        await using var bobConn = BuildConnection(bobToken, bob);

        await aliceConn.StartAsync();
        await bobConn.StartAsync();
        await alice.NextSnapshot();
        await bob.NextSnapshot();

        // Host (Alice) creates, Bob joins, Alice queues a song to satisfy the StartGame preconditions.
        var created = await aliceConn.InvokeAsync<CreateLobbyResult>(
            nameof(ILobbyHub.CreateLobby),
            new CreateLobbyArgs("Start Lobby", GameMode.Band, Region.UsEast, "Track", 4, new SongLibraryDto(new[] { Hash1 })));
        Assert.Equal(LobbyStatus.SongSelect, created.Lobby.Status);

        await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, new SongLibraryDto(new[] { Hash1 })));
        await alice.NextPlayerJoined();

        await aliceConn.InvokeAsync<QueuedSongDto>(nameof(ILobbyHub.QueueSong), new QueueSongArgs(Hash1));
        await alice.NextSongQueued();
        await bob.NextSongQueued();

        // Alice starts the game.
        await aliceConn.InvokeAsync(nameof(ILobbyHub.StartGame));

        var aliceStart = await alice.NextGameStarted();
        var bobStart = await bob.NextGameStarted();

        Assert.Equal(created.Lobby.Id, aliceStart.LobbyId);
        Assert.Equal(created.Lobby.Id, bobStart.LobbyId);
        Assert.Equal(GameServerEndpoint, aliceStart.Endpoint);
        Assert.Equal(GameServerEndpoint, bobStart.Endpoint);
        Assert.Equal(GameServerConnectionKey, aliceStart.ConnectionKey);
        Assert.Equal(GameServerConnectionKey, bobStart.ConnectionKey);
        Assert.NotEqual(aliceStart.GameToken, bobStart.GameToken);

        AssertGameToken(aliceStart.GameToken, expectedSub: aliceId, expectedName: "alice", expectedLobbyId: created.Lobby.Id, expectedMembers: 2);
        AssertGameToken(bobStart.GameToken, expectedSub: bobId, expectedName: "bob", expectedLobbyId: created.Lobby.Id, expectedMembers: 2);

        var aliceStatus = await alice.NextStatusChanged();
        var bobStatus = await bob.NextStatusChanged();
        Assert.Equal(LobbyStatus.GameStarted, aliceStatus.Status);
        Assert.Equal(LobbyStatus.GameStarted, bobStatus.Status);

        // Game server callback: song actually starts. End-to-end shape of the cue post.
        using var http = _factory.CreateClient();
        var originUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var startedResp = await http.PostAsJsonAsync(
            $"/api/v1/lobbies/{created.Lobby.Id}/song-started",
            new { SongOriginUtcMs = originUtcMs, DurationMs = 215_000 },
            JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, startedResp.StatusCode);

        // Game server callback: finish the game.
        var finishResp = await http.PostAsync($"/api/v1/lobbies/{created.Lobby.Id}/game-finished", content: null);
        Assert.Equal(HttpStatusCode.NoContent, finishResp.StatusCode);

        var aliceBack = await alice.NextStatusChanged();
        var bobBack = await bob.NextStatusChanged();
        Assert.Equal(LobbyStatus.SongSelect, aliceBack.Status);
        Assert.Equal(LobbyStatus.SongSelect, bobBack.Status);

        // Auto-removal: both clients are notified that the played song was popped, with
        // reason=Played so clients can suppress the "song removed" toast.
        var aliceRemoved = await alice.NextSongRemoved();
        var bobRemoved = await bob.NextSongRemoved();
        Assert.Equal(SongRemovalReason.Played, aliceRemoved.Reason);
        Assert.Equal(SongRemovalReason.Played, bobRemoved.Reason);
        Assert.Equal(created.Lobby.Id, aliceRemoved.LobbyId);
    }

    [Fact]
    public async Task SongStarted_endpoint_returns_404_for_unknown_lobby()
    {
        using var http = _factory.CreateClient();
        var resp = await http.PostAsJsonAsync(
            "/api/v1/lobbies/DOESNOTEXIST/song-started",
            new { SongOriginUtcMs = 1L, DurationMs = 0 },
            JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task StartGame_rejects_when_caller_is_not_host()
    {
        var aliceToken = (await IssueIdentityAsync("alice")).Token;
        var bobToken = (await IssueIdentityAsync("bob")).Token;

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
            new CreateLobbyArgs("Not Host Lobby", GameMode.Band, Region.UsEast, "Track", 4, new SongLibraryDto(new[] { Hash1 })));
        await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, new SongLibraryDto(new[] { Hash1 })));
        await alice.NextPlayerJoined();
        await aliceConn.InvokeAsync<QueuedSongDto>(nameof(ILobbyHub.QueueSong), new QueueSongArgs(Hash1));
        await alice.NextSongQueued();
        await bob.NextSongQueued();

        var ex = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => bobConn.InvokeAsync(nameof(ILobbyHub.StartGame)));
        Assert.Contains("not_host", ex.Message);
    }

    [Fact]
    public async Task StartGame_rejects_with_one_player()
    {
        var aliceToken = (await IssueIdentityAsync("alice")).Token;
        var alice = new ClientHarness();
        await using var aliceConn = BuildConnection(aliceToken, alice);
        await aliceConn.StartAsync();
        await alice.NextSnapshot();

        await aliceConn.InvokeAsync<CreateLobbyResult>(
            nameof(ILobbyHub.CreateLobby),
            new CreateLobbyArgs("Solo Lobby", GameMode.Band, Region.UsEast, "Track", 4, new SongLibraryDto(new[] { Hash1 })));
        await aliceConn.InvokeAsync<QueuedSongDto>(nameof(ILobbyHub.QueueSong), new QueueSongArgs(Hash1));
        await alice.NextSongQueued();

        var ex = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => aliceConn.InvokeAsync(nameof(ILobbyHub.StartGame)));
        Assert.Contains("not_enough_players", ex.Message);
    }

    [Fact]
    public async Task StartGame_rejects_with_empty_queue()
    {
        var aliceToken = (await IssueIdentityAsync("alice")).Token;
        var bobToken = (await IssueIdentityAsync("bob")).Token;

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
            new CreateLobbyArgs("Empty Queue Lobby", GameMode.Band, Region.UsEast, "Track", 4, new SongLibraryDto(new[] { Hash1 })));
        await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, new SongLibraryDto(new[] { Hash1 })));
        await alice.NextPlayerJoined();

        var ex = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => aliceConn.InvokeAsync(nameof(ILobbyHub.StartGame)));
        Assert.Contains("queue_empty", ex.Message);
    }

    [Fact]
    public async Task StartGame_rejects_when_already_started()
    {
        var aliceToken = (await IssueIdentityAsync("alice")).Token;
        var bobToken = (await IssueIdentityAsync("bob")).Token;

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
            new CreateLobbyArgs("Double Start", GameMode.Band, Region.UsEast, "Track", 4, new SongLibraryDto(new[] { Hash1 })));
        await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, new SongLibraryDto(new[] { Hash1 })));
        await alice.NextPlayerJoined();
        await aliceConn.InvokeAsync<QueuedSongDto>(nameof(ILobbyHub.QueueSong), new QueueSongArgs(Hash1));
        await alice.NextSongQueued();
        await bob.NextSongQueued();

        await aliceConn.InvokeAsync(nameof(ILobbyHub.StartGame));
        await alice.NextGameStarted();
        await bob.NextGameStarted();
        await alice.NextStatusChanged();
        await bob.NextStatusChanged();

        var ex = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => aliceConn.InvokeAsync(nameof(ILobbyHub.StartGame)));
        Assert.Contains("already_started", ex.Message);
    }

    [Fact]
    public async Task FinishGame_returns_404_for_unknown_lobby()
    {
        using var http = _factory.CreateClient();
        var resp = await http.PostAsync("/api/v1/lobbies/DOESNOTEXIST/game-finished", content: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task FinishGame_returns_409_when_lobby_is_in_song_select()
    {
        var aliceToken = (await IssueIdentityAsync("alice")).Token;
        var alice = new ClientHarness();
        await using var aliceConn = BuildConnection(aliceToken, alice);
        await aliceConn.StartAsync();
        await alice.NextSnapshot();

        var created = await aliceConn.InvokeAsync<CreateLobbyResult>(
            nameof(ILobbyHub.CreateLobby),
            new CreateLobbyArgs("Not Started", GameMode.Band, Region.UsEast, "Track", 4, new SongLibraryDto(new[] { Hash1 })));

        using var http = _factory.CreateClient();
        var resp = await http.PostAsync($"/api/v1/lobbies/{created.Lobby.Id}/game-finished", content: null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    private static void AssertGameToken(string token, string expectedSub, string expectedName, string expectedLobbyId, int expectedMembers)
    {
        var handler = new JsonWebTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(GameSecret));
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = GameIssuer,
            ValidAudience = GameAudience,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            IssuerSigningKey = key,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
        var result = handler.ValidateTokenAsync(token, parameters).GetAwaiter().GetResult();
        Assert.True(result.IsValid, result.Exception?.ToString());

        Assert.Equal(expectedSub, result.Claims["sub"]?.ToString());
        Assert.Equal(expectedName, result.Claims["name"]?.ToString());
        Assert.Equal(expectedLobbyId, result.Claims["lobby_id"]?.ToString());
        Assert.Equal(expectedMembers, Convert.ToInt32(result.Claims["expected_members"]));
    }

    private async Task<(string Token, string UserId)> IssueIdentityAsync(string displayName)
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/dev", new DevAuthRequest(displayName), JsonOptions);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<DevAuthResponse>(JsonOptions);
        return (payload!.Token, payload.UserId);
    }

    private HubConnection BuildConnection(string token, ClientHarness harness)
    {
        var conn = new HubConnectionBuilder()
            .WithUrl(_factory.Server.BaseAddress + "hubs/lobby", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
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

    private sealed class ClientHarness
    {
        private readonly Queue<LobbyDto[]> _snapshots = new();
        private readonly Queue<(string LobbyId, string UserId, string DisplayName)> _joins = new();
        private readonly Queue<(string LobbyId, QueuedSongDto Song)> _songsQueued = new();
        private readonly Queue<(string LobbyId, long Sequence, SongRemovalReason Reason)> _songsRemoved = new();
        private readonly Queue<(string LobbyId, string Endpoint, string ConnectionKey, string GameToken, DateTimeOffset ExpiresAt)> _gameStarteds = new();
        private readonly Queue<(string LobbyId, LobbyStatus Status)> _statusChanges = new();

        private TaskCompletionSource<LobbyDto[]>? _snapshotWaiter;
        private TaskCompletionSource<(string, string, string)>? _joinWaiter;
        private TaskCompletionSource<(string LobbyId, QueuedSongDto Song)>? _songQueuedWaiter;
        private TaskCompletionSource<(string LobbyId, long Sequence, SongRemovalReason Reason)>? _songRemovedWaiter;
        private TaskCompletionSource<(string LobbyId, string Endpoint, string ConnectionKey, string GameToken, DateTimeOffset ExpiresAt)>? _gameStartedWaiter;
        private TaskCompletionSource<(string LobbyId, LobbyStatus Status)>? _statusChangedWaiter;

        private readonly object _lock = new();

        public void Bind(HubConnection conn)
        {
            conn.On<LobbyDto[]>("OnLobbySnapshot", lobbies => Push(lobbies, _snapshots, ref _snapshotWaiter));
            conn.On<PlayerJoinedEvent>("OnPlayerJoined", e =>
                Push((e.LobbyId, e.UserId, e.DisplayName), _joins, ref _joinWaiter));
            conn.On<SongQueuedEvent>("OnSongQueued", e =>
                Push((e.LobbyId, e.Song), _songsQueued, ref _songQueuedWaiter));
            conn.On<SongRemovedFromQueueEvent>("OnSongRemovedFromQueue", e =>
                Push((e.LobbyId, e.Sequence, e.Reason), _songsRemoved, ref _songRemovedWaiter));
            conn.On<GameStartedEvent>("OnGameStarted", e =>
                Push((e.LobbyId, e.GameServerEndpoint, e.ConnectionKey, e.GameToken, e.ExpiresAt), _gameStarteds, ref _gameStartedWaiter));
            conn.On<LobbyStatusChangedEvent>("OnLobbyStatusChanged", e =>
                Push((e.LobbyId, e.Status), _statusChanges, ref _statusChangedWaiter));
        }

        public Task<LobbyDto[]> NextSnapshot() => Pull(_snapshots, ref _snapshotWaiter);
        public Task<(string LobbyId, string UserId, string DisplayName)> NextPlayerJoined() => Pull(_joins, ref _joinWaiter);
        public Task<(string LobbyId, QueuedSongDto Song)> NextSongQueued() => Pull(_songsQueued, ref _songQueuedWaiter);
        public Task<(string LobbyId, long Sequence, SongRemovalReason Reason)> NextSongRemoved() => Pull(_songsRemoved, ref _songRemovedWaiter);
        public Task<(string LobbyId, string Endpoint, string ConnectionKey, string GameToken, DateTimeOffset ExpiresAt)> NextGameStarted() => Pull(_gameStarteds, ref _gameStartedWaiter);
        public Task<(string LobbyId, LobbyStatus Status)> NextStatusChanged() => Pull(_statusChanges, ref _statusChangedWaiter);

        private void Push<T>(T value, Queue<T> queue, ref TaskCompletionSource<T>? slot)
        {
            lock (_lock)
            {
                if (slot is { } w)
                {
                    slot = null;
                    w.TrySetResult(value);
                }
                else
                {
                    queue.Enqueue(value);
                }
            }
        }

        private Task<T> Pull<T>(Queue<T> queue, ref TaskCompletionSource<T>? slot)
        {
            lock (_lock)
            {
                if (queue.TryDequeue(out var existing))
                {
                    return Task.FromResult(existing);
                }
                var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                slot = tcs;
                return tcs.Task.WaitAsync(WaitTimeout);
            }
        }
    }
}
