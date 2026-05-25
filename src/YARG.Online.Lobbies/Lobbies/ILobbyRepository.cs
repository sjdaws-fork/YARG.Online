using YARG.Online.Lobbies.Allocation;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Domain;

namespace YARG.Online.Lobbies.Lobbies;

public enum JoinResult
{
    Joined,
    AlreadyMember,
    NotFound,
    Full,
    Banned,
}

public enum LeaveOutcome
{
    NotFound,
    Left,
    LobbyClosed,
}

public sealed record SongLibraryDelta(IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

public sealed record QueueAvailabilityDelta(long Sequence, IReadOnlyList<string> AddedMissing, IReadOnlyList<string> RemovedMissing);

/// <summary>
/// Returned when a leave or kick causes the host role to move to another member.
/// Non-null only when the lobby survives (i.e. at least one member remains).
/// </summary>
public sealed record HostChange(string NewHostUserId, string NewHostName);

public sealed record JoinResultData(
    JoinResult Result,
    Lobby? Lobby,
    SongLibraryDelta? Delta,
    IReadOnlyCollection<string>? LobbySongLibrarySnapshot,
    IReadOnlyList<QueueAvailabilityDelta>? QueueAvailabilityUpdates);

public sealed record LeaveResult(
    LeaveOutcome Outcome,
    Lobby? Lobby,
    SongLibraryDelta? Delta,
    IReadOnlyList<long>? RemovedQueueEntries,
    IReadOnlyList<QueueAvailabilityDelta>? QueueAvailabilityUpdates,
    HostChange? HostChange);

public enum EnqueueOutcome
{
    Added,
    NotFound,
    NotMember,
    NotInLibrary,
    QueueFull,
}

public sealed record EnqueueResult(EnqueueOutcome Outcome, QueuedSong? Entry);

public enum RemoveQueuedSongOutcome
{
    Removed,
    NotFound,
    NotMember,
    EntryMissing,
    NotPermitted,
}

public sealed record RemoveQueuedSongResult(RemoveQueuedSongOutcome Outcome, QueuedSong? Entry);

public enum TransferHostOutcome
{
    Transferred,
    NotFound,
    NotHost,
    TargetNotMember,
    TargetIsHost,
}

public sealed record TransferHostResult(TransferHostOutcome Outcome, Lobby? Lobby, HostChange? Change);

public enum KickOutcome
{
    Kicked,
    NotFound,
    NotHost,
    TargetNotMember,
    TargetIsHost,
}

public sealed record KickResult(
    KickOutcome Outcome,
    Lobby? Lobby,
    SongLibraryDelta? Delta,
    IReadOnlyList<long>? RemovedQueueEntries,
    IReadOnlyList<QueueAvailabilityDelta>? QueueAvailabilityUpdates);

public enum StartGameOutcome
{
    Started,
    NotFound,
    NotHost,
    AlreadyStarting,
    AlreadyStarted,
    NotEnoughPlayers,
    QueueEmpty,
    /// <summary>One or more members are still viewing the post-game results
    /// screen (or haven't reported back to the lobby yet). The host must
    /// wait until every member's <c>IsBackInLobby</c> flag is true.</summary>
    PlayersStillInResults,
}

public sealed record StartGameMember(string UserId, string DisplayName);

public sealed record BeginStartGameResultData(
    StartGameOutcome Outcome,
    Lobby? Lobby,
    int MemberCount);

public enum ConfirmStartGameOutcome
{
    Started,
    NotFound,
    NotStarting,
}

public sealed record ConfirmStartGameResultData(
    ConfirmStartGameOutcome Outcome,
    Lobby? Lobby,
    IReadOnlyList<StartGameMember>? Members);

public enum FinishGameOutcome
{
    Finished,
    NotFound,
    NotStarted,
}

public sealed record FinishGameResultData(
    FinishGameOutcome Outcome,
    Lobby? Lobby,
    QueuedSong? PlayedSong,
    GameAllocation? Allocation);

public enum SongStartedOutcome
{
    Set,
    NotFound,
    NotStarted,
}

public sealed record SongStartedResultData(SongStartedOutcome Outcome, Lobby? Lobby);

public enum LeaveResultsOutcome
{
    /// <summary>Flag flipped to true; broadcast the event.</summary>
    MarkedBackInLobby,
    /// <summary>Flag flipped to true AND this was the last member to come
    /// back, so the lobby has been transitioned out of <see cref="LobbyStatus.GameStarted"/>
    /// back to <see cref="LobbyStatus.SongSelect"/>. Caller must broadcast
    /// both <c>OnPlayerLobbyReadyChanged</c> and <c>OnLobbyStatusChanged</c>.
    /// Used when every player bails early (via the in-game Quit button)
    /// before <c>FinishGameAsync</c> would have transitioned the lobby on
    /// its own — without this path, the lobby would stay stuck in
    /// GameStarted and the host's Start button would say "song already
    /// active" for the next song.</summary>
    MarkedBackInLobbyAndGameEnded,
    /// <summary>Already true — caller was never away. Treated as a no-op; no broadcast needed.</summary>
    AlreadyBackInLobby,
    NotFound,
    NotMember,
}

public sealed record LeaveResultsResultData(LeaveResultsOutcome Outcome, Lobby? Lobby, QueuedSong? AbandonedSong);

public enum UpdateLibraryOutcome
{
    /// <summary>Library replaced and intersection recomputed. <see cref="UpdateLibraryResultData.Delta"/>
    /// contains the diff against the previous shared set (may be empty if the recompute happened to
    /// land on the same intersection). Caller should broadcast <c>OnLobbySongLibraryUpdated</c>
    /// and any <c>OnQueuedSongAvailabilityChanged</c> deltas.</summary>
    Applied,
    NotFound,
    NotMember,
}

public sealed record UpdateLibraryResultData(
    UpdateLibraryOutcome Outcome,
    Lobby? Lobby,
    SongLibraryDelta? Delta,
    IReadOnlyList<QueueAvailabilityDelta>? QueueAvailabilityUpdates);

public interface ILobbyRepository
{
    /// <summary>
    /// Persist a new lobby with a server-assigned ID and the host's initial song library.
    /// The repository owns ID generation and uniqueness: it calls <paramref name="lobbyFactory"/>
    /// with a freshly-minted ID (via the injected <see cref="ILobbyIdGenerator"/>) and persists
    /// the returned <see cref="Lobby"/>, retrying on collision up to
    /// <see cref="LobbyOptions.IdGenerationAttempts"/> times. Returns the persisted lobby
    /// (whose <c>Id</c> reflects the assigned ID) or null if every attempt collided.
    ///
    /// Locating the retry loop here keeps the in-memory implementation's TryAdd-style probing
    /// and a future Redis-backed implementation's atomic SETNX primitive behind the same
    /// abstraction — the hub doesn't need to know which storage is in play, just that the
    /// repository will either hand back a unique ID or report exhaustion.
    /// </summary>
    /// <param name="lobbyFactory">Builds the lobby record given the generated ID. Invoked
    /// once per attempt; implementations must not assume a single invocation.</param>
    /// <param name="hostInstrument">Host's selected instrument as a YARG.Core.Instrument byte; echoed back via LobbyMemberDto.</param>
    Task<Lobby?> CreateAsync(
        Func<string, Lobby> lobbyFactory,
        IReadOnlyCollection<string> hostLibrary,
        byte hostInstrument,
        CancellationToken ct);

    Task<Lobby?> GetAsync(string lobbyId, CancellationToken ct);

    /// <param name="instrument">Joiner's selected instrument as a YARG.Core.Instrument byte; echoed back via LobbyMemberDto and PlayerJoinedEvent.</param>
    Task<JoinResultData> JoinAsync(string lobbyId, string userId, string displayName, IReadOnlyCollection<string> library, byte instrument, CancellationToken ct);

    Task<LeaveResult> LeaveAsync(string lobbyId, string userId, CancellationToken ct);

    Task<bool> IsMemberAsync(string lobbyId, string userId, CancellationToken ct);

    Task<IReadOnlyList<LobbyMemberDto>> GetMembersAsync(string lobbyId, CancellationToken ct);

    Task<LobbySearchResult> SearchAsync(LobbySearchQuery query, CancellationToken ct);

    /// <summary>
    /// Appends a chat message to the lobby's bounded history. Returns the stored message
    /// (with server-assigned <see cref="ChatMessage.Sequence"/>) or null if the lobby
    /// is missing or the sender is not a member.
    /// </summary>
    Task<ChatMessage?> AppendChatMessageAsync(
        string lobbyId,
        string userId,
        string displayName,
        string text,
        DateTimeOffset sentAt,
        CancellationToken ct);

    Task<IReadOnlyList<ChatMessage>> GetChatHistoryAsync(string lobbyId, CancellationToken ct);

    /// <summary>
    /// Append a song to the lobby's queue on behalf of <paramref name="userId"/>. The requester
    /// must be a lobby member and must own the song; their ID is therefore never present in the
    /// returned entry's <see cref="QueuedSong.MissingFor"/>.
    /// </summary>
    Task<EnqueueResult> EnqueueSongAsync(
        string lobbyId,
        string userId,
        string songHash,
        DateTimeOffset now,
        CancellationToken ct);

    /// <summary>
    /// Remove a queued song. Allowed only when the caller is the lobby host or the original requester.
    /// </summary>
    Task<RemoveQueuedSongResult> RemoveQueuedSongAsync(
        string lobbyId,
        string userId,
        long sequence,
        CancellationToken ct);

    /// <summary>Snapshot of the lobby's current song queue, ordered by insertion.</summary>
    Task<IReadOnlyList<QueuedSong>> GetQueueAsync(string lobbyId, CancellationToken ct);

    /// <summary>
    /// Transfer the host role from <paramref name="callerUserId"/> to <paramref name="targetUserId"/>.
    /// Both must be current members; the caller must be the current host.
    /// </summary>
    Task<TransferHostResult> TransferHostAsync(string lobbyId, string callerUserId, string targetUserId, CancellationToken ct);

    /// <summary>
    /// Remove <paramref name="targetUserId"/> from the lobby on behalf of the host. Same side-effects
    /// as a non-host leave (library re-intersection, queue cleanup), plus the target is added to the
    /// lobby's banned set for the lobby's remaining lifetime.
    /// </summary>
    Task<KickResult> KickPlayerAsync(string lobbyId, string callerUserId, string targetUserId, CancellationToken ct);

    /// <summary>
    /// Replace the caller's <c>PlayerLibraries</c> entry with <paramref name="library"/> and recompute
    /// the lobby's shared-library intersection across all remaining members. The returned delta is
    /// the difference between the old shared set and the new one; queue availability deltas cover
    /// queued songs the caller is now (or no longer) missing relative to the previous state.
    /// </summary>
    Task<UpdateLibraryResultData> UpdatePlayerLibraryAsync(
        string lobbyId,
        string userId,
        IReadOnlyCollection<string> library,
        CancellationToken ct);

    /// <summary>
    /// Transition the lobby to <see cref="LobbyStatus.Starting"/>. Allowed only when the caller is the
    /// current host, the lobby is currently in <see cref="LobbyStatus.SongSelect"/>, there are at least
    /// two members, and the song queue is non-empty. On success the returned <see cref="BeginStartGameResultData.MemberCount"/>
    /// gives the current member count so the caller can size the game-server allocation. The authoritative
    /// member list (for minting per-user tokens) is captured later by <see cref="ConfirmStartGameAsync"/>,
    /// since membership can shift while the allocator runs. Caller must follow this with either
    /// <see cref="ConfirmStartGameAsync"/> (on allocation success) or <see cref="AbortStartGameAsync"/>
    /// (on allocation failure).
    /// </summary>
    Task<BeginStartGameResultData> BeginStartGameAsync(string lobbyId, string callerUserId, CancellationToken ct);

    /// <summary>
    /// Transition the lobby from <see cref="LobbyStatus.Starting"/> to <see cref="LobbyStatus.GameStarted"/>
    /// and store the resolved <paramref name="allocation"/> on the lobby so <see cref="FinishGameAsync"/>
    /// can surface it for slot release. On success the returned <see cref="ConfirmStartGameResultData.Members"/>
    /// snapshots every current member (with display name) — captured atomically with the GameStarted
    /// transition — so the caller can mint per-user tokens and bake a consistent quorum count into them.
    /// </summary>
    Task<ConfirmStartGameResultData> ConfirmStartGameAsync(string lobbyId, GameAllocation allocation, CancellationToken ct);

    /// <summary>
    /// Roll the lobby back from <see cref="LobbyStatus.Starting"/> to <see cref="LobbyStatus.SongSelect"/>
    /// after a failed allocation. Idempotent: a lobby that is already in <see cref="LobbyStatus.SongSelect"/>
    /// (or has been deleted) is a no-op.
    /// </summary>
    Task AbortStartGameAsync(string lobbyId, CancellationToken ct);

    /// <summary>
    /// Transition the lobby back to <see cref="LobbyStatus.SongSelect"/>. Used by the
    /// game-finished REST callback. Only valid when the lobby is currently <see cref="LobbyStatus.GameStarted"/>.
    /// Pops the head of the song queue (the song that was being played) and clears the
    /// lobby's <see cref="Lobby.SongStartedAt"/>/<see cref="Lobby.SongDurationMs"/> runtime fields.
    /// The popped entry is surfaced as <see cref="FinishGameResultData.PlayedSong"/> so the caller
    /// can broadcast the removal with <see cref="SongRemovalReason.Played"/>. The lobby's stored
    /// <see cref="GameAllocation"/> is returned as <see cref="FinishGameResultData.Allocation"/> and
    /// cleared from the entry so the caller can release slots via <see cref="IGameEndedHandler"/>.
    /// </summary>
    Task<FinishGameResultData> FinishGameAsync(string lobbyId, CancellationToken ct);

    /// <summary>
    /// Record the wall-clock instant and chart duration at which the in-progress song actually
    /// began playing. Called by the game server once it has broadcast <c>GameStartCue</c> to all
    /// peers. Only valid while the lobby is in <see cref="LobbyStatus.GameStarted"/>; returns
    /// <see cref="SongStartedOutcome.NotStarted"/> if the game already finished (e.g. a
    /// member-departure cascade ended the session before the cue post arrived).
    /// </summary>
    Task<SongStartedResultData> SongStartedAsync(
        string lobbyId,
        DateTimeOffset startedAt,
        int durationMs,
        CancellationToken ct);

    /// <summary>
    /// Mark <paramref name="userId"/> as back in the lobby (the player has
    /// closed the post-game results screen or otherwise returned to the
    /// song-select view). The host's StartGame is gated on every member
    /// being back. Idempotent — repeated calls return
    /// <see cref="LeaveResultsOutcome.AlreadyBackInLobby"/>.
    /// </summary>
    Task<LeaveResultsResultData> LeaveResultsAsync(string lobbyId, string userId, CancellationToken ct);
}
