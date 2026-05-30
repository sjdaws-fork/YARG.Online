using AutoMapper;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using YARG.Online.Lobbies.Allocation;
using YARG.Online.Lobbies.Auth;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Online.Lobbies.Domain;
using YARG.Online.Lobbies.Endpoints;
using YARG.Online.Lobbies.Lobbies;
using YARG.Online.Lobbies.Validation;

namespace YARG.Online.Lobbies.Hubs;

[Authorize]
public sealed class LobbyHub : Hub<ILobbyHubClient>, ILobbyHub
{
    public const string BrowseGroup = "browse";
    public static string LobbyGroup(string lobbyId) => $"lobby:{lobbyId}";

    private readonly ILobbyRepository _repo;
    private readonly ILobbyChangeBuffer _buffer;
    private readonly IConnectionTracker _connections;
    private readonly IValidator<CreateLobbyArgs> _validator;
    private readonly IValidator<EnterLobbyArgs> _enterValidator;
    private readonly IValidator<SendChatMessageArgs> _chatValidator;
    private readonly IValidator<QueueSongArgs> _queueValidator;
    private readonly IValidator<TransferHostArgs> _transferHostValidator;
    private readonly IValidator<KickPlayerArgs> _kickPlayerValidator;
    private readonly IGameJwtTokenService _gameJwt;
    private readonly IMapper _mapper;
    private readonly IGameAllocator _allocator;
    private readonly IGameEndedHandler _gameEndedHandler;
    private readonly TimeProvider _clock;
    private readonly ILogger<LobbyHub> _logger;

