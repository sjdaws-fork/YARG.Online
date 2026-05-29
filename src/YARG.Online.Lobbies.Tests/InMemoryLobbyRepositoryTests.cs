using Microsoft.Extensions.Options;
using Xunit;
using YARG.Online.Lobbies.Allocation;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Domain;
using YARG.Online.Lobbies.Lobbies;

namespace YARG.Online.Lobbies.Tests;

/// <summary>
/// Adapter for tests that were written against the pre-factory CreateAsync signature.
/// Keeps the test bodies readable (<c>repo.CreateAsync(NewLobby(), ...)</c>) while the
/// production interface now takes a factory so the repo can mint IDs internally.
/// The Lobby passed here is captured by closure and returned regardless of what ID
/// the repo generates — the dictionary keys off the Lobby's Id, so tests retain
/// deterministic IDs without needing a fake generator that knows them upfront.
/// </summary>
internal static class LobbyRepositoryTestExtensions
{
    public static Task<Lobby?> CreateAsync(
        this ILobbyRepository repo,
        Lobby lobby,
        IReadOnlyCollection<string> hostLibrary,
        byte hostInstrument,
        CancellationToken ct) =>
        repo.CreateAsync(_ => lobby, hostLibrary, hostInstrument, ct);

    // Pre-songSpeed signature. Defaults speed to 1.0 (100%) for tests that don't care.
    public static Task<EnqueueResult> EnqueueSongAsync(
        this ILobbyRepository repo,
        string lobbyId,
        string userId,
        string songHash,
        DateTimeOffset now,
        CancellationToken ct) =>
        repo.EnqueueSongAsync(lobbyId, userId, songHash, 1f, now, ct);
}

public class InMemoryLobbyRepositoryTests
{
    private static readonly string[] EmptyLib = Array.Empty<string>();

    // The repo now owns ID generation, but the tests still want to pin lobbies to
    // hard-coded IDs ("AAAAAAAA", "BBBBBBBB" etc.) so they can address them in
    // follow-up JoinAsync / GetAsync calls. The fix is to inject a stub generator
    // whose output is ignored by the test's factory shim (see CreateAsync extension
    // below) — the Lobby the test passes in carries the deterministic Id and the
    // ConcurrentDictionary keys off that, not the generator's output.
    private sealed class StubLobbyIdGenerator : ILobbyIdGenerator
    {
        public string Next() => "STUB-ID";
    }

    private static InMemoryLobbyRepository NewRepo(int maxChatHistorySize = 100) =>
        new(new StubLobbyIdGenerator(),
            Options.Create(new LobbyOptions { MaxChatHistorySize = maxChatHistorySize }));

    // Two-step StartGame helper for tests that don't care about the intermediate Starting
    // state or about exercising allocator failures — synthesizes a placeholder allocation.
    private static async Task<BeginStartGameResultData> StartGameAsync(InMemoryLobbyRepository repo, string lobbyId, string callerUserId)
    {
        var begin = await repo.BeginStartGameAsync(lobbyId, callerUserId, default);
        if (begin.Outcome != StartGameOutcome.Started) return begin;
        var allocation = new GameAllocation("test:0", GameServerName: null, SlotCount: begin.MemberCount);
        await repo.ConfirmStartGameAsync(lobbyId, allocation, default);
        return begin;
    }

    private static Lobby NewLobby(string id = "AAAAAAAA", string hostId = "host", int maxPlayers = 4) => new(
        Id: id,
        Name: "name",
        HostUserId: hostId,
        HostName: "Host",
        GameMode: GameMode.Band,
        Region: Region.UsEast,
        Song: "song",
        PlayerCount: 1,
        MaxPlayers: maxPlayers,
        CreatedAt: DateTimeOffset.UtcNow,
        SharedSongCount: 0);

    [Fact]
    public async Task CreateAsync_returns_lobby_for_new_id_and_null_when_all_attempts_collide()
    {
        var repo = NewRepo();
        var lobby = NewLobby();

        // First create: succeeds.
        Assert.NotNull(await repo.CreateAsync(lobby, EmptyLib, 0, default));

        // Second create with the same Lobby (factory ignores the generator-provided ID
        // and returns the same hard-coded Id every retry). Exhausts the attempt budget,
        // so the repo reports collision via a null return.
        Assert.Null(await repo.CreateAsync(lobby, EmptyLib, 0, default));
    }

