using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Contracts.Rest;

namespace YARG.Online.Lobbies.Tests;

public class LobbyHubIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Valid 40-char hex SHA-1-shaped strings for tests. Validator requires this format.
    private const string Hash1 = "0000000000000000000000000000000000000001";
    private const string Hash2 = "0000000000000000000000000000000000000002";
    private const string Hash3 = "0000000000000000000000000000000000000003";
    private const string Hash4 = "0000000000000000000000000000000000000004";

    private static SongLibraryDto Lib(params string[] hashes) => new(hashes);

    private readonly WebApplicationFactory<Program> _factory;

    public LobbyHubIntegrationTests(WebApplicationFactory<Program> factory)
    {
        // WebApplicationFactory defaults to the "Development" environment, which is required
        // for /api/v1/auth/dev to be registered.
        _factory = factory.WithWebHostBuilder(_ => { });
    }

    [Fact]
    public async Task Snapshot_then_batch_then_join_round_trip()
    {
        var aliceToken = await IssueTokenAsync("alice");
        var bobToken = await IssueTokenAsync("bob");

        var alice = new ClientHarness();
        var bob = new ClientHarness();

        await using var aliceConn = BuildConnection(aliceToken, alice);
        await using var bobConn = BuildConnection(bobToken, bob);

        await aliceConn.StartAsync();
        await bobConn.StartAsync();

        // Both clients land in "browse" and receive the empty snapshot.
        var aliceSnap = await alice.NextSnapshot();
        var bobSnap = await bob.NextSnapshot();
        Assert.Empty(aliceSnap);
        Assert.Empty(bobSnap);

        // Alice creates a lobby. She leaves the browse group; Bob (still browsing) sees the Added batch.
        var created = await aliceConn.InvokeAsync<CreateLobbyResult>(
            nameof(ILobbyHub.CreateLobby),
            new CreateLobbyArgs("Test Lobby", GameMode.Band, Region.UsEast, "Track", 4, Lib(Hash1, Hash2)));

        var batch = await bob.NextBatch(b => b.Added.Length > 0);
        var addedLobby = Assert.Single(batch.Added);
        Assert.Equal(created.Lobby.Id, addedLobby.Id);
        Assert.Equal(1, addedLobby.PlayerCount);

        // Bob enters the lobby. Alice (now host of "lobby:{id}") receives OnPlayerJoined.
        var entered = await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby),
            new EnterLobbyArgs(created.Lobby.Id, Lib(Hash1, Hash2)));
        Assert.Equal(created.Lobby.Id, entered.Lobby.Id);
        Assert.Equal(2, entered.CurrentMembers.Length);
        Assert.Contains(entered.CurrentMembers, m => m.DisplayName == "alice");
        Assert.Contains(entered.CurrentMembers, m => m.DisplayName == "bob");

        var joined = await alice.NextPlayerJoined();
        Assert.Equal(created.Lobby.Id, joined.LobbyId);

        // Bob leaves. Alice (still in the lobby) receives OnPlayerLeft.
        await bobConn.InvokeAsync(nameof(ILobbyHub.LeaveLobby));

        var left = await alice.NextPlayerLeft();
        Assert.Equal(created.Lobby.Id, left.LobbyId);

        // Bob got a fresh snapshot on his way back to browse — the lobby is still there with count 1.
        var bobReturnSnap = await bob.NextSnapshot();
        var visible = Assert.Single(bobReturnSnap);
        Assert.Equal(created.Lobby.Id, visible.Id);
        Assert.Equal(1, visible.PlayerCount);
    }

    [Fact]
    public async Task Chat_messages_broadcast_to_lobby_and_history_replays_on_join()
    {
        var aliceToken = await IssueTokenAsync("alice");
        var bobToken = await IssueTokenAsync("bob");
        var carolToken = await IssueTokenAsync("carol");

        var alice = new ClientHarness();
        var bob = new ClientHarness();
        var carol = new ClientHarness();

        await using var aliceConn = BuildConnection(aliceToken, alice);
        await using var bobConn = BuildConnection(bobToken, bob);
        await using var carolConn = BuildConnection(carolToken, carol);

        await aliceConn.StartAsync();
        await bobConn.StartAsync();
        await carolConn.StartAsync();

        // Drain initial browse snapshots.
        await alice.NextSnapshot();
        await bob.NextSnapshot();
        await carol.NextSnapshot();

        var created = await aliceConn.InvokeAsync<CreateLobbyResult>(
            nameof(ILobbyHub.CreateLobby),
            new CreateLobbyArgs("Chat Lobby", GameMode.Band, Region.UsEast, "Track", 4, Lib(Hash1)));

        await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, Lib(Hash1)));
        await alice.NextPlayerJoined();

        // Alice and Bob each send one message. Both should receive both, including their own echo.
        await aliceConn.InvokeAsync(nameof(ILobbyHub.SendChatMessage), new SendChatMessageArgs("hello from alice"));
        await bobConn.InvokeAsync(nameof(ILobbyHub.SendChatMessage), new SendChatMessageArgs("hi alice, bob here"));

        var aliceFirst = await alice.NextChatMessage();
        var aliceSecond = await alice.NextChatMessage();
        var bobFirst = await bob.NextChatMessage();
        var bobSecond = await bob.NextChatMessage();

        Assert.Equal(1, aliceFirst.Message.Sequence);
        Assert.Equal("hello from alice", aliceFirst.Message.Text);
        Assert.Equal("alice", aliceFirst.Message.DisplayName);
        Assert.Equal(2, aliceSecond.Message.Sequence);
        Assert.Equal("hi alice, bob here", aliceSecond.Message.Text);
        Assert.Equal("bob", aliceSecond.Message.DisplayName);
        Assert.Equal(aliceFirst.Message.Sequence, bobFirst.Message.Sequence);
        Assert.Equal(aliceSecond.Message.Sequence, bobSecond.Message.Sequence);

        // Carol joins later — she gets the full history in the EnterLobby snapshot.
        var entered = await carolConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, Lib(Hash1)));
        Assert.Equal(2, entered.ChatHistory.Length);
        Assert.Equal(new long[] { 1, 2 }, entered.ChatHistory.Select(m => m.Sequence));
        Assert.Equal(new[] { "hello from alice", "hi alice, bob here" }, entered.ChatHistory.Select(m => m.Text));

        // Drain Alice/Bob's "carol joined" notification so the next chat assertion isn't racy.
        await alice.NextPlayerJoined();
        await bob.NextPlayerJoined();

        // New message after Carol joins is delivered to all three.
        await carolConn.InvokeAsync(nameof(ILobbyHub.SendChatMessage), new SendChatMessageArgs("carol arrives"));

        var aliceThird = await alice.NextChatMessage();
        var bobThird = await bob.NextChatMessage();
        var carolFirst = await carol.NextChatMessage();

        Assert.Equal(3, aliceThird.Message.Sequence);
        Assert.Equal("carol arrives", aliceThird.Message.Text);
        Assert.Equal("carol", aliceThird.Message.DisplayName);
        Assert.Equal(3, bobThird.Message.Sequence);
        Assert.Equal(3, carolFirst.Message.Sequence);
    }

    [Fact]
    public async Task EnterLobby_returns_lobby_song_library_snapshot_after_intersection()
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

        // Alice's library: {1, 2, 3}.
        var created = await aliceConn.InvokeAsync<CreateLobbyResult>(
            nameof(ILobbyHub.CreateLobby),
            new CreateLobbyArgs("Lib Lobby", GameMode.Band, Region.UsEast, "Track", 4,
                Lib(Hash1, Hash2, Hash3)));
        Assert.Equal(3, created.Lobby.SharedSongCount);

        // Bob joins with library {2, 3, 4}. Intersection = {2, 3}.
        var entered = await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby),
            new EnterLobbyArgs(created.Lobby.Id, Lib(Hash2, Hash3, Hash4)));

        Assert.Equal(2, entered.Lobby.SharedSongCount);
        // Bob's library is {2, 3, 4}, intersection is {2, 3}, so the joiner is told
        // to remove {4} (the song Alice doesn't have).
        Assert.Equal(new[] { Hash4 }, entered.LibraryRemovals.OrderBy(x => x));

        // Alice (still in the lobby group) is notified of the removed hash.
        var update = await alice.NextLibraryUpdate();
        Assert.Equal(created.Lobby.Id, update.LobbyId);
        Assert.Empty(update.Added);
        Assert.Equal(new[] { Hash1 }, update.Removed);
    }

    [Fact]
    public async Task LeaveLobby_broadcasts_added_hashes_when_intersection_grows()
    {
        var aliceToken = await IssueTokenAsync("alice");
        var bobToken = await IssueTokenAsync("bob");
        var carolToken = await IssueTokenAsync("carol");

        var alice = new ClientHarness();
        var bob = new ClientHarness();
        var carol = new ClientHarness();

        await using var aliceConn = BuildConnection(aliceToken, alice);
        await using var bobConn = BuildConnection(bobToken, bob);
        await using var carolConn = BuildConnection(carolToken, carol);

        await aliceConn.StartAsync();
        await bobConn.StartAsync();
        await carolConn.StartAsync();
        await alice.NextSnapshot();
        await bob.NextSnapshot();
        await carol.NextSnapshot();

        // Alice's library: {1, 2, 3}.
        var created = await aliceConn.InvokeAsync<CreateLobbyResult>(
            nameof(ILobbyHub.CreateLobby),
            new CreateLobbyArgs("Leave Lobby", GameMode.Band, Region.UsEast, "Track", 4,
                Lib(Hash1, Hash2, Hash3)));

        // Bob (full library) and Carol (missing 3) join. After both, shared = {1, 2}.
        await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, Lib(Hash1, Hash2, Hash3)));
        await alice.NextPlayerJoined();
        await carolConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, Lib(Hash1, Hash2)));
        await alice.NextPlayerJoined();
        await bob.NextPlayerJoined();

        // Alice received a library-update when Carol joined (Hash3 dropped). Drain it.
        var dropUpdate = await alice.NextLibraryUpdate();
        Assert.Equal(new[] { Hash3 }, dropUpdate.Removed);
        var bobDropUpdate = await bob.NextLibraryUpdate();
        Assert.Equal(new[] { Hash3 }, bobDropUpdate.Removed);

        // Carol leaves — Hash3 returns to the shared set; Alice and Bob are notified.
        await carolConn.InvokeAsync(nameof(ILobbyHub.LeaveLobby));

        await alice.NextPlayerLeft();
        await bob.NextPlayerLeft();

        var aliceAdd = await alice.NextLibraryUpdate();
        var bobAdd = await bob.NextLibraryUpdate();

        Assert.Equal(new[] { Hash3 }, aliceAdd.Added);
        Assert.Empty(aliceAdd.Removed);
        Assert.Equal(new[] { Hash3 }, bobAdd.Added);
    }

    [Fact]
    public async Task SharedSongCount_propagates_to_browse_batch_updates()
    {
        var aliceToken = await IssueTokenAsync("alice");
        var bobToken = await IssueTokenAsync("bob");
        var browserToken = await IssueTokenAsync("browser");

        var alice = new ClientHarness();
        var bob = new ClientHarness();
        var browser = new ClientHarness();

        await using var aliceConn = BuildConnection(aliceToken, alice);
        await using var bobConn = BuildConnection(bobToken, bob);
        await using var browserConn = BuildConnection(browserToken, browser);

        await aliceConn.StartAsync();
        await bobConn.StartAsync();
        await browserConn.StartAsync();
        await alice.NextSnapshot();
        await bob.NextSnapshot();
        await browser.NextSnapshot();

        var created = await aliceConn.InvokeAsync<CreateLobbyResult>(
            nameof(ILobbyHub.CreateLobby),
            new CreateLobbyArgs("Browse Lobby", GameMode.Band, Region.UsEast, "Track", 4,
                Lib(Hash1, Hash2, Hash3)));

        var addedBatch = await browser.NextBatch(b => b.Added.Length > 0);
        Assert.Equal(3, addedBatch.Added[0].SharedSongCount);

        await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, Lib(Hash2, Hash3)));

        var updatedBatch = await browser.NextBatch(
            b => b.Updated.Length > 0 && b.Updated[0].Id == created.Lobby.Id);
        Assert.Equal(2, updatedBatch.Updated[0].SharedSongCount);
    }

    [Fact]
    public async Task EnterLobby_with_empty_library_fails_validation()
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
            new CreateLobbyArgs("Lobby", GameMode.Band, Region.UsEast, "Track", 4, Lib(Hash1)));

        var ex = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => bobConn.InvokeAsync<EnterLobbyResult>(
                nameof(ILobbyHub.EnterLobby),
                new EnterLobbyArgs(created.Lobby.Id, Lib())));

        Assert.Contains("validation_failed", ex.Message);
    }

    [Fact]
    public async Task QueueSong_then_RemoveQueuedSong_broadcasts_to_lobby_with_missing_for()
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

        // Alice's library: {1, 2}. She'll queue Hash1, which Bob doesn't have.
        var created = await aliceConn.InvokeAsync<CreateLobbyResult>(
            nameof(ILobbyHub.CreateLobby),
            new CreateLobbyArgs("Queue Lobby", GameMode.Band, Region.UsEast, "Track", 4, Lib(Hash1, Hash2)));

        // Bob enters with only Hash2.
        var entered = await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby),
            new EnterLobbyArgs(created.Lobby.Id, Lib(Hash2)));
        Assert.Empty(entered.SongQueue);
        await alice.NextPlayerJoined();

        // Alice receives a library update when Bob joins (Hash1 dropped from shared) — drain it.
        await alice.NextLibraryUpdate();

        // Alice queues Hash1. Bob doesn't own it, so MissingFor contains Bob's user ID.
        var queuedReturn = await aliceConn.InvokeAsync<QueuedSongDto>(
            nameof(ILobbyHub.QueueSong),
            new QueueSongArgs(Hash1));
        Assert.Equal(Hash1, queuedReturn.SongHash);
        Assert.Equal(aliceId, queuedReturn.RequesterId);
        Assert.Equal(new[] { bobId }, queuedReturn.MissingFor);

        // Both Alice and Bob receive the broadcast.
        var aliceQueued = await alice.NextSongQueued();
        var bobQueued = await bob.NextSongQueued();
        Assert.Equal(queuedReturn.Sequence, aliceQueued.Song.Sequence);
        Assert.Equal(queuedReturn.Sequence, bobQueued.Song.Sequence);
        Assert.Equal(new[] { bobId }, bobQueued.Song.MissingFor);

        // Alice removes the entry. Both clients receive OnSongRemovedFromQueue with reason=Removed.
        await aliceConn.InvokeAsync(nameof(ILobbyHub.RemoveQueuedSong), new RemoveQueuedSongArgs(queuedReturn.Sequence));
        var aliceRemoved = await alice.NextSongRemoved();
        var bobRemoved = await bob.NextSongRemoved();
        Assert.Equal(queuedReturn.Sequence, aliceRemoved.Sequence);
        Assert.Equal(queuedReturn.Sequence, bobRemoved.Sequence);
        Assert.Equal(SongRemovalReason.Removed, aliceRemoved.Reason);
        Assert.Equal(SongRemovalReason.Removed, bobRemoved.Reason);
    }

    [Fact]
    public async Task TransferHost_promotes_target_and_notifies_both_clients()
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

        var created = await aliceConn.InvokeAsync<CreateLobbyResult>(
            nameof(ILobbyHub.CreateLobby),
            new CreateLobbyArgs("Transfer Lobby", GameMode.Band, Region.UsEast, "Track", 4, Lib(Hash1)));
        await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, Lib(Hash1)));
        await alice.NextPlayerJoined();

        await aliceConn.InvokeAsync(nameof(ILobbyHub.TransferHost), new TransferHostArgs(bobId));

        var aliceChange = await alice.NextHostChanged();
        var bobChange = await bob.NextHostChanged();

        Assert.Equal(created.Lobby.Id, aliceChange.LobbyId);
        Assert.Equal(bobId, aliceChange.NewHostUserId);
        Assert.Equal("bob", aliceChange.NewHostName);
        Assert.Equal(bobId, bobChange.NewHostUserId);
        Assert.Equal("bob", bobChange.NewHostName);

        // Sanity: now Alice (no longer host) cannot transfer further.
        var ex = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => aliceConn.InvokeAsync(nameof(ILobbyHub.TransferHost), new TransferHostArgs(aliceId)));
        Assert.Contains("not_host", ex.Message);
    }

    [Fact]
    public async Task TransferHost_called_by_non_host_throws_not_host()
    {
        var aliceToken = await IssueTokenAsync("alice");
        var (bobToken, bobId) = await IssueIdentityAsync("bob");

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
            new CreateLobbyArgs("Lobby", GameMode.Band, Region.UsEast, "Track", 4, Lib(Hash1)));
        await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, Lib(Hash1)));
        await alice.NextPlayerJoined();

        // Bob is not the host.
        var ex = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => bobConn.InvokeAsync(nameof(ILobbyHub.TransferHost), new TransferHostArgs(bobId)));
        Assert.Contains("not_host", ex.Message);
    }

    [Fact]
    public async Task KickPlayer_removes_target_returns_them_to_browse_and_bans_them()
    {
        var (aliceToken, _) = await IssueIdentityAsync("alice");
        var (bobToken, bobId) = await IssueIdentityAsync("bob");

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
            new CreateLobbyArgs("Kick Lobby", GameMode.Band, Region.UsEast, "Track", 4, Lib(Hash1)));
        await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, Lib(Hash1)));
        await alice.NextPlayerJoined();

        await aliceConn.InvokeAsync(nameof(ILobbyHub.KickPlayer), new KickPlayerArgs(bobId));

        // Both clients receive OnPlayerKicked (Bob still in the group when it fires).
        var aliceKick = await alice.NextPlayerKicked();
        var bobKick = await bob.NextPlayerKicked();
        Assert.Equal(created.Lobby.Id, aliceKick.LobbyId);
        Assert.Equal(bobId, aliceKick.UserId);
        Assert.Equal("kicked_by_host", aliceKick.Reason);
        Assert.Equal(bobId, bobKick.UserId);

        // Bob is returned to browse and receives a snapshot (the lobby still exists).
        var bobSnapshot = await bob.NextSnapshot();
        var visible = Assert.Single(bobSnapshot);
        Assert.Equal(created.Lobby.Id, visible.Id);
        Assert.Equal(1, visible.PlayerCount);

        // Alice (still in the lobby) sees the player leave.
        var aliceLeft = await alice.NextPlayerLeft();
        Assert.Equal(bobId, aliceLeft.UserId);

        // Bob cannot re-enter — he's banned for the lobby's lifetime.
        var ex = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => bobConn.InvokeAsync<EnterLobbyResult>(
                nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, Lib(Hash1))));
        Assert.Contains("banned_from_lobby", ex.Message);
    }

    [Fact]
    public async Task KickPlayer_called_by_non_host_throws_not_host()
    {
        var (aliceToken, aliceId) = await IssueIdentityAsync("alice");
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
            new CreateLobbyArgs("Lobby", GameMode.Band, Region.UsEast, "Track", 4, Lib(Hash1)));
        await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, Lib(Hash1)));
        await alice.NextPlayerJoined();

        var ex = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => bobConn.InvokeAsync(nameof(ILobbyHub.KickPlayer), new KickPlayerArgs(aliceId)));
        Assert.Contains("not_host", ex.Message);
    }

    [Fact]
    public async Task Host_disconnect_auto_transfers_to_next_member()
    {
        var aliceToken = await IssueTokenAsync("alice");
        var (bobToken, bobId) = await IssueIdentityAsync("bob");

        var alice = new ClientHarness();
        var bob = new ClientHarness();

        var aliceConn = BuildConnection(aliceToken, alice);
        await using var bobConn = BuildConnection(bobToken, bob);

        await aliceConn.StartAsync();
        await bobConn.StartAsync();
        await alice.NextSnapshot();
        await bob.NextSnapshot();

        var created = await aliceConn.InvokeAsync<CreateLobbyResult>(
            nameof(ILobbyHub.CreateLobby),
            new CreateLobbyArgs("Disconnect Lobby", GameMode.Band, Region.UsEast, "Track", 4, Lib(Hash1)));
        await bobConn.InvokeAsync<EnterLobbyResult>(
            nameof(ILobbyHub.EnterLobby), new EnterLobbyArgs(created.Lobby.Id, Lib(Hash1)));
        await alice.NextPlayerJoined();

        // Alice (the host) disconnects abruptly.
        await aliceConn.DisposeAsync();

        // Bob, still in the lobby, sees both OnPlayerLeft (Alice gone) and OnHostChanged (himself promoted).
        var bobLeft = await bob.NextPlayerLeft();
        Assert.Equal(created.Lobby.Id, bobLeft.LobbyId);

        var bobHostChange = await bob.NextHostChanged();
        Assert.Equal(created.Lobby.Id, bobHostChange.LobbyId);
        Assert.Equal(bobId, bobHostChange.NewHostUserId);
        Assert.Equal("bob", bobHostChange.NewHostName);

        // Sanity: Bob (now host) can still leave cleanly without an exception.
        await bobConn.InvokeAsync(nameof(ILobbyHub.LeaveLobby));
        await bob.NextSnapshot();
    }

    private async Task<string> IssueTokenAsync(string displayName)
    {
        var (token, _) = await IssueIdentityAsync(displayName);
        return token;
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
        private readonly Queue<LobbyBatchUpdate> _batches = new();
        private readonly Queue<(string LobbyId, string UserId, string DisplayName)> _joins = new();
        private readonly Queue<(string LobbyId, string UserId)> _leaves = new();
        private readonly Queue<(string LobbyId, ChatMessage Message)> _chats = new();
        private readonly Queue<(string LobbyId, string[] Added, string[] Removed)> _libraryUpdates = new();
        private readonly Queue<(string LobbyId, QueuedSongDto Song)> _songsQueued = new();
        private readonly Queue<(string LobbyId, long Sequence, SongRemovalReason Reason)> _songsRemoved = new();
        private readonly Queue<(string LobbyId, long Sequence, string[] AddedMissing, string[] RemovedMissing)> _availabilityUpdates = new();
        private readonly Queue<(string LobbyId, string NewHostUserId, string NewHostName)> _hostChanges = new();
        private readonly Queue<(string LobbyId, string UserId, string Reason)> _kicks = new();

        private TaskCompletionSource<LobbyDto[]>? _snapshotWaiter;
        private TaskCompletionSource<LobbyBatchUpdate>? _batchWaiter;
        private Func<LobbyBatchUpdate, bool>? _batchPredicate;
        private TaskCompletionSource<(string, string, string)>? _joinWaiter;
        private TaskCompletionSource<(string, string)>? _leaveWaiter;
        private TaskCompletionSource<(string LobbyId, ChatMessage Message)>? _chatWaiter;
        private TaskCompletionSource<(string LobbyId, string[] Added, string[] Removed)>? _libraryUpdateWaiter;
        private TaskCompletionSource<(string LobbyId, QueuedSongDto Song)>? _songQueuedWaiter;
        private TaskCompletionSource<(string LobbyId, long Sequence, SongRemovalReason Reason)>? _songRemovedWaiter;
        private TaskCompletionSource<(string LobbyId, long Sequence, string[] AddedMissing, string[] RemovedMissing)>? _availabilityUpdateWaiter;
        private TaskCompletionSource<(string LobbyId, string NewHostUserId, string NewHostName)>? _hostChangeWaiter;
        private TaskCompletionSource<(string LobbyId, string UserId, string Reason)>? _kickWaiter;

        private readonly object _lock = new();

        public void Bind(HubConnection conn)
        {
            conn.On<LobbyDto[]>("OnLobbySnapshot", lobbies =>
            {
                lock (_lock)
                {
                    if (_snapshotWaiter is { } w)
                    {
                        _snapshotWaiter = null;
                        w.TrySetResult(lobbies);
                    }
                    else
                    {
                        _snapshots.Enqueue(lobbies);
                    }
                }
            });
            conn.On<LobbyBatchUpdate>("OnLobbyBatch", batch =>
            {
                lock (_lock)
                {
                    if (_batchWaiter is { } w && (_batchPredicate?.Invoke(batch) ?? true))
                    {
                        _batchWaiter = null;
                        _batchPredicate = null;
                        w.TrySetResult(batch);
                    }
                    else
                    {
                        _batches.Enqueue(batch);
                    }
                }
            });
            conn.On<PlayerJoinedEvent>("OnPlayerJoined", e =>
            {
                lock (_lock)
                {
                    if (_joinWaiter is { } w)
                    {
                        _joinWaiter = null;
                        w.TrySetResult((e.LobbyId, e.UserId, e.DisplayName));
                    }
                    else
                    {
                        _joins.Enqueue((e.LobbyId, e.UserId, e.DisplayName));
                    }
                }
            });
            conn.On<PlayerLeftEvent>("OnPlayerLeft", e =>
            {
                lock (_lock)
                {
                    if (_leaveWaiter is { } w)
                    {
                        _leaveWaiter = null;
                        w.TrySetResult((e.LobbyId, e.UserId));
                    }
                    else
                    {
                        _leaves.Enqueue((e.LobbyId, e.UserId));
                    }
                }
            });
            conn.On<ChatMessageEvent>("OnChatMessage", e =>
            {
                lock (_lock)
                {
                    if (_chatWaiter is { } w)
                    {
                        _chatWaiter = null;
                        w.TrySetResult((e.LobbyId, e.Message));
                    }
                    else
                    {
                        _chats.Enqueue((e.LobbyId, e.Message));
                    }
                }
            });
            conn.On<LobbySongLibraryUpdatedEvent>("OnLobbySongLibraryUpdated", e =>
            {
                lock (_lock)
                {
                    if (_libraryUpdateWaiter is { } w)
                    {
                        _libraryUpdateWaiter = null;
                        w.TrySetResult((e.LobbyId, e.Added, e.Removed));
                    }
                    else
                    {
                        _libraryUpdates.Enqueue((e.LobbyId, e.Added, e.Removed));
                    }
                }
            });
            conn.On<SongQueuedEvent>("OnSongQueued", e =>
            {
                lock (_lock)
                {
                    if (_songQueuedWaiter is { } w)
                    {
                        _songQueuedWaiter = null;
                        w.TrySetResult((e.LobbyId, e.Song));
                    }
                    else
                    {
                        _songsQueued.Enqueue((e.LobbyId, e.Song));
                    }
                }
            });
            conn.On<SongRemovedFromQueueEvent>("OnSongRemovedFromQueue", e =>
            {
                lock (_lock)
                {
                    if (_songRemovedWaiter is { } w)
                    {
                        _songRemovedWaiter = null;
                        w.TrySetResult((e.LobbyId, e.Sequence, e.Reason));
                    }
                    else
                    {
                        _songsRemoved.Enqueue((e.LobbyId, e.Sequence, e.Reason));
                    }
                }
            });
            conn.On<QueuedSongAvailabilityChangedEvent>("OnQueuedSongAvailabilityChanged", e =>
            {
                lock (_lock)
                {
                    if (_availabilityUpdateWaiter is { } w)
                    {
                        _availabilityUpdateWaiter = null;
                        w.TrySetResult((e.LobbyId, e.Sequence, e.AddedMissing, e.RemovedMissing));
                    }
                    else
                    {
                        _availabilityUpdates.Enqueue((e.LobbyId, e.Sequence, e.AddedMissing, e.RemovedMissing));
                    }
                }
            });
            conn.On<HostChangedEvent>("OnHostChanged", e =>
            {
                lock (_lock)
                {
                    if (_hostChangeWaiter is { } w)
                    {
                        _hostChangeWaiter = null;
                        w.TrySetResult((e.LobbyId, e.NewHostUserId, e.NewHostName));
                    }
                    else
                    {
                        _hostChanges.Enqueue((e.LobbyId, e.NewHostUserId, e.NewHostName));
                    }
                }
            });
            conn.On<PlayerKickedEvent>("OnPlayerKicked", e =>
            {
                lock (_lock)
                {
                    if (_kickWaiter is { } w)
                    {
                        _kickWaiter = null;
                        w.TrySetResult((e.LobbyId, e.UserId, e.Reason));
                    }
                    else
                    {
                        _kicks.Enqueue((e.LobbyId, e.UserId, e.Reason));
                    }
                }
            });
        }

        public Task<LobbyDto[]> NextSnapshot() => Pull(_snapshots, ref _snapshotWaiter);
        public Task<(string LobbyId, string UserId, string DisplayName)> NextPlayerJoined() => Pull(_joins, ref _joinWaiter);
        public Task<(string LobbyId, string UserId)> NextPlayerLeft() => Pull(_leaves, ref _leaveWaiter);
        public Task<(string LobbyId, ChatMessage Message)> NextChatMessage() => Pull(_chats, ref _chatWaiter);
        public Task<(string LobbyId, string[] Added, string[] Removed)> NextLibraryUpdate() => Pull(_libraryUpdates, ref _libraryUpdateWaiter);
        public Task<(string LobbyId, QueuedSongDto Song)> NextSongQueued() => Pull(_songsQueued, ref _songQueuedWaiter);
        public Task<(string LobbyId, long Sequence, SongRemovalReason Reason)> NextSongRemoved() => Pull(_songsRemoved, ref _songRemovedWaiter);
        public Task<(string LobbyId, long Sequence, string[] AddedMissing, string[] RemovedMissing)> NextAvailabilityUpdate() => Pull(_availabilityUpdates, ref _availabilityUpdateWaiter);
        public Task<(string LobbyId, string NewHostUserId, string NewHostName)> NextHostChanged() => Pull(_hostChanges, ref _hostChangeWaiter);
        public Task<(string LobbyId, string UserId, string Reason)> NextPlayerKicked() => Pull(_kicks, ref _kickWaiter);

        public Task<LobbyBatchUpdate> NextBatch(Func<LobbyBatchUpdate, bool> predicate)
        {
            lock (_lock)
            {
                while (_batches.TryDequeue(out var queued))
                {
                    if (predicate(queued))
                    {
                        return Task.FromResult(queued);
                    }
                }
                var tcs = new TaskCompletionSource<LobbyBatchUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
                _batchWaiter = tcs;
                _batchPredicate = predicate;
                return tcs.Task.WaitAsync(WaitTimeout);
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