    public LobbyHub(
        ILobbyRepository repo,
        ILobbyChangeBuffer buffer,
        IConnectionTracker connections,
        IValidator<CreateLobbyArgs> validator,
        IValidator<EnterLobbyArgs> enterValidator,
        IValidator<SendChatMessageArgs> chatValidator,
        IValidator<QueueSongArgs> queueValidator,
        IValidator<TransferHostArgs> transferHostValidator,
        IValidator<KickPlayerArgs> kickPlayerValidator,
        IGameJwtTokenService gameJwt,
        IMapper mapper,
        IGameAllocator allocator,
        IGameEndedHandler gameEndedHandler,
        TimeProvider clock,
        ILogger<LobbyHub> logger)
    {
        _repo = repo;
        _buffer = buffer;
        _connections = connections;
        _validator = validator;
        _enterValidator = enterValidator;
        _chatValidator = chatValidator;
        _queueValidator = queueValidator;
        _transferHostValidator = transferHostValidator;
        _kickPlayerValidator = kickPlayerValidator;
        _gameJwt = gameJwt;
        _mapper = mapper;
        _allocator = allocator;
        _gameEndedHandler = gameEndedHandler;
        _clock = clock;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var sub = Context.User?.FindFirst("sub")?.Value;
        var clientVersion = Context.User?.FindFirst("client_version")?.Value;
        _logger.LogTrace(
            "OnConnectedAsync: ConnectionId={ConnectionId} Sub={Sub} Name={Name} ClientVersion={ClientVersion}",
            Context.ConnectionId, sub, Context.User?.Identity?.Name, clientVersion);

        // TODO(version-gate): rejection happens in ClientVersionHubFilter on the
        // first hub-method invocation (CreateLobby / EnterLobby etc.). Doing it
        // here in OnConnectedAsync doesn't work -- the client's StartAsync has
        // already returned by the time exceptions or Context.Abort() take effect,
        // so the user just sees a silent disconnect with an empty browser.

        await Groups.AddToGroupAsync(Context.ConnectionId, BrowseGroup, Context.ConnectionAborted);

        var snapshot = await _repo.SearchAsync(
            new LobbySearchQuery(0, int.MaxValue, null, null, null),
            Context.ConnectionAborted);
        var dtos = snapshot.Items.Select(_mapper.Map<LobbyDto>).ToArray();
        await Clients.Caller.OnLobbySnapshot(dtos);

        _logger.LogTrace(
            "OnConnectedAsync sent initial snapshot: ConnectionId={ConnectionId} SnapshotCount={SnapshotCount}",
            Context.ConnectionId, dtos.Length);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var lobbyId = _connections.GetLobby(Context.ConnectionId);
        var userId = _connections.GetUserId(Context.ConnectionId);

        _logger.LogTrace(
            "OnDisconnectedAsync: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} ExceptionMessage={ExceptionMessage}",
            Context.ConnectionId, lobbyId, userId, exception?.Message);

        if (lobbyId is not null && userId is not null)
        {
            // ConnectionAborted is already cancelled at this point; use CancellationToken.None
            // so the cleanup runs to completion.
            var result = await _repo.LeaveAsync(lobbyId, userId, CancellationToken.None);
            _connections.ClearLobby(Context.ConnectionId);

            _logger.LogTrace(
                "OnDisconnectedAsync cleanup outcome: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} Outcome={Outcome}",
                Context.ConnectionId, lobbyId, userId, result.Outcome);

            await BroadcastLeaveAsync(lobbyId, userId, result, othersOnly: true);
        }
        else
        {
            _logger.LogTrace(
                "OnDisconnectedAsync no lobby association: ConnectionId={ConnectionId}",
                Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task<CreateLobbyResult> CreateLobby(CreateLobbyArgs args, IAsyncEnumerable<string[]> library)
    {
        _logger.LogTrace(
            "CreateLobby: ConnectionId={ConnectionId} Name={Name} GameMode={GameMode} Region={Region} MaxPlayers={MaxPlayers}",
            Context.ConnectionId, args.Name, args.GameMode, args.Region, args.MaxPlayers);

        if (_connections.GetLobby(Context.ConnectionId) is not null)
        {
            _logger.LogTrace("CreateLobby rejected: ConnectionId={ConnectionId} Reason=already_in_lobby", Context.ConnectionId);
            throw Hub("already_in_lobby");
        }

        var validation = _validator.Validate(args);
        if (!validation.IsValid)
        {
            var firstError = validation.Errors[0];
            _logger.LogTrace(
                "CreateLobby rejected: ConnectionId={ConnectionId} Reason=validation_failed Property={Property}",
                Context.ConnectionId, firstError.PropertyName);
            throw Hub("validation_failed", firstError.PropertyName);
        }

        var (userId, displayName) = Context.User!.RequireCaller();

        var normalizedLibrary = await DrainLibraryAsync(library, Context.ConnectionAborted);
        _logger.LogTrace(
            "CreateLobby library drained: ConnectionId={ConnectionId} LibrarySize={LibrarySize}",
            Context.ConnectionId, normalizedLibrary.Count);

        // ID generation + collision retries live inside the repository so a future Redis-backed
        // implementation can use a single atomic SETNX per attempt instead of the in-memory
        // implementation's generate-then-TryAdd loop. The factory builds the Lobby once the
        // repo has minted (or re-minted, on collision) an ID.
        var lobby = await _repo.CreateAsync(
            id => new Lobby(
                Id: id,
                Name: args.Name,
                HostUserId: userId,
                HostName: displayName,
                GameMode: args.GameMode,
                Region: args.Region,
                Song: args.Song ?? string.Empty,
                PlayerCount: 1,
                MaxPlayers: args.MaxPlayers,
                CreatedAt: _clock.GetUtcNow(),
                SharedSongCount: normalizedLibrary.Count,
                IsPublic: args.IsPublic),
            normalizedLibrary,
            args.Instrument,
            Context.ConnectionAborted);

        if (lobby is null)
        {
            _logger.LogTrace(
                "CreateLobby exhausted id attempts: ConnectionId={ConnectionId} UserId={UserId}",
                Context.ConnectionId, userId);
            throw Hub("lobby_id_exhausted");
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, BrowseGroup, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyGroup(lobby.Id), Context.ConnectionAborted);
        _connections.SetLobby(Context.ConnectionId, userId, lobby.Id);

        var dto = _mapper.Map<LobbyDto>(lobby);
        // Private lobbies are reachable only by join-by-code: never enqueue them
        // into the browse buffer, so they never surface in OnLobbySnapshot /
        // OnLobbyBatch broadcasts to the browse group.
        if (lobby.IsPublic)
        {
            _buffer.Enqueue(new LobbyChange(lobby.Id, LobbyChangeKind.Added, dto));
        }

        _logger.LogTrace(
            "CreateLobby created: ConnectionId={ConnectionId} UserId={UserId} LobbyId={LobbyId}",
            Context.ConnectionId, userId, lobby.Id);

        return new CreateLobbyResult(dto);
    }

    public async Task<EnterLobbyResult> EnterLobby(EnterLobbyArgs args, IAsyncEnumerable<string[]> library)
    {
        _logger.LogTrace(
            "EnterLobby: ConnectionId={ConnectionId} LobbyId={LobbyId}",
            Context.ConnectionId, args.LobbyId);

        var validation = _enterValidator.Validate(args);
        if (!validation.IsValid)
        {
            var firstError = validation.Errors[0];
            _logger.LogTrace(
                "EnterLobby rejected: ConnectionId={ConnectionId} Reason=validation_failed Property={Property}",
                Context.ConnectionId, firstError.PropertyName);
            throw Hub("validation_failed", firstError.PropertyName);
        }

        var lobbyId = args.LobbyId;

        if (_connections.GetLobby(Context.ConnectionId) is not null)
        {
            _logger.LogTrace("EnterLobby rejected: ConnectionId={ConnectionId} Reason=already_in_lobby", Context.ConnectionId);
            throw Hub("already_in_lobby");
        }

        var (userId, displayName) = Context.User!.RequireCaller();
        var normalizedLibrary = await DrainLibraryAsync(library, Context.ConnectionAborted);
        var joinData = await _repo.JoinAsync(lobbyId, userId, displayName, normalizedLibrary, args.Instrument, Context.ConnectionAborted);

        _logger.LogTrace(
            "EnterLobby join result: ConnectionId={ConnectionId} UserId={UserId} LobbyId={LobbyId} Result={Result}",
            Context.ConnectionId, userId, lobbyId, joinData.Result);

        Lobby joinedLobby;
        bool isNewMember;
        switch (joinData.Result)
        {
            case JoinResult.NotFound:
                throw Hub("lobby_not_found");
            case JoinResult.Full:
                throw Hub("lobby_full");
            case JoinResult.Banned:
                throw Hub("banned_from_lobby");
            case JoinResult.Joined:
                joinedLobby = joinData.Lobby!;
                isNewMember = true;
                break;
            case JoinResult.AlreadyMember:
                joinedLobby = joinData.Lobby!;
                isNewMember = false;
                break;
            default:
                throw new InvalidOperationException($"Unexpected join result {joinData.Result}.");
        }

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, BrowseGroup, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, LobbyGroup(lobbyId), Context.ConnectionAborted);
        _connections.SetLobby(Context.ConnectionId, userId, lobbyId);

        if (isNewMember)
        {
            await Clients.OthersInGroup(LobbyGroup(lobbyId))
                .OnPlayerJoined(new PlayerJoinedEvent(lobbyId, userId, displayName) { Instrument = args.Instrument });
            _buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Updated, _mapper.Map<LobbyDto>(joinedLobby)));

            if (joinData.Delta is { Removed.Count: > 0 } delta)
            {
                await Clients.OthersInGroup(LobbyGroup(lobbyId))
                    .OnLobbySongLibraryUpdated(new LobbySongLibraryUpdatedEvent(
                        lobbyId, Array.Empty<string>(), delta.Removed.ToArray()));
            }

            if (joinData.QueueAvailabilityUpdates is { Count: > 0 } queueUpdates)
            {
                foreach (var qu in queueUpdates)
                {
                    await Clients.OthersInGroup(LobbyGroup(lobbyId))
                        .OnQueuedSongAvailabilityChanged(new QueuedSongAvailabilityChangedEvent(
                            lobbyId,
                            qu.Sequence,
                            qu.AddedMissing.ToArray(),
                            qu.RemovedMissing.ToArray()));
                }
            }
        }

        var members = await _repo.GetMembersAsync(lobbyId, Context.ConnectionAborted);
        var history = await _repo.GetChatHistoryAsync(lobbyId, Context.ConnectionAborted);
        var queue = await _repo.GetQueueAsync(lobbyId, Context.ConnectionAborted);

        // Tell the joiner which of their own songs are NOT in the post-join shared library
        // so they can reconstruct the effective lobby library locally as `myLib ∖ removals`.
        var sharedLookup = joinData.LobbySongLibrarySnapshot is { } snap
            ? snap as HashSet<string> ?? new HashSet<string>(snap, StringComparer.Ordinal)
            : null;
        var removals = new List<string>();
        foreach (var h in normalizedLibrary)
        {
            if (sharedLookup is null || !sharedLookup.Contains(h))
                removals.Add(h);
        }

        _logger.LogTrace(
            "EnterLobby returning: ConnectionId={ConnectionId} UserId={UserId} LobbyId={LobbyId} IsNewMember={IsNewMember} MemberCount={MemberCount} QueueLength={QueueLength} LibraryRemovalCount={LibraryRemovalCount}",
            Context.ConnectionId, userId, lobbyId, isNewMember, members.Count, queue.Count, removals.Count);

        return new EnterLobbyResult(
            _mapper.Map<LobbyDto>(joinedLobby),
            members.ToArray(),
            history.ToArray(),
            removals.ToArray(),
            queue.Select(_mapper.Map<QueuedSongDto>).ToArray());
    }