    [Fact]
    public async Task JoinAsync_joined_then_already_member()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(), EmptyLib, 0, default);

        var first = await repo.JoinAsync("AAAAAAAA", "player-1", "player-1", EmptyLib, 0, default);
        Assert.Equal(JoinResult.Joined, first.Result);
        Assert.Equal(2, first.Lobby!.PlayerCount);

        var second = await repo.JoinAsync("AAAAAAAA", "player-1", "player-1", EmptyLib, 0, default);
        Assert.Equal(JoinResult.AlreadyMember, second.Result);
    }

    [Fact]
    public async Task JoinAsync_returns_NotFound_for_unknown_lobby()
    {
        var repo = NewRepo();
        var result = await repo.JoinAsync("ZZZZZZZZ", "u", "u", EmptyLib, 0, default);
        Assert.Equal(JoinResult.NotFound, result.Result);
    }

    [Fact]
    public async Task JoinAsync_returns_Full_at_max_players()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(maxPlayers: 2), EmptyLib, 0, default);

        await repo.JoinAsync("AAAAAAAA", "p1", "p1", EmptyLib, 0, default);
        var result = await repo.JoinAsync("AAAAAAAA", "p2", "p2", EmptyLib, 0, default);
        Assert.Equal(JoinResult.Full, result.Result);
    }

    [Fact]
    public async Task LeaveAsync_non_host_decrements_count()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(), EmptyLib, 0, default);
        await repo.JoinAsync("AAAAAAAA", "player-1", "player-1", EmptyLib, 0, default);

        var result = await repo.LeaveAsync("AAAAAAAA", "player-1", default);

        Assert.Equal(LeaveOutcome.Left, result.Outcome);
        Assert.Equal(1, result.Lobby!.PlayerCount);
    }

    [Fact]
    public async Task LeaveAsync_host_with_remaining_members_auto_transfers()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), EmptyLib, 0, default);
        await repo.JoinAsync("AAAAAAAA", "player-1", "Player One", EmptyLib, 0, default);
        await repo.JoinAsync("AAAAAAAA", "player-2", "Player Two", EmptyLib, 0, default);

        var result = await repo.LeaveAsync("AAAAAAAA", "host", default);

        Assert.Equal(LeaveOutcome.Left, result.Outcome);
        Assert.NotNull(result.HostChange);
        // player-1 joined first after the host, so they're promoted.
        Assert.Equal("player-1", result.HostChange!.NewHostUserId);
        Assert.Equal("Player One", result.HostChange.NewHostName);

        var lobby = await repo.GetAsync("AAAAAAAA", default);
        Assert.NotNull(lobby);
        Assert.Equal("player-1", lobby!.HostUserId);
        Assert.Equal("Player One", lobby.HostName);
        Assert.Equal(2, lobby.PlayerCount);
    }

    [Fact]
    public async Task LeaveAsync_last_member_closes_lobby()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), EmptyLib, 0, default);

        // Host is the only member; leaving closes the lobby (also covers the host case).
        var result = await repo.LeaveAsync("AAAAAAAA", "host", default);

        Assert.Equal(LeaveOutcome.LobbyClosed, result.Outcome);
    }

    [Fact]
    public async Task LeaveAsync_returns_NotFound_for_unknown_user()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(), EmptyLib, 0, default);

        var result = await repo.LeaveAsync("AAAAAAAA", "nobody", default);
        Assert.Equal(LeaveOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task GetMembersAsync_returns_current_members()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), EmptyLib, 0, default);
        await repo.JoinAsync("AAAAAAAA", "player-1", "player-1", EmptyLib, 0, default);

        var members = await repo.GetMembersAsync("AAAAAAAA", default);

        Assert.Equal(2, members.Count);
        Assert.Contains(members, m => m.UserId == "host" && m.DisplayName == "Host");
        Assert.Contains(members, m => m.UserId == "player-1" && m.DisplayName == "player-1");
    }

    [Fact]
    public async Task SearchAsync_filters_by_game_mode()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby("AAAAAAAA"), EmptyLib, 0, default);
        await repo.CreateAsync(NewLobby("BBBBBBBB") with { GameMode = GameMode.Quickplay }, EmptyLib, 0, default);

        var bandOnly = await repo.SearchAsync(new LobbySearchQuery(0, 10, GameMode.Band, null, null), default);

        var item = Assert.Single(bandOnly.Items);
        Assert.Equal("AAAAAAAA", item.Id);
    }

    [Fact]
    public async Task SearchAsync_filters_by_query_string_against_name_and_song()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby("AAAAAAAA") with { Name = "Friday Night" }, EmptyLib, 0, default);
        await repo.CreateAsync(NewLobby("BBBBBBBB") with { Song = "Saturday Anthem" }, EmptyLib, 0, default);
        await repo.CreateAsync(NewLobby("CCCCCCCC") with { Name = "Other", Song = "Other" }, EmptyLib, 0, default);

        var hits = await repo.SearchAsync(new LobbySearchQuery(0, 10, null, null, "day"), default);

        Assert.Equal(2, hits.Items.Count);
        Assert.DoesNotContain(hits.Items, l => l.Id == "CCCCCCCC");
    }

    [Fact]
    public async Task AppendChatMessageAsync_assigns_monotonic_sequence_starting_at_1()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(), EmptyLib, 0, default);

        var sent = DateTimeOffset.UtcNow;
        var first = await repo.AppendChatMessageAsync("AAAAAAAA", "host", "Host", "hi", sent, default);
        var second = await repo.AppendChatMessageAsync("AAAAAAAA", "host", "Host", "again", sent, default);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, first!.Sequence);
        Assert.Equal(2, second!.Sequence);
        Assert.Equal("hi", first.Text);
    }

    [Fact]
    public async Task AppendChatMessageAsync_returns_null_for_unknown_lobby()
    {
        var repo = NewRepo();
        var result = await repo.AppendChatMessageAsync("ZZZZZZZZ", "host", "Host", "hi", DateTimeOffset.UtcNow, default);
        Assert.Null(result);
    }

    [Fact]
    public async Task AppendChatMessageAsync_returns_null_for_non_member()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(), EmptyLib, 0, default);

        var result = await repo.AppendChatMessageAsync("AAAAAAAA", "stranger", "Stranger", "hi", DateTimeOffset.UtcNow, default);
        Assert.Null(result);
    }

    [Fact]
    public async Task AppendChatMessageAsync_evicts_oldest_when_history_full()
    {
        var repo = NewRepo(maxChatHistorySize: 3);
        await repo.CreateAsync(NewLobby(), EmptyLib, 0, default);

        var t = DateTimeOffset.UtcNow;
        await repo.AppendChatMessageAsync("AAAAAAAA", "host", "Host", "m1", t, default);
        await repo.AppendChatMessageAsync("AAAAAAAA", "host", "Host", "m2", t, default);
        await repo.AppendChatMessageAsync("AAAAAAAA", "host", "Host", "m3", t, default);
        await repo.AppendChatMessageAsync("AAAAAAAA", "host", "Host", "m4", t, default);

        var history = await repo.GetChatHistoryAsync("AAAAAAAA", default);

        Assert.Equal(3, history.Count);
        Assert.Equal(new[] { "m2", "m3", "m4" }, history.Select(m => m.Text));
        Assert.Equal(new long[] { 2, 3, 4 }, history.Select(m => m.Sequence));
    }

    [Fact]
    public async Task GetChatHistoryAsync_returns_empty_for_unknown_lobby()
    {
        var repo = NewRepo();
        var history = await repo.GetChatHistoryAsync("ZZZZZZZZ", default);
        Assert.Empty(history);
    }

    // --- Song library tests ---

    [Fact]
    public async Task CreateAsync_initializes_lobby_song_library_to_host_library()
    {
        var repo = NewRepo();
        var hostLib = new[] { "h1", "h2", "h3" };

        await repo.CreateAsync(NewLobby(), hostLib, 0, default);

        var lobby = await repo.GetAsync("AAAAAAAA", default);
        Assert.Equal(3, lobby!.SharedSongCount);
    }

    [Fact]
    public async Task JoinAsync_intersects_lobby_library_with_new_player_library()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(), new[] { "h1", "h2", "h3" }, 0, default);

        var join = await repo.JoinAsync("AAAAAAAA", "player-1", "player-1", new[] { "h2", "h3", "h4" }, 0, default);

        Assert.Equal(JoinResult.Joined, join.Result);
        Assert.Equal(2, join.Lobby!.SharedSongCount);
        Assert.NotNull(join.LobbySongLibrarySnapshot);
        Assert.Equal(new[] { "h2", "h3" }.OrderBy(x => x), join.LobbySongLibrarySnapshot!.OrderBy(x => x));
    }

    [Fact]
    public async Task JoinAsync_delta_removed_lists_hashes_no_longer_shared()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(), new[] { "h1", "h2", "h3" }, 0, default);

        var join = await repo.JoinAsync("AAAAAAAA", "player-1", "player-1", new[] { "h2" }, 0, default);

        Assert.NotNull(join.Delta);
        Assert.Empty(join.Delta!.Added);
        Assert.Equal(new[] { "h1", "h3" }.OrderBy(x => x), join.Delta!.Removed.OrderBy(x => x));
    }

    [Fact]
    public async Task JoinAsync_disjoint_library_collapses_shared_set_to_empty()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(), new[] { "h1", "h2" }, 0, default);

        var join = await repo.JoinAsync("AAAAAAAA", "player-1", "player-1", new[] { "h9", "h10" }, 0, default);

        Assert.Equal(JoinResult.Joined, join.Result);
        Assert.Equal(0, join.Lobby!.SharedSongCount);
        Assert.Empty(join.LobbySongLibrarySnapshot!);
    }

    [Fact]
    public async Task JoinAsync_returns_snapshot_on_already_member_without_delta()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1", "h2" }, 0, default);

        // Host attempts to "rejoin" with a different library — snapshot returned, no delta, library unchanged.
        var rejoin = await repo.JoinAsync("AAAAAAAA", "host", "host", new[] { "h9" }, 0, default);

        Assert.Equal(JoinResult.AlreadyMember, rejoin.Result);
        Assert.Null(rejoin.Delta);
        Assert.Equal(new[] { "h1", "h2" }.OrderBy(x => x), rejoin.LobbySongLibrarySnapshot!.OrderBy(x => x));
    }

    [Fact]
    public async Task LeaveAsync_recalculates_intersection_across_remaining_players()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1", "h2", "h3" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h1", "h2", "h3" }, 0, default);
        // p2 doesn't have h3 — pulls h3 out of the shared library.
        await repo.JoinAsync("AAAAAAAA", "p2", "p2", new[] { "h1", "h2" }, 0, default);

        var beforeLeave = await repo.GetAsync("AAAAAAAA", default);
        Assert.Equal(2, beforeLeave!.SharedSongCount);

        var leave = await repo.LeaveAsync("AAAAAAAA", "p2", default);

        Assert.Equal(LeaveOutcome.Left, leave.Outcome);
        Assert.Equal(3, leave.Lobby!.SharedSongCount);
        Assert.NotNull(leave.Delta);
        Assert.Equal(new[] { "h3" }, leave.Delta!.Added);
        Assert.Empty(leave.Delta!.Removed);
    }

    [Fact]
    public async Task LeaveAsync_with_no_newly_shared_songs_returns_empty_delta()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1", "h2" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h1", "h2" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p2", "p2", new[] { "h1", "h2" }, 0, default);

        var leave = await repo.LeaveAsync("AAAAAAAA", "p2", default);

        Assert.Equal(LeaveOutcome.Left, leave.Outcome);
        Assert.Equal(2, leave.Lobby!.SharedSongCount);
        Assert.NotNull(leave.Delta);
        Assert.Empty(leave.Delta!.Added);
        Assert.Empty(leave.Delta!.Removed);
    }

    [Fact]
    public async Task Concurrent_joins_produce_correct_intersection_regardless_of_order()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1", "h2", "h3", "h4" }, 0, default);

        // Two players join in parallel. Final shared library must equal hostLib ∩ libA ∩ libB
        // = {h1,h2,h3,h4} ∩ {h1,h2,h3} ∩ {h2,h3,h4} = {h2,h3}.
        await Task.WhenAll(
            repo.JoinAsync("AAAAAAAA", "pA", "pA", new[] { "h1", "h2", "h3" }, 0, default),
            repo.JoinAsync("AAAAAAAA", "pB", "pB", new[] { "h2", "h3", "h4" }, 0, default));

        var lobby = await repo.GetAsync("AAAAAAAA", default);
        Assert.Equal(2, lobby!.SharedSongCount);
    }

    [Fact]
    public async Task JoinAsync_normalizes_duplicate_hashes_in_player_library()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(), new[] { "h1", "h2" }, 0, default);

        var join = await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h1", "h1", "h2", "h2" }, 0, default);

        Assert.Equal(2, join.Lobby!.SharedSongCount);
    }

    // --- Song queue tests ---

    private static InMemoryLobbyRepository NewRepoWithQueueCap(int maxQueueSize) =>
        new(new StubLobbyIdGenerator(),
            Options.Create(new LobbyOptions { MaxQueueSize = maxQueueSize }));

    [Fact]
    public async Task EnqueueSongAsync_assigns_monotonic_sequences_and_excludes_requester_from_missing()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1", "h2" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h2" }, 0, default);

        var t = DateTimeOffset.UtcNow;
        var first = await repo.EnqueueSongAsync("AAAAAAAA", "host", "h1", t, default);
        var second = await repo.EnqueueSongAsync("AAAAAAAA", "host", "h2", t, default);

        Assert.Equal(EnqueueOutcome.Added, first.Outcome);
        Assert.Equal(EnqueueOutcome.Added, second.Outcome);
        Assert.Equal(1, first.Entry!.Sequence);
        Assert.Equal(2, second.Entry!.Sequence);
        Assert.Equal("host", first.Entry.RequesterId);
        // p1 doesn't own h1, so they should appear in MissingFor; requester (host) never does.
        Assert.Equal(new[] { "p1" }, first.Entry.MissingFor);
        Assert.DoesNotContain("host", first.Entry.MissingFor);
        // p1 owns h2 — nobody is missing.
        Assert.Empty(second.Entry.MissingFor);
    }

    [Fact]
    public async Task EnqueueSongAsync_rejects_NotInLibrary_when_requester_doesnt_own()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1" }, 0, default);

        var result = await repo.EnqueueSongAsync("AAAAAAAA", "host", "h2", DateTimeOffset.UtcNow, default);

        Assert.Equal(EnqueueOutcome.NotInLibrary, result.Outcome);
        Assert.Null(result.Entry);
    }

    [Fact]
    public async Task EnqueueSongAsync_rejects_NotMember_for_non_member()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1" }, 0, default);

        var result = await repo.EnqueueSongAsync("AAAAAAAA", "stranger", "h1", DateTimeOffset.UtcNow, default);

        Assert.Equal(EnqueueOutcome.NotMember, result.Outcome);
    }

    [Fact]
    public async Task EnqueueSongAsync_returns_QueueFull_when_capacity_reached()
    {
        var repo = NewRepoWithQueueCap(2);
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1", "h2", "h3" }, 0, default);

        await repo.EnqueueSongAsync("AAAAAAAA", "host", "h1", DateTimeOffset.UtcNow, default);
        await repo.EnqueueSongAsync("AAAAAAAA", "host", "h2", DateTimeOffset.UtcNow, default);
        var third = await repo.EnqueueSongAsync("AAAAAAAA", "host", "h3", DateTimeOffset.UtcNow, default);

        Assert.Equal(EnqueueOutcome.QueueFull, third.Outcome);
    }

    [Fact]
    public async Task RemoveQueuedSongAsync_allowed_for_host_even_on_other_users_entry()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h1" }, 0, default);
        var added = await repo.EnqueueSongAsync("AAAAAAAA", "p1", "h1", DateTimeOffset.UtcNow, default);

        var result = await repo.RemoveQueuedSongAsync("AAAAAAAA", "host", added.Entry!.Sequence, default);

        Assert.Equal(RemoveQueuedSongOutcome.Removed, result.Outcome);
        Assert.Equal(added.Entry.Sequence, result.Entry!.Sequence);
        Assert.Empty(await repo.GetQueueAsync("AAAAAAAA", default));
    }

    [Fact]
    public async Task RemoveQueuedSongAsync_allowed_for_requester()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h1" }, 0, default);
        var added = await repo.EnqueueSongAsync("AAAAAAAA", "p1", "h1", DateTimeOffset.UtcNow, default);

        var result = await repo.RemoveQueuedSongAsync("AAAAAAAA", "p1", added.Entry!.Sequence, default);

        Assert.Equal(RemoveQueuedSongOutcome.Removed, result.Outcome);
    }

    [Fact]
    public async Task RemoveQueuedSongAsync_rejected_for_non_host_non_requester()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h1" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p2", "p2", new[] { "h1" }, 0, default);
        var added = await repo.EnqueueSongAsync("AAAAAAAA", "p1", "h1", DateTimeOffset.UtcNow, default);

        var result = await repo.RemoveQueuedSongAsync("AAAAAAAA", "p2", added.Entry!.Sequence, default);

        Assert.Equal(RemoveQueuedSongOutcome.NotPermitted, result.Outcome);
        Assert.Single(await repo.GetQueueAsync("AAAAAAAA", default));
    }

    [Fact]
    public async Task RemoveQueuedSongAsync_returns_EntryMissing_for_unknown_sequence()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1" }, 0, default);

        var result = await repo.RemoveQueuedSongAsync("AAAAAAAA", "host", 999, default);

        Assert.Equal(RemoveQueuedSongOutcome.EntryMissing, result.Outcome);
    }

    [Fact]
    public async Task JoinAsync_appends_new_player_to_MissingFor_for_unowned_queue_entries()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1", "h2" }, 0, default);
        await repo.EnqueueSongAsync("AAAAAAAA", "host", "h1", DateTimeOffset.UtcNow, default);
        await repo.EnqueueSongAsync("AAAAAAAA", "host", "h2", DateTimeOffset.UtcNow, default);

        // p1 owns h2 but not h1.
        var join = await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h2" }, 0, default);

        Assert.NotNull(join.QueueAvailabilityUpdates);
        var delta = Assert.Single(join.QueueAvailabilityUpdates!);
        Assert.Equal(1, delta.Sequence);
        Assert.Equal(new[] { "p1" }, delta.AddedMissing);
        Assert.Empty(delta.RemovedMissing);

        var queue = await repo.GetQueueAsync("AAAAAAAA", default);
        var entryH1 = queue.Single(q => q.SongHash == "h1");
        var entryH2 = queue.Single(q => q.SongHash == "h2");
        Assert.Equal(new[] { "p1" }, entryH1.MissingFor);
        Assert.Empty(entryH2.MissingFor);
    }

    [Fact]
    public async Task LeaveAsync_removes_leavers_entries_and_strips_them_from_MissingFor()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host", maxPlayers: 4), new[] { "h1", "h2" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h2" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p2", "p2", new[] { "h2" }, 0, default);

        // host queues a song neither p1 nor p2 owns (h1) — both end up in MissingFor.
        var hostEntry = await repo.EnqueueSongAsync("AAAAAAAA", "host", "h1", DateTimeOffset.UtcNow, default);
        // p1 queues their own song (h2).
        var p1Entry = await repo.EnqueueSongAsync("AAAAAAAA", "p1", "h2", DateTimeOffset.UtcNow, default);

        var leave = await repo.LeaveAsync("AAAAAAAA", "p1", default);

        Assert.Equal(LeaveOutcome.Left, leave.Outcome);
        Assert.NotNull(leave.RemovedQueueEntries);
        Assert.Equal(new[] { p1Entry.Entry!.Sequence }, leave.RemovedQueueEntries!);

        Assert.NotNull(leave.QueueAvailabilityUpdates);
        var hostDelta = Assert.Single(leave.QueueAvailabilityUpdates!);
        Assert.Equal(hostEntry.Entry!.Sequence, hostDelta.Sequence);
        Assert.Empty(hostDelta.AddedMissing);
        Assert.Equal(new[] { "p1" }, hostDelta.RemovedMissing);

        var queue = await repo.GetQueueAsync("AAAAAAAA", default);
        var remaining = Assert.Single(queue);
        Assert.Equal(hostEntry.Entry.Sequence, remaining.Sequence);
        Assert.Equal(new[] { "p2" }, remaining.MissingFor);
    }

    [Fact]
    public async Task GetQueueAsync_returns_empty_for_unknown_lobby()
    {
        var repo = NewRepo();
        var queue = await repo.GetQueueAsync("ZZZZZZZZ", default);
        Assert.Empty(queue);
    }

    // --- Host transfer tests ---

    [Fact]
    public async Task TransferHostAsync_swaps_host_and_returns_change()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host") with { HostName = "Host" }, EmptyLib, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "Player One", EmptyLib, 0, default);

        var result = await repo.TransferHostAsync("AAAAAAAA", "host", "p1", default);

        Assert.Equal(TransferHostOutcome.Transferred, result.Outcome);
        Assert.NotNull(result.Change);
        Assert.Equal("p1", result.Change!.NewHostUserId);
        Assert.Equal("Player One", result.Change.NewHostName);
        Assert.Equal("p1", result.Lobby!.HostUserId);
        Assert.Equal("Player One", result.Lobby.HostName);
    }

    [Fact]
    public async Task TransferHostAsync_rejects_non_host_caller()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), EmptyLib, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", EmptyLib, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p2", "p2", EmptyLib, 0, default);

        var result = await repo.TransferHostAsync("AAAAAAAA", "p1", "p2", default);

        Assert.Equal(TransferHostOutcome.NotHost, result.Outcome);
        Assert.Null(result.Change);
    }

    [Fact]
    public async Task TransferHostAsync_rejects_target_not_member()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), EmptyLib, 0, default);

        var result = await repo.TransferHostAsync("AAAAAAAA", "host", "stranger", default);

        Assert.Equal(TransferHostOutcome.TargetNotMember, result.Outcome);
    }

    [Fact]
    public async Task TransferHostAsync_rejects_target_is_host()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), EmptyLib, 0, default);

        var result = await repo.TransferHostAsync("AAAAAAAA", "host", "host", default);

        Assert.Equal(TransferHostOutcome.TargetIsHost, result.Outcome);
    }

    [Fact]
    public async Task TransferHostAsync_returns_NotFound_for_unknown_lobby()
    {
        var repo = NewRepo();
        var result = await repo.TransferHostAsync("ZZZZZZZZ", "host", "p1", default);
        Assert.Equal(TransferHostOutcome.NotFound, result.Outcome);
    }

    // --- Kick tests ---

    [Fact]
    public async Task KickPlayerAsync_removes_member_and_recomputes_intersection()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1", "h2", "h3" }, 0, default);
        // p1 lacks h3 — shared library collapses to {h1, h2} after they join.
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h1", "h2" }, 0, default);

        var result = await repo.KickPlayerAsync("AAAAAAAA", "host", "p1", default);

        Assert.Equal(KickOutcome.Kicked, result.Outcome);
        Assert.Equal(1, result.Lobby!.PlayerCount);
        Assert.Equal(3, result.Lobby.SharedSongCount);
        Assert.NotNull(result.Delta);
        Assert.Equal(new[] { "h3" }, result.Delta!.Added);
        Assert.Empty(result.Delta.Removed);

        var members = await repo.GetMembersAsync("AAAAAAAA", default);
        Assert.Single(members);
        Assert.DoesNotContain(members, m => m.UserId == "p1");
    }

    [Fact]
    public async Task KickPlayerAsync_removes_kicked_users_queue_entries()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h1" }, 0, default);
        var queued = await repo.EnqueueSongAsync("AAAAAAAA", "p1", "h1", DateTimeOffset.UtcNow, default);

        var result = await repo.KickPlayerAsync("AAAAAAAA", "host", "p1", default);

        Assert.Equal(KickOutcome.Kicked, result.Outcome);
        Assert.NotNull(result.RemovedQueueEntries);
        Assert.Equal(new[] { queued.Entry!.Sequence }, result.RemovedQueueEntries!);
        Assert.Empty(await repo.GetQueueAsync("AAAAAAAA", default));
    }

    [Fact]
    public async Task KickPlayerAsync_rejects_non_host_caller()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), EmptyLib, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", EmptyLib, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p2", "p2", EmptyLib, 0, default);

        var result = await repo.KickPlayerAsync("AAAAAAAA", "p1", "p2", default);

        Assert.Equal(KickOutcome.NotHost, result.Outcome);
    }

    [Fact]
    public async Task KickPlayerAsync_rejects_target_is_host()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), EmptyLib, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", EmptyLib, 0, default);

        var result = await repo.KickPlayerAsync("AAAAAAAA", "host", "host", default);

        Assert.Equal(KickOutcome.TargetIsHost, result.Outcome);
    }

    [Fact]
    public async Task KickPlayerAsync_rejects_target_not_member()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), EmptyLib, 0, default);

        var result = await repo.KickPlayerAsync("AAAAAAAA", "host", "stranger", default);

        Assert.Equal(KickOutcome.TargetNotMember, result.Outcome);
    }

    [Fact]
    public async Task KickPlayerAsync_returns_NotFound_for_unknown_lobby()
    {
        var repo = NewRepo();
        var result = await repo.KickPlayerAsync("ZZZZZZZZ", "host", "p1", default);
        Assert.Equal(KickOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task JoinAsync_returns_Banned_after_kick()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), EmptyLib, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", EmptyLib, 0, default);
        await repo.KickPlayerAsync("AAAAAAAA", "host", "p1", default);

        var rejoin = await repo.JoinAsync("AAAAAAAA", "p1", "p1", EmptyLib, 0, default);

        Assert.Equal(JoinResult.Banned, rejoin.Result);
        Assert.Null(rejoin.Lobby);
    }

    [Fact]
    public async Task FinishGameAsync_pops_head_of_queue_and_clears_song_runtime_fields()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1", "h2" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h1", "h2" }, 0, default);

        // Queue two songs in order.
        var first = await repo.EnqueueSongAsync("AAAAAAAA", "host", "h1", DateTimeOffset.UtcNow, default);
        var second = await repo.EnqueueSongAsync("AAAAAAAA", "host", "h2", DateTimeOffset.UtcNow, default);

        await StartGameAsync(repo, "AAAAAAAA", "host");
        await repo.SongStartedAsync("AAAAAAAA", DateTimeOffset.UtcNow, durationMs: 215_000, default);

        var finish = await repo.FinishGameAsync("AAAAAAAA", default);

        Assert.Equal(FinishGameOutcome.Finished, finish.Outcome);
        Assert.NotNull(finish.PlayedSong);
        Assert.Equal(first.Entry!.Sequence, finish.PlayedSong!.Sequence);
        Assert.Equal("h1", finish.PlayedSong.SongHash);
        Assert.Null(finish.Lobby!.SongStartedAt);
        Assert.Null(finish.Lobby!.SongDurationMs);
        Assert.Equal(LobbyStatus.SongSelect, finish.Lobby!.Status);

        // The second song is now the head and is still queued.
        var queue = await repo.GetQueueAsync("AAAAAAAA", default);
        var remaining = Assert.Single(queue);
        Assert.Equal(second.Entry!.Sequence, remaining.Sequence);
    }

    [Fact]
    public async Task BeginStartGame_then_Abort_preserves_ready_flags_and_allows_retry()
    {
        // An allocation failure rolls the lobby back via AbortStartGameAsync. The per-member
        // IsBackInLobby flags must survive that rollback so the host can retry StartGame —
        // they are only flipped to false on the GameStarted transition (ConfirmStartGameAsync).
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h1" }, 0, default);
        await repo.EnqueueSongAsync("AAAAAAAA", "host", "h1", DateTimeOffset.UtcNow, default);

        // First attempt reaches Starting, then the allocator "fails" and we abort.
        var firstBegin = await repo.BeginStartGameAsync("AAAAAAAA", "host", default);
        Assert.Equal(StartGameOutcome.Started, firstBegin.Outcome);
        await repo.AbortStartGameAsync("AAAAAAAA", default);

        // Retry: the readiness gate must still pass rather than failing with
        // PlayersStillInResults because of stale IsBackInLobby flags.
        var retryBegin = await repo.BeginStartGameAsync("AAAAAAAA", "host", default);
        Assert.Equal(StartGameOutcome.Started, retryBegin.Outcome);
    }

    [Fact]
    public async Task FinishGameAsync_with_empty_queue_returns_null_PlayedSong()
    {
        // Defensive: a member-departure cascade can drain the queue during gameplay. FinishGame
        // should still flip status back to SongSelect without panicking, just without a removal
        // broadcast.
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h1" }, 0, default);
        await repo.EnqueueSongAsync("AAAAAAAA", "host", "h1", DateTimeOffset.UtcNow, default);
        await StartGameAsync(repo, "AAAAAAAA", "host");

        // Simulate the queue having been drained mid-game (e.g. requester left and their entry
        // was purged by RemoveMemberLocked) by leaving the only-other-member.
        await repo.LeaveAsync("AAAAAAAA", "host", default); // host leaves -> handoff, queue intact
        // Now p1 (new host) finishes. Need to first remove the entry to simulate empty queue.
        // Easier path: just put the lobby back into GameStarted via a fresh setup.

        var repo2 = NewRepo();
        await repo2.CreateAsync(NewLobby(hostId: "host"), new[] { "h1" }, 0, default);
        await repo2.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h1" }, 0, default);
        var added = await repo2.EnqueueSongAsync("AAAAAAAA", "host", "h1", DateTimeOffset.UtcNow, default);
        await StartGameAsync(repo2, "AAAAAAAA", "host");
        // Drain the queue manually via a host removal.
        await repo2.RemoveQueuedSongAsync("AAAAAAAA", "host", added.Entry!.Sequence, default);

        var finish = await repo2.FinishGameAsync("AAAAAAAA", default);
        Assert.Equal(FinishGameOutcome.Finished, finish.Outcome);
        Assert.Null(finish.PlayedSong);
        Assert.Equal(LobbyStatus.SongSelect, finish.Lobby!.Status);
    }

    [Fact]
    public async Task SongStartedAsync_sets_started_at_and_duration_when_GameStarted()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1" }, 0, default);
        await repo.JoinAsync("AAAAAAAA", "p1", "p1", new[] { "h1" }, 0, default);
        await repo.EnqueueSongAsync("AAAAAAAA", "host", "h1", DateTimeOffset.UtcNow, default);
        await StartGameAsync(repo, "AAAAAAAA", "host");

        var startedAt = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);
        var result = await repo.SongStartedAsync("AAAAAAAA", startedAt, durationMs: 180_000, default);

        Assert.Equal(SongStartedOutcome.Set, result.Outcome);
        Assert.Equal(startedAt, result.Lobby!.SongStartedAt);
        Assert.Equal(180_000, result.Lobby!.SongDurationMs);
    }

    [Fact]
    public async Task SongStartedAsync_returns_NotStarted_when_lobby_in_song_select()
    {
        var repo = NewRepo();
        await repo.CreateAsync(NewLobby(hostId: "host"), new[] { "h1" }, 0, default);

        var result = await repo.SongStartedAsync("AAAAAAAA", DateTimeOffset.UtcNow, durationMs: 1000, default);

        Assert.Equal(SongStartedOutcome.NotStarted, result.Outcome);
        Assert.Null(result.Lobby);
    }

    [Fact]
    public async Task SongStartedAsync_returns_NotFound_for_unknown_lobby()
    {
        var repo = NewRepo();

        var result = await repo.SongStartedAsync("ZZZZZZZZ", DateTimeOffset.UtcNow, durationMs: 1000, default);

        Assert.Equal(SongStartedOutcome.NotFound, result.Outcome);
        Assert.Null(result.Lobby);
    }
}