    public async Task SendChatMessage(SendChatMessageArgs args)
    {
        var lobbyId = _connections.GetLobby(Context.ConnectionId);
        var userId = _connections.GetUserId(Context.ConnectionId);

        _logger.LogTrace(
            "SendChatMessage: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} TextLength={TextLength}",
            Context.ConnectionId, lobbyId, userId, args.Text.Length);

        if (lobbyId is null || userId is null)
        {
            _logger.LogTrace("SendChatMessage rejected: ConnectionId={ConnectionId} Reason=not_in_lobby", Context.ConnectionId);
            throw Hub("not_in_lobby");
        }

        var validation = _chatValidator.Validate(args);
        if (!validation.IsValid)
        {
            var firstError = validation.Errors[0];
            _logger.LogTrace(
                "SendChatMessage rejected: ConnectionId={ConnectionId} Reason=validation_failed Property={Property}",
                Context.ConnectionId, firstError.PropertyName);
            throw Hub("validation_failed", firstError.PropertyName);
        }

        var (_, displayName) = Context.User!.RequireCaller();
        var trimmed = args.Text.Trim();

        var message = await _repo.AppendChatMessageAsync(
            lobbyId, userId, displayName, trimmed, _clock.GetUtcNow(), Context.ConnectionAborted);
        if (message is null)
        {
            _logger.LogTrace(
                "SendChatMessage rejected: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} Reason=not_in_lobby (repo returned null)",
                Context.ConnectionId, lobbyId, userId);
            throw Hub("not_in_lobby");
        }

        await Clients.Group(LobbyGroup(lobbyId)).OnChatMessage(new ChatMessageEvent(lobbyId, message));

        _logger.LogTrace(
            "SendChatMessage broadcast: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} TrimmedLength={TrimmedLength}",
            Context.ConnectionId, lobbyId, userId, trimmed.Length);
    }

    public async Task LeaveLobby()
    {
        var lobbyId = _connections.GetLobby(Context.ConnectionId);
        var userId = _connections.GetUserId(Context.ConnectionId);

        _logger.LogTrace(
            "LeaveLobby: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId}",
            Context.ConnectionId, lobbyId, userId);

        if (lobbyId is null || userId is null)
        {
            _logger.LogTrace("LeaveLobby no-op: ConnectionId={ConnectionId} Reason=not_in_lobby", Context.ConnectionId);
            return;
        }

        var result = await _repo.LeaveAsync(lobbyId, userId, Context.ConnectionAborted);

        _logger.LogTrace(
            "LeaveLobby outcome: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} Outcome={Outcome}",
            Context.ConnectionId, lobbyId, userId, result.Outcome);

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, LobbyGroup(lobbyId), Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, BrowseGroup, Context.ConnectionAborted);
        _connections.ClearLobby(Context.ConnectionId);

        await BroadcastLeaveAsync(lobbyId, userId, result, othersOnly: true);

        // Resync the caller's browse view in case they missed batches while in-lobby.
        await SendBrowseSnapshotToCallerAsync();
    }

    public async Task TransferHost(TransferHostArgs args)
    {
        var lobbyId = _connections.GetLobby(Context.ConnectionId);
        var userId = _connections.GetUserId(Context.ConnectionId);

        _logger.LogTrace(
            "TransferHost: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} TargetUserId={TargetUserId}",
            Context.ConnectionId, lobbyId, userId, args.TargetUserId);

        if (lobbyId is null || userId is null)
        {
            _logger.LogTrace("TransferHost rejected: ConnectionId={ConnectionId} Reason=not_in_lobby", Context.ConnectionId);
            throw Hub("not_in_lobby");
        }

        var validation = _transferHostValidator.Validate(args);
        if (!validation.IsValid)
        {
            var firstError = validation.Errors[0];
            _logger.LogTrace(
                "TransferHost rejected: ConnectionId={ConnectionId} Reason=validation_failed Property={Property}",
                Context.ConnectionId, firstError.PropertyName);
            throw Hub("validation_failed", firstError.PropertyName);
        }

        var result = await _repo.TransferHostAsync(lobbyId, userId, args.TargetUserId, Context.ConnectionAborted);

        _logger.LogTrace(
            "TransferHost outcome: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} TargetUserId={TargetUserId} Outcome={Outcome}",
            Context.ConnectionId, lobbyId, userId, args.TargetUserId, result.Outcome);

        switch (result.Outcome)
        {
            case TransferHostOutcome.Transferred:
                var change = result.Change!;
                await Clients.Group(LobbyGroup(lobbyId))
                    .OnHostChanged(new HostChangedEvent(lobbyId, change.NewHostUserId, change.NewHostName));
                _buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Updated, _mapper.Map<LobbyDto>(result.Lobby!)));
                return;
            case TransferHostOutcome.NotFound:
                throw Hub("not_in_lobby");
            case TransferHostOutcome.NotHost:
                throw Hub("not_host");
            case TransferHostOutcome.TargetIsHost:
                throw Hub("target_is_host");
            case TransferHostOutcome.TargetNotMember:
                throw Hub("target_not_member");
            default:
                throw new InvalidOperationException($"Unexpected transfer host outcome {result.Outcome}.");
        }
    }

    public async Task KickPlayer(KickPlayerArgs args)
    {
        var lobbyId = _connections.GetLobby(Context.ConnectionId);
        var userId = _connections.GetUserId(Context.ConnectionId);

        _logger.LogTrace(
            "KickPlayer: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} TargetUserId={TargetUserId}",
            Context.ConnectionId, lobbyId, userId, args.TargetUserId);

        if (lobbyId is null || userId is null)
        {
            _logger.LogTrace("KickPlayer rejected: ConnectionId={ConnectionId} Reason=not_in_lobby", Context.ConnectionId);
            throw Hub("not_in_lobby");
        }

        var validation = _kickPlayerValidator.Validate(args);
        if (!validation.IsValid)
        {
            var firstError = validation.Errors[0];
            _logger.LogTrace(
                "KickPlayer rejected: ConnectionId={ConnectionId} Reason=validation_failed Property={Property}",
                Context.ConnectionId, firstError.PropertyName);
            throw Hub("validation_failed", firstError.PropertyName);
        }

        var result = await _repo.KickPlayerAsync(lobbyId, userId, args.TargetUserId, Context.ConnectionAborted);

        _logger.LogTrace(
            "KickPlayer outcome: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} TargetUserId={TargetUserId} Outcome={Outcome}",
            Context.ConnectionId, lobbyId, userId, args.TargetUserId, result.Outcome);

        switch (result.Outcome)
        {
            case KickOutcome.Kicked:
                var target = args.TargetUserId;

                // Broadcast OnPlayerKicked while the kicked user is still in the group so they receive it.
                await Clients.Group(LobbyGroup(lobbyId))
                    .OnPlayerKicked(new PlayerKickedEvent(lobbyId, target, "kicked_by_host"));

                // Move the kicked user's connection(s) back to browse scope.
                var targetConnections = _connections.GetConnectionsForUser(target);
                var movedConnectionCount = 0;
                foreach (var connId in targetConnections)
                {
                    await Groups.RemoveFromGroupAsync(connId, LobbyGroup(lobbyId), Context.ConnectionAborted);
                    await Groups.AddToGroupAsync(connId, BrowseGroup, Context.ConnectionAborted);
                    _connections.ClearLobby(connId);
                    await SendBrowseSnapshotToClientAsync(connId);
                    movedConnectionCount++;
                }

                _logger.LogTrace(
                    "KickPlayer moved target connections: LobbyId={LobbyId} TargetUserId={TargetUserId} MovedConnectionCount={MovedConnectionCount}",
                    lobbyId, target, movedConnectionCount);

                // Now broadcast OnPlayerLeft + library/queue side-effects to the remaining members.
                await Clients.Group(LobbyGroup(lobbyId)).OnPlayerLeft(new PlayerLeftEvent(lobbyId, target));
                if (result.Delta is { Added.Count: > 0 } delta)
                {
                    await Clients.Group(LobbyGroup(lobbyId))
                        .OnLobbySongLibraryUpdated(new LobbySongLibraryUpdatedEvent(
                            lobbyId, delta.Added.ToArray(), Array.Empty<string>()));
                }
                await BroadcastQueueSideEffectsAsync(lobbyId, result.RemovedQueueEntries, result.QueueAvailabilityUpdates, othersOnly: false);

                _buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Updated, _mapper.Map<LobbyDto>(result.Lobby!)));
                return;

            case KickOutcome.NotFound:
                throw Hub("not_in_lobby");
            case KickOutcome.NotHost:
                throw Hub("not_host");
            case KickOutcome.TargetIsHost:
                throw Hub("target_is_host");
            case KickOutcome.TargetNotMember:
                throw Hub("target_not_member");
            default:
                throw new InvalidOperationException($"Unexpected kick outcome {result.Outcome}.");
        }
    }

    public async Task StartGame()
    {
        var lobbyId = _connections.GetLobby(Context.ConnectionId);
        var userId = _connections.GetUserId(Context.ConnectionId);

        _logger.LogTrace(
            "StartGame: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId}",
            Context.ConnectionId, lobbyId, userId);

        if (lobbyId is null || userId is null)
        {
            _logger.LogTrace("StartGame rejected: ConnectionId={ConnectionId} Reason=not_in_lobby", Context.ConnectionId);
            throw Hub("not_in_lobby");
        }

        var begin = await _repo.BeginStartGameAsync(lobbyId, userId, Context.ConnectionAborted);

        _logger.LogTrace(
            "StartGame begin outcome: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} Outcome={Outcome}",
            Context.ConnectionId, lobbyId, userId, begin.Outcome);

        switch (begin.Outcome)
        {
            case StartGameOutcome.Started:
                break;
            case StartGameOutcome.NotFound:
                throw Hub("not_in_lobby");
            case StartGameOutcome.NotHost:
                throw Hub("not_host");
            case StartGameOutcome.AlreadyStarting:
                throw Hub("already_starting");
            case StartGameOutcome.AlreadyStarted:
                throw Hub("already_started");
            case StartGameOutcome.NotEnoughPlayers:
                throw Hub("not_enough_players");
            case StartGameOutcome.QueueEmpty:
                throw Hub("queue_empty");
            case StartGameOutcome.PlayersStillInResults:
                throw Hub("players_still_in_results");
            case StartGameOutcome.SongMissingForMembers:
                throw Hub("song_missing_for_members");
            default:
                throw new InvalidOperationException($"Unexpected start outcome {begin.Outcome}.");
        }

        // Broadcast the intermediate Starting state so clients can render a "preparing" UI
        // while we wait on the allocator. On allocation failure we roll back below.
        await Clients.Group(LobbyGroup(lobbyId))
            .OnLobbyStatusChanged(new LobbyStatusChangedEvent(lobbyId, LobbyStatus.Starting));
        _buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Updated, _mapper.Map<LobbyDto>(begin.Lobby!)));

        // Size the allocation off the Begin-time count; the authoritative member list
        // (and the quorum count baked into tokens) is taken from ConfirmStartGameAsync below.
        var slotCount = begin.MemberCount;
        GameAllocation allocation;
        try
        {
            allocation = await _allocator.AllocateAsync(slotCount, Context.ConnectionAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Allocation failed; rolling back StartGame: LobbyId={LobbyId} UserId={UserId} SlotCount={SlotCount}",
                lobbyId, userId, slotCount);

            // Use CancellationToken.None for rollback so we don't leave the lobby stuck in Starting
            // if the original request was cancelled.
            await _repo.AbortStartGameAsync(lobbyId, CancellationToken.None);

            var current = await _repo.GetAsync(lobbyId, CancellationToken.None);
            await Clients.Group(LobbyGroup(lobbyId))
                .OnLobbyStatusChanged(new LobbyStatusChangedEvent(lobbyId, LobbyStatus.SongSelect));
            if (current is not null)
            {
                _buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Updated, _mapper.Map<LobbyDto>(current)));
            }

            throw Hub("allocation_failed");
        }

        var confirm = await _repo.ConfirmStartGameAsync(lobbyId, allocation, Context.ConnectionAborted);
        if (confirm.Outcome != ConfirmStartGameOutcome.Started)
        {
            _logger.LogWarning(
                "StartGame confirm failed; rolling back: LobbyId={LobbyId} Outcome={Outcome}",
                lobbyId, confirm.Outcome);
            await _repo.AbortStartGameAsync(lobbyId, CancellationToken.None);
            throw Hub("start_aborted");
        }

        // Mint a per-member game JWT and deliver it only to that member's connections.
        // Each token's `sub` is the recipient — they must never be cross-broadcast.
        // The member snapshot comes from Confirm so the quorum count baked into every
        // token matches exactly who is in the lobby at the GameStarted transition.
        var members = confirm.Members!;
        var expectedMembers = members.Count;
        var hostUserId = confirm.Lobby!.HostUserId;
        var tokensIssued = 0;
        var dispatches = 0;
        foreach (var member in members)
        {
            var isHost = member.UserId == hostUserId;
            var issued = _gameJwt.IssueGameToken(member.UserId, member.DisplayName, lobbyId, expectedMembers, isHost);
            tokensIssued++;
            foreach (var connId in _connections.GetConnectionsForUser(member.UserId))
            {
                await Clients.Client(connId)
                    .OnGameStarted(new GameStartedEvent(
                        lobbyId,
                        allocation.Endpoint,
                        issued.Token,
                        issued.ExpiresAt));
                dispatches++;
            }
        }

        _logger.LogTrace(
            "StartGame dispatched game tokens: LobbyId={LobbyId} ExpectedMembers={ExpectedMembers} TokensIssued={TokensIssued} Dispatches={Dispatches} Endpoint={Endpoint} GameServer={GameServer}",
            lobbyId, expectedMembers, tokensIssued, dispatches, allocation.Endpoint, allocation.GameServerName);

        await Clients.Group(LobbyGroup(lobbyId))
            .OnLobbyStatusChanged(new LobbyStatusChangedEvent(lobbyId, LobbyStatus.GameStarted));
        _buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Updated, _mapper.Map<LobbyDto>(confirm.Lobby!)));
    }

    public async Task LeaveResults()
    {
        var lobbyId = _connections.GetLobby(Context.ConnectionId);
        var userId = _connections.GetUserId(Context.ConnectionId);

        _logger.LogTrace(
            "LeaveResults: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId}",
            Context.ConnectionId, lobbyId, userId);

        if (lobbyId is null || userId is null)
        {
            // Common case: client invokes LeaveResults defensively from the
            // results screen even when they aren't in a lobby (offline play).
            // Silently no-op rather than throwing.
            return;
        }

        var result = await _repo.LeaveResultsAsync(lobbyId, userId, Context.ConnectionAborted);

        _logger.LogTrace(
            "LeaveResults outcome: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} Outcome={Outcome}",
            Context.ConnectionId, lobbyId, userId, result.Outcome);

        if (result.Outcome == LeaveResultsOutcome.MarkedBackInLobby)
        {
            await Clients.Group(LobbyGroup(lobbyId))
                .OnPlayerLobbyReadyChanged(new PlayerLobbyReadyChangedEvent(
                    lobbyId, userId, IsBackInLobby: true));
        }
        else if (result.Outcome == LeaveResultsOutcome.MarkedBackInLobbyAndGameEnded)
        {
            // Every member is now back in the lobby AND we were mid-song;
            // the repo transitioned the lobby back to SongSelect to mirror
            // FinishGameAsync's broadcast set. Without this path the lobby
            // would stay stuck in GameStarted when everyone bails early via
            // the in-game Quit button, and the host would see "song still
            // active" trying to start the next round.
            await Clients.Group(LobbyGroup(lobbyId))
                .OnPlayerLobbyReadyChanged(new PlayerLobbyReadyChangedEvent(
                    lobbyId, userId, IsBackInLobby: true));
            await Clients.Group(LobbyGroup(lobbyId))
                .OnLobbyStatusChanged(new LobbyStatusChangedEvent(lobbyId, LobbyStatus.SongSelect));
            if (result.AbandonedSong is { } abandoned)
            {
                await Clients.Group(LobbyGroup(lobbyId))
                    .OnSongRemovedFromQueue(new SongRemovedFromQueueEvent(
                        lobbyId, abandoned.Sequence, SongRemovalReason.Played));
            }
            if (result.Lobby is { } updated)
            {
                _buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Updated, _mapper.Map<LobbyDto>(updated)));
            }
            if (result.ReleasedAllocation is { } allocation)
            {
                await ReleaseAllocationBestEffortAsync(lobbyId, allocation);
            }
        }
        // Other outcomes are no-ops or harmless duplicates — no broadcast.
    }

    public async Task UpdateLibrary(IAsyncEnumerable<string[]> library)
    {
        var lobbyId = _connections.GetLobby(Context.ConnectionId);
        var userId = _connections.GetUserId(Context.ConnectionId);

        if (lobbyId is null || userId is null)
        {
            // Caller invoked from outside an active lobby — silently no-op like
            // LeaveResults' defensive path. The client guards on CurrentLobby
            // before calling, but a race with LeaveLobby could land us here.
            return;
        }

        var normalized = await DrainLibraryAsync(library, Context.ConnectionAborted);
        var result = await _repo.UpdatePlayerLibraryAsync(lobbyId, userId, normalized, Context.ConnectionAborted);

        _logger.LogTrace(
            "UpdateLibrary outcome: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} Outcome={Outcome}",
            Context.ConnectionId, lobbyId, userId, result.Outcome);

        if (result.Outcome != UpdateLibraryOutcome.Applied) return;

        // Broadcast the shared-library diff to the whole group (caller included —
        // the caller needs the delta to reconcile its local LobbySongLibrary
        // mirror with what the server now considers the shared set).
        if (result.Delta is { } delta && (delta.Added.Count > 0 || delta.Removed.Count > 0))
        {
            await Clients.Group(LobbyGroup(lobbyId))
                .OnLobbySongLibraryUpdated(new LobbySongLibraryUpdatedEvent(
                    lobbyId, delta.Added.ToArray(), delta.Removed.ToArray()));
        }

        if (result.QueueAvailabilityUpdates is { Count: > 0 } queueUpdates)
        {
            foreach (var qu in queueUpdates)
            {
                await Clients.Group(LobbyGroup(lobbyId))
                    .OnQueuedSongAvailabilityChanged(new QueuedSongAvailabilityChangedEvent(
                        lobbyId,
                        qu.Sequence,
                        qu.AddedMissing.ToArray(),
                        qu.RemovedMissing.ToArray()));
            }
        }

        // SharedSongCount changed → browser-view consumers want the refreshed snapshot.
        if (result.Lobby is { } updated)
        {
            _buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Updated, _mapper.Map<LobbyDto>(updated)));
        }
    }

    /// <summary>
    /// Broadcast the effects of a leave (explicit LeaveLobby, disconnect): OnPlayerLeft, optional
    /// OnHostChanged on auto-transfer, library deltas, and queue cleanup. When the lobby closes
    /// (last member left), emits a removal change to the browse buffer.
    /// </summary>
    private async Task BroadcastLeaveAsync(string lobbyId, string userId, LeaveResult result, bool othersOnly)
    {
        _logger.LogTrace(
            "BroadcastLeaveAsync: LobbyId={LobbyId} UserId={UserId} Outcome={Outcome} OthersOnly={OthersOnly}",
            lobbyId, userId, result.Outcome, othersOnly);

        var target = othersOnly
            ? Clients.OthersInGroup(LobbyGroup(lobbyId))
            : Clients.Group(LobbyGroup(lobbyId));

        switch (result.Outcome)
        {
            case LeaveOutcome.Left:
                await target.OnPlayerLeft(new PlayerLeftEvent(lobbyId, userId));
                if (result.HostChange is { } host)
                {
                    _logger.LogTrace(
                        "BroadcastLeaveAsync host auto-transfer: LobbyId={LobbyId} NewHostUserId={NewHostUserId}",
                        lobbyId, host.NewHostUserId);
                    await target.OnHostChanged(new HostChangedEvent(lobbyId, host.NewHostUserId, host.NewHostName));
                }
                if (result.Lobby is { } left)
                {
                    _buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Updated, _mapper.Map<LobbyDto>(left)));
                }
                if (result.Delta is { Added.Count: > 0 } delta)
                {
                    await target.OnLobbySongLibraryUpdated(new LobbySongLibraryUpdatedEvent(
                        lobbyId, delta.Added.ToArray(), Array.Empty<string>()));
                }
                await BroadcastQueueSideEffectsAsync(lobbyId, result.RemovedQueueEntries, result.QueueAvailabilityUpdates, othersOnly);
                break;

            case LeaveOutcome.LobbyClosed:
                _logger.LogTrace("BroadcastLeaveAsync lobby closed: LobbyId={LobbyId}", lobbyId);
                _buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Removed, null));
                if (result.ReleasedAllocation is { } abandonedAllocation)
                {
                    await ReleaseAllocationBestEffortAsync(lobbyId, abandonedAllocation);
                }
                break;
        }
    }

    private async Task ReleaseAllocationBestEffortAsync(string lobbyId, GameAllocation allocation)
    {
        // Cleanup path — use CancellationToken.None so the slot release runs to
        // completion even when the triggering connection has already aborted.
        // Mirrors the best-effort pattern in GameLifecycleEndpoints.FinishGame.
        try
        {
            await _gameEndedHandler.OnGameEndedAsync(
                new GameEndedContext(lobbyId, allocation.GameServerName, allocation.SlotCount),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "IGameEndedHandler failed for LobbyId={LobbyId} GameServer={GameServer}",
                lobbyId, allocation.GameServerName);
        }
    }

    private async Task SendBrowseSnapshotToCallerAsync()
    {
        var snapshot = await _repo.SearchAsync(
            new LobbySearchQuery(0, int.MaxValue, null, null, null),
            Context.ConnectionAborted);
        var dtos = snapshot.Items.Select(_mapper.Map<LobbyDto>).ToArray();
        await Clients.Caller.OnLobbySnapshot(dtos);
    }

    private async Task SendBrowseSnapshotToClientAsync(string connectionId)
    {
        var snapshot = await _repo.SearchAsync(
            new LobbySearchQuery(0, int.MaxValue, null, null, null),
            Context.ConnectionAborted);
        var dtos = snapshot.Items.Select(_mapper.Map<LobbyDto>).ToArray();
        await Clients.Client(connectionId).OnLobbySnapshot(dtos);
    }

    public async Task<QueuedSongDto> QueueSong(QueueSongArgs args)
    {
        var lobbyId = _connections.GetLobby(Context.ConnectionId);
        var userId = _connections.GetUserId(Context.ConnectionId);

        _logger.LogTrace(
            "QueueSong: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} SongHash={SongHash}",
            Context.ConnectionId, lobbyId, userId, args.SongHash);

        if (lobbyId is null || userId is null)
        {
            _logger.LogTrace("QueueSong rejected: ConnectionId={ConnectionId} Reason=not_in_lobby", Context.ConnectionId);
            throw Hub("not_in_lobby");
        }

        var validation = _queueValidator.Validate(args);
        if (!validation.IsValid)
        {
            var firstError = validation.Errors[0];
            _logger.LogTrace(
                "QueueSong rejected: ConnectionId={ConnectionId} Reason=validation_failed Property={Property}",
                Context.ConnectionId, firstError.PropertyName);
            throw Hub("validation_failed", firstError.PropertyName);
        }

        var hash = args.SongHash.ToLowerInvariant();
        var result = await _repo.EnqueueSongAsync(lobbyId, userId, hash, args.SongSpeed, _clock.GetUtcNow(), Context.ConnectionAborted);

        _logger.LogTrace(
            "QueueSong outcome: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} SongHash={SongHash} Outcome={Outcome} Sequence={Sequence}",
            Context.ConnectionId, lobbyId, userId, hash, result.Outcome, result.Entry?.Sequence);

        switch (result.Outcome)
        {
            case EnqueueOutcome.Added:
                var dto = _mapper.Map<QueuedSongDto>(result.Entry!);
                await Clients.Group(LobbyGroup(lobbyId)).OnSongQueued(new SongQueuedEvent(lobbyId, dto));
                // Queue head may have changed — push the updated Lobby snapshot
                // so browser clients see the new Song hash (used for the icon).
                var enqueuedLobby = await _repo.GetAsync(lobbyId, Context.ConnectionAborted);
                if (enqueuedLobby is { IsPublic: true })
                {
                    _buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Updated, _mapper.Map<LobbyDto>(enqueuedLobby)));
                }
                return dto;
            case EnqueueOutcome.NotFound:
            case EnqueueOutcome.NotMember:
                throw Hub("not_in_lobby");
            case EnqueueOutcome.NotInLibrary:
                throw Hub("song_not_in_library");
            case EnqueueOutcome.QueueFull:
                throw Hub("queue_full");
            default:
                throw new InvalidOperationException($"Unexpected enqueue outcome {result.Outcome}.");
        }
    }

    public async Task RemoveQueuedSong(RemoveQueuedSongArgs args)
    {
        var lobbyId = _connections.GetLobby(Context.ConnectionId);
        var userId = _connections.GetUserId(Context.ConnectionId);

        _logger.LogTrace(
            "RemoveQueuedSong: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} Sequence={Sequence}",
            Context.ConnectionId, lobbyId, userId, args.Sequence);

        if (lobbyId is null || userId is null)
        {
            _logger.LogTrace("RemoveQueuedSong rejected: ConnectionId={ConnectionId} Reason=not_in_lobby", Context.ConnectionId);
            throw Hub("not_in_lobby");
        }

        var result = await _repo.RemoveQueuedSongAsync(lobbyId, userId, args.Sequence, Context.ConnectionAborted);

        _logger.LogTrace(
            "RemoveQueuedSong outcome: ConnectionId={ConnectionId} LobbyId={LobbyId} UserId={UserId} Sequence={Sequence} Outcome={Outcome}",
            Context.ConnectionId, lobbyId, userId, args.Sequence, result.Outcome);

        switch (result.Outcome)
        {
            case RemoveQueuedSongOutcome.Removed:
                await Clients.Group(LobbyGroup(lobbyId))
                    .OnSongRemovedFromQueue(new SongRemovedFromQueueEvent(
                        lobbyId, args.Sequence, SongRemovalReason.Removed)
                    {
                        RemovedByUserId = userId,
                    });
                // Queue head may have changed — push updated snapshot for browser icon.
                var removedLobby = await _repo.GetAsync(lobbyId, Context.ConnectionAborted);
                if (removedLobby is { IsPublic: true })
                {
                    _buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Updated, _mapper.Map<LobbyDto>(removedLobby)));
                }
                return;
            case RemoveQueuedSongOutcome.NotFound:
            case RemoveQueuedSongOutcome.NotMember:
                throw Hub("not_in_lobby");
            case RemoveQueuedSongOutcome.EntryMissing:
                throw Hub("queue_entry_missing");
            case RemoveQueuedSongOutcome.NotPermitted:
                throw Hub("queue_remove_forbidden");
            default:
                throw new InvalidOperationException($"Unexpected remove outcome {result.Outcome}.");
        }
    }

    private async Task BroadcastQueueSideEffectsAsync(
        string lobbyId,
        IReadOnlyList<long>? removedQueueEntries,
        IReadOnlyList<QueueAvailabilityDelta>? queueAvailabilityUpdates,
        bool othersOnly,
        SongRemovalReason removalReason = SongRemovalReason.RequesterLeft)
    {
        var target = othersOnly
            ? Clients.OthersInGroup(LobbyGroup(lobbyId))
            : Clients.Group(LobbyGroup(lobbyId));

        if (removedQueueEntries is { Count: > 0 } removed)
        {
            _logger.LogTrace(
                "BroadcastQueueSideEffectsAsync removed entries: LobbyId={LobbyId} RemovedCount={RemovedCount} Reason={Reason}",
                lobbyId, removed.Count, removalReason);
            foreach (var sequence in removed)
            {
                await target.OnSongRemovedFromQueue(new SongRemovedFromQueueEvent(
                    lobbyId, sequence, removalReason));
            }
        }

        if (queueAvailabilityUpdates is { Count: > 0 } updates)
        {
            _logger.LogTrace(
                "BroadcastQueueSideEffectsAsync availability updates: LobbyId={LobbyId} UpdateCount={UpdateCount}",
                lobbyId, updates.Count);
            foreach (var qu in updates)
            {
                await target.OnQueuedSongAvailabilityChanged(new QueuedSongAvailabilityChangedEvent(
                    lobbyId,
                    qu.Sequence,
                    qu.AddedMissing.ToArray(),
                    qu.RemovedMissing.ToArray()));
            }
        }
    }

    /// <summary>
    /// Accumulate the streamed song-library chunks into a normalized (lowercased) set,
    /// validating each hash as it arrives. 
    /// Throws <c>validation_failed</c> for a malformed hash, an over-large library,
    /// or an empty library.
    /// </summary>
    private static async Task<HashSet<string>> DrainLibraryAsync(
        IAsyncEnumerable<string[]> library, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var chunk in library.WithCancellation(ct))
        {
            if (chunk is null) continue;
            foreach (var hash in chunk)
            {
                if (hash is null || !Sha1Hex.IsMatch(hash))
                {
                    throw Hub("validation_failed", "Invalid song hash in library");
                }
                set.Add(hash.ToLowerInvariant());
                if (set.Count > SongLibraryStreaming.MaxHashes)
                {
                    throw Hub("validation_failed", "Library too large");
                }
            }
        }
        if (set.Count == 0)
        {
            throw Hub("validation_failed", "Library empty");
        }
        return set;
    }

    private static HubException Hub(string code) => new(code);
    private static HubException Hub(string code, string detail) => new($"{code}: {detail}");
}
