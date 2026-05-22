using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using YARG.Online.Lobbies.Allocation;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Domain;

namespace YARG.Online.Lobbies.Lobbies;

public sealed class InMemoryLobbyRepository : ILobbyRepository
{
    private sealed class Entry
    {
        public Lobby Lobby = default!;
        public readonly HashSet<string> Members = new(StringComparer.Ordinal);
        // Membership order. Used to pick a deterministic next host when the current host leaves.
        public readonly List<string> JoinOrder = new();
        public readonly Dictionary<string, string> DisplayNames =
            new(StringComparer.Ordinal);
        // Per-member selected instrument as a YARG.Core.Instrument byte. Echoed back
        // in LobbyMemberDto and PlayerJoinedEvent so other members can render the
        // right instrument icon in their player list. Not cleared on leave —
        // mirrors DisplayNames behavior; the entry's lifecycle is the lobby's lifetime.
        public readonly Dictionary<string, byte> Instruments =
            new(StringComparer.Ordinal);
        // Per-member "currently on the lobby/song-select screen" flag. True
        // by default (every joining member arrives at the lobby), flipped to
        // false in StartGameAsync, flipped back to true in LeaveResultsAsync
        // when each member confirms they've returned from the results screen.
        // The host's StartGameAsync is gated on every member being true.
        public readonly Dictionary<string, bool> IsBackInLobby =
            new(StringComparer.Ordinal);
        // Users banned for the lobby's lifetime. Populated by KickPlayerAsync, cleared on lobby close.
        public readonly HashSet<string> Banned = new(StringComparer.Ordinal);
        public readonly Dictionary<string, HashSet<string>> PlayerLibraries =
            new(StringComparer.Ordinal);
        public HashSet<string> LobbySongLibrary = new(StringComparer.Ordinal);
        public readonly Queue<ChatMessage> ChatHistory = new();
        public long NextChatSequence = 1;
        public readonly List<QueuedSong> SongQueue = new();
        public long NextQueueSequence = 1;
        // Allocation stamped by ConfirmStartGameAsync; consumed and cleared by FinishGameAsync.
        public GameAllocation? CurrentAllocation;
        public readonly object Lock = new();
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _maxChatHistorySize;
    private readonly int _maxQueueSize;

    public InMemoryLobbyRepository(IOptions<LobbyOptions> options)
    {
        _maxChatHistorySize = Math.Max(1, options.Value.MaxChatHistorySize);
        _maxQueueSize = Math.Max(1, options.Value.MaxQueueSize);
    }

    public Task<bool> CreateAsync(Lobby lobby, IReadOnlyCollection<string> hostLibrary, byte hostInstrument, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var hostLib = new HashSet<string>(hostLibrary, StringComparer.Ordinal);
        var entry = new Entry
        {
            Lobby = lobby with { SharedSongCount = hostLib.Count },
            LobbySongLibrary = new HashSet<string>(hostLib, StringComparer.Ordinal),
        };
        entry.Members.Add(lobby.HostUserId);
        entry.JoinOrder.Add(lobby.HostUserId);
        entry.DisplayNames[lobby.HostUserId] = lobby.HostName;
        entry.Instruments[lobby.HostUserId] = hostInstrument;
        entry.PlayerLibraries[lobby.HostUserId] = hostLib;
        entry.IsBackInLobby[lobby.HostUserId] = true;

        var added = _entries.TryAdd(lobby.Id, entry);
        return Task.FromResult(added);
    }

    public Task<Lobby?> GetAsync(string lobbyId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult<Lobby?>(null);
        }

        lock (entry.Lock)
        {
            return Task.FromResult<Lobby?>(entry.Lobby);
        }
    }

    public Task<JoinResultData> JoinAsync(string lobbyId, string userId, string displayName, IReadOnlyCollection<string> library, byte instrument, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult(new JoinResultData(JoinResult.NotFound, null, null, null, null));
        }

        lock (entry.Lock)
        {
            if (entry.Banned.Contains(userId))
            {
                return Task.FromResult(new JoinResultData(JoinResult.Banned, null, null, null, null));
            }

            if (entry.Members.Contains(userId))
            {
                return Task.FromResult(new JoinResultData(
                    JoinResult.AlreadyMember,
                    entry.Lobby,
                    null,
                    entry.LobbySongLibrary.ToArray(),
                    null));
            }

            if (entry.Members.Count >= entry.Lobby.MaxPlayers)
            {
                return Task.FromResult(new JoinResultData(JoinResult.Full, null, null, null, null));
            }

            var lib = new HashSet<string>(library, StringComparer.Ordinal);
            entry.Members.Add(userId);
            entry.JoinOrder.Add(userId);
            entry.DisplayNames[userId] = displayName;
            entry.Instruments[userId] = instrument;
            entry.PlayerLibraries[userId] = lib;
            // A late joiner lands on the lobby/song-select screen even if a
            // game is in progress for other members, so the flag is true on
            // arrival. The flag only flips for joiners who themselves go
            // through a StartGame → ResultsScreen → LeaveResults cycle.
            entry.IsBackInLobby[userId] = true;

            // New shared library = old shared library ∩ new player's library.
            var removed = new List<string>();
            entry.LobbySongLibrary.RemoveWhere(h =>
            {
                if (lib.Contains(h)) return false;
                removed.Add(h);
                return true;
            });

            // Update the queue: append the new player to MissingFor for every song they don't own.
            List<QueueAvailabilityDelta>? queueUpdates = null;
            for (var i = 0; i < entry.SongQueue.Count; i++)
            {
                var queued = entry.SongQueue[i];
                if (lib.Contains(queued.SongHash)) continue;

                var newMissing = new List<string>(queued.MissingFor.Count + 1);
                newMissing.AddRange(queued.MissingFor);
                newMissing.Add(userId);
                entry.SongQueue[i] = queued with { MissingFor = newMissing };

                queueUpdates ??= new List<QueueAvailabilityDelta>();
                queueUpdates.Add(new QueueAvailabilityDelta(
                    queued.Sequence,
                    new[] { userId },
                    Array.Empty<string>()));
            }

            entry.Lobby = entry.Lobby with
            {
                PlayerCount = entry.Members.Count,
                SharedSongCount = entry.LobbySongLibrary.Count,
            };

            return Task.FromResult(new JoinResultData(
                JoinResult.Joined,
                entry.Lobby,
                new SongLibraryDelta(Array.Empty<string>(), removed),
                entry.LobbySongLibrary.ToArray(),
                queueUpdates));
        }
    }

    public Task<LeaveResult> LeaveAsync(string lobbyId, string userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult(new LeaveResult(LeaveOutcome.NotFound, null, null, null, null, null));
        }

        // We snapshot the decision under the entry lock, then evict from the outer dictionary only
        // after releasing it (TryRemove on ConcurrentDictionary is itself thread-safe).
        bool close;
        Lobby? updated;
        SongLibraryDelta? delta;
        HostChange? hostChange;
        List<long>? removedQueueEntries = null;
        List<QueueAvailabilityDelta>? queueUpdates = null;
        lock (entry.Lock)
        {
            if (!entry.Members.Contains(userId))
            {
                return Task.FromResult(new LeaveResult(LeaveOutcome.NotFound, null, null, null, null, null));
            }

            var wasHost = entry.Lobby.HostUserId == userId;
            (delta, removedQueueEntries, queueUpdates) = RemoveMemberLocked(entry, userId);

            if (entry.Members.Count == 0)
            {
                close = true;
                updated = null;
                delta = null;
                removedQueueEntries = null;
                queueUpdates = null;
                hostChange = null;
            }
            else
            {
                close = false;
                if (wasHost)
                {
                    var newHostId = entry.JoinOrder[0];
                    var newHostName = entry.DisplayNames.TryGetValue(newHostId, out var n) ? n : string.Empty;
                    entry.Lobby = entry.Lobby with
                    {
                        HostUserId = newHostId,
                        HostName = newHostName,
                    };
                    hostChange = new HostChange(newHostId, newHostName);
                }
                else
                {
                    hostChange = null;
                }
                updated = entry.Lobby;
            }
        }

        if (close)
        {
            _entries.TryRemove(lobbyId, out _);
            return Task.FromResult(new LeaveResult(LeaveOutcome.LobbyClosed, null, null, null, null, null));
        }

        return Task.FromResult(new LeaveResult(LeaveOutcome.Left, updated, delta, removedQueueEntries, queueUpdates, hostChange));
    }

    public Task<bool> IsMemberAsync(string lobbyId, string userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult(false);
        }

        lock (entry.Lock)
        {
            return Task.FromResult(entry.Members.Contains(userId));
        }
    }

    public Task<IReadOnlyList<LobbyMemberDto>> GetMembersAsync(string lobbyId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult<IReadOnlyList<LobbyMemberDto>>(Array.Empty<LobbyMemberDto>());
        }

        lock (entry.Lock)
        {
            var members = new List<LobbyMemberDto>(entry.JoinOrder.Count);
            foreach (var userId in entry.JoinOrder)
            {
                var name = entry.DisplayNames.TryGetValue(userId, out var n) ? n : userId;
                var inst = entry.Instruments.TryGetValue(userId, out var i) ? i : (byte) 0;
                var back = !entry.IsBackInLobby.TryGetValue(userId, out var b) || b;
                members.Add(new LobbyMemberDto(userId, name)
                {
                    Instrument = inst,
                    IsBackInLobby = back,
                });
            }
            return Task.FromResult<IReadOnlyList<LobbyMemberDto>>(members);
        }
    }

    public Task<LobbySearchResult> SearchAsync(LobbySearchQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var snapshot = new List<Lobby>(_entries.Count);
        foreach (var entry in _entries.Values)
        {
            lock (entry.Lock)
            {
                snapshot.Add(entry.Lobby);
            }
        }

        IEnumerable<Lobby> filtered = snapshot;

        if (query.GameMode is { } gm)
        {
            filtered = filtered.Where(l => l.GameMode == gm);
        }

        if (query.Region is { } region)
        {
            filtered = filtered.Where(l => l.Region == region);
        }

        if (!string.IsNullOrEmpty(query.Q))
        {
            var needle = query.Q;
            filtered = filtered.Where(l =>
                l.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                l.Song.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = filtered
            .OrderByDescending(l => l.CreatedAt)
            .ThenBy(l => l.Id, StringComparer.Ordinal)
            .ToArray();

        var total = ordered.LongLength;
        var page = ordered.Skip(query.Skip).Take(query.Take).ToArray();
        return Task.FromResult(new LobbySearchResult(page, total));
    }

    public Task<ChatMessage?> AppendChatMessageAsync(
        string lobbyId,
        string userId,
        string displayName,
        string text,
        DateTimeOffset sentAt,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult<ChatMessage?>(null);
        }

        lock (entry.Lock)
        {
            if (!entry.Members.Contains(userId))
            {
                return Task.FromResult<ChatMessage?>(null);
            }

            var message = new ChatMessage(entry.NextChatSequence++, userId, displayName, text, sentAt);

            if (entry.ChatHistory.Count >= _maxChatHistorySize)
            {
                entry.ChatHistory.Dequeue();
            }
            entry.ChatHistory.Enqueue(message);

            return Task.FromResult<ChatMessage?>(message);
        }
    }

    public Task<IReadOnlyList<ChatMessage>> GetChatHistoryAsync(string lobbyId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
        }

        lock (entry.Lock)
        {
            return Task.FromResult<IReadOnlyList<ChatMessage>>(entry.ChatHistory.ToArray());
        }
    }

    public Task<EnqueueResult> EnqueueSongAsync(
        string lobbyId,
        string userId,
        string songHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult(new EnqueueResult(EnqueueOutcome.NotFound, null));
        }

        lock (entry.Lock)
        {
            if (!entry.Members.Contains(userId))
            {
                return Task.FromResult(new EnqueueResult(EnqueueOutcome.NotMember, null));
            }

            if (!entry.PlayerLibraries.TryGetValue(userId, out var requesterLib) || !requesterLib.Contains(songHash))
            {
                return Task.FromResult(new EnqueueResult(EnqueueOutcome.NotInLibrary, null));
            }

            if (entry.SongQueue.Count >= _maxQueueSize)
            {
                return Task.FromResult(new EnqueueResult(EnqueueOutcome.QueueFull, null));
            }

            var missing = new List<string>();
            foreach (var member in entry.Members)
            {
                if (member == userId) continue;
                if (!entry.PlayerLibraries.TryGetValue(member, out var memberLib) || !memberLib.Contains(songHash))
                {
                    missing.Add(member);
                }
            }

            var queued = new QueuedSong(
                Sequence: entry.NextQueueSequence++,
                SongHash: songHash,
                RequesterId: userId,
                QueuedAt: now,
                MissingFor: missing);
            entry.SongQueue.Add(queued);

            return Task.FromResult(new EnqueueResult(EnqueueOutcome.Added, queued));
        }
    }

    public Task<RemoveQueuedSongResult> RemoveQueuedSongAsync(
        string lobbyId,
        string userId,
        long sequence,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult(new RemoveQueuedSongResult(RemoveQueuedSongOutcome.NotFound, null));
        }

        lock (entry.Lock)
        {
            if (!entry.Members.Contains(userId))
            {
                return Task.FromResult(new RemoveQueuedSongResult(RemoveQueuedSongOutcome.NotMember, null));
            }

            var index = entry.SongQueue.FindIndex(q => q.Sequence == sequence);
            if (index < 0)
            {
                return Task.FromResult(new RemoveQueuedSongResult(RemoveQueuedSongOutcome.EntryMissing, null));
            }

            var existing = entry.SongQueue[index];
            var isHost = entry.Lobby.HostUserId == userId;
            var isRequester = existing.RequesterId == userId;
            if (!isHost && !isRequester)
            {
                return Task.FromResult(new RemoveQueuedSongResult(RemoveQueuedSongOutcome.NotPermitted, null));
            }

            entry.SongQueue.RemoveAt(index);
            return Task.FromResult(new RemoveQueuedSongResult(RemoveQueuedSongOutcome.Removed, existing));
        }
    }

    public Task<IReadOnlyList<QueuedSong>> GetQueueAsync(string lobbyId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult<IReadOnlyList<QueuedSong>>(Array.Empty<QueuedSong>());
        }

        lock (entry.Lock)
        {
            return Task.FromResult<IReadOnlyList<QueuedSong>>(entry.SongQueue.ToArray());
        }
    }

    public Task<TransferHostResult> TransferHostAsync(string lobbyId, string callerUserId, string targetUserId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult(new TransferHostResult(TransferHostOutcome.NotFound, null, null));
        }

        lock (entry.Lock)
        {
            if (entry.Lobby.HostUserId != callerUserId)
            {
                return Task.FromResult(new TransferHostResult(TransferHostOutcome.NotHost, null, null));
            }

            if (targetUserId == callerUserId)
            {
                return Task.FromResult(new TransferHostResult(TransferHostOutcome.TargetIsHost, null, null));
            }

            if (!entry.Members.Contains(targetUserId))
            {
                return Task.FromResult(new TransferHostResult(TransferHostOutcome.TargetNotMember, null, null));
            }

            var newName = entry.DisplayNames.TryGetValue(targetUserId, out var n) ? n : string.Empty;
            entry.Lobby = entry.Lobby with
            {
                HostUserId = targetUserId,
                HostName = newName,
            };
            return Task.FromResult(new TransferHostResult(
                TransferHostOutcome.Transferred,
                entry.Lobby,
                new HostChange(targetUserId, newName)));
        }
    }

    public Task<KickResult> KickPlayerAsync(string lobbyId, string callerUserId, string targetUserId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult(new KickResult(KickOutcome.NotFound, null, null, null, null));
        }

        lock (entry.Lock)
        {
            if (entry.Lobby.HostUserId != callerUserId)
            {
                return Task.FromResult(new KickResult(KickOutcome.NotHost, null, null, null, null));
            }

            if (targetUserId == callerUserId)
            {
                return Task.FromResult(new KickResult(KickOutcome.TargetIsHost, null, null, null, null));
            }

            if (!entry.Members.Contains(targetUserId))
            {
                return Task.FromResult(new KickResult(KickOutcome.TargetNotMember, null, null, null, null));
            }

            var (delta, removedQueue, queueUpdates) = RemoveMemberLocked(entry, targetUserId);
            entry.Banned.Add(targetUserId);

            return Task.FromResult(new KickResult(
                KickOutcome.Kicked,
                entry.Lobby,
                delta,
                removedQueue,
                queueUpdates));
        }
    }

    public Task<BeginStartGameResultData> BeginStartGameAsync(string lobbyId, string callerUserId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult(new BeginStartGameResultData(StartGameOutcome.NotFound, null, 0));
        }

        lock (entry.Lock)
        {
            if (entry.Lobby.HostUserId != callerUserId)
            {
                return Task.FromResult(new BeginStartGameResultData(StartGameOutcome.NotHost, null, 0));
            }

            switch (entry.Lobby.Status)
            {
                case LobbyStatus.Starting:
                    return Task.FromResult(new BeginStartGameResultData(StartGameOutcome.AlreadyStarting, null, 0));
                case LobbyStatus.GameStarted:
                    return Task.FromResult(new BeginStartGameResultData(StartGameOutcome.AlreadyStarted, null, 0));
                case LobbyStatus.SongSelect:
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected lobby status {entry.Lobby.Status}.");
            }

            if (entry.Members.Count < 2)
            {
                return Task.FromResult(new BeginStartGameResultData(StartGameOutcome.NotEnoughPlayers, null, 0));
            }

            if (entry.SongQueue.Count == 0)
            {
                return Task.FromResult(new BeginStartGameResultData(StartGameOutcome.QueueEmpty, null, 0));
            }

            // Gate: every member must have signalled they're back in the
            // lobby. The flag is true by default for fresh joiners and the
            // host on lobby creation; flipped false on the previous StartGame
            // and back to true only when each member calls LeaveResults
            // after closing the post-game results screen.
            foreach (var userId in entry.JoinOrder)
            {
                if (entry.IsBackInLobby.TryGetValue(userId, out var ready) && !ready)
                {
                    return Task.FromResult(new BeginStartGameResultData(StartGameOutcome.PlayersStillInResults, null, 0));
                }
            }

            entry.Lobby = entry.Lobby with { Status = LobbyStatus.Starting };

            return Task.FromResult(new BeginStartGameResultData(StartGameOutcome.Started, entry.Lobby, entry.JoinOrder.Count));
        }
    }

    public Task<ConfirmStartGameResultData> ConfirmStartGameAsync(string lobbyId, GameAllocation allocation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult(new ConfirmStartGameResultData(ConfirmStartGameOutcome.NotFound, null, null));
        }

        lock (entry.Lock)
        {
            if (entry.Lobby.Status != LobbyStatus.Starting)
            {
                return Task.FromResult(new ConfirmStartGameResultData(ConfirmStartGameOutcome.NotStarting, null, null));
            }

            entry.CurrentAllocation = allocation;
            entry.Lobby = entry.Lobby with { Status = LobbyStatus.GameStarted };

            // Snapshot the membership at the GameStarted transition (not at
            // BeginStartGameAsync) so the per-user tokens the caller mints and
            // the quorum count baked into them reflect exactly who is in the
            // lobby now — membership can shift while the allocator runs.
            var members = new List<StartGameMember>(entry.JoinOrder.Count);
            foreach (var userId in entry.JoinOrder)
            {
                var name = entry.DisplayNames.TryGetValue(userId, out var n) ? n : userId;
                members.Add(new StartGameMember(userId, name));

                // Each member is now in-game until they report back via
                // LeaveResults. Flip the flags atomically with the GameStarted
                // transition — not in BeginStartGameAsync — so that an aborted
                // allocation (AbortStartGameAsync) leaves them untouched and
                // the host can retry StartGame.
                entry.IsBackInLobby[userId] = false;
            }

            return Task.FromResult(new ConfirmStartGameResultData(ConfirmStartGameOutcome.Started, entry.Lobby, members));
        }
    }

    public Task AbortStartGameAsync(string lobbyId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.CompletedTask;
        }

        lock (entry.Lock)
        {
            if (entry.Lobby.Status != LobbyStatus.Starting)
            {
                return Task.CompletedTask;
            }

            entry.Lobby = entry.Lobby with { Status = LobbyStatus.SongSelect };
        }
        return Task.CompletedTask;
    }

    public Task<FinishGameResultData> FinishGameAsync(string lobbyId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult(new FinishGameResultData(FinishGameOutcome.NotFound, null, null, null));
        }

        lock (entry.Lock)
        {
            if (entry.Lobby.Status != LobbyStatus.GameStarted)
            {
                return Task.FromResult(new FinishGameResultData(FinishGameOutcome.NotStarted, null, null, null));
            }

            // The song that was just played is by construction at the head of the queue (StartGame
            // requires Count > 0; nothing reorders the queue while GameStarted). Pop it so the
            // host doesn't have to manually remove it before starting the next round.
            // Defensive: the queue could be empty if a member-departure cascade purged it during
            // gameplay — in that case we still clear runtime fields, just with no PlayedSong.
            QueuedSong? played = null;
            if (entry.SongQueue.Count > 0)
            {
                played = entry.SongQueue[0];
                entry.SongQueue.RemoveAt(0);
            }

            var allocation = entry.CurrentAllocation;
            entry.CurrentAllocation = null;

            entry.Lobby = entry.Lobby with
            {
                Status = LobbyStatus.SongSelect,
                SongStartedAt = null,
                SongDurationMs = null,
            };
            return Task.FromResult(new FinishGameResultData(FinishGameOutcome.Finished, entry.Lobby, played, allocation));
        }
    }

    public Task<SongStartedResultData> SongStartedAsync(
        string lobbyId,
        DateTimeOffset startedAt,
        int durationMs,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult(new SongStartedResultData(SongStartedOutcome.NotFound, null));
        }

        lock (entry.Lock)
        {
            if (entry.Lobby.Status != LobbyStatus.GameStarted)
            {
                return Task.FromResult(new SongStartedResultData(SongStartedOutcome.NotStarted, null));
            }

            entry.Lobby = entry.Lobby with
            {
                SongStartedAt = startedAt,
                SongDurationMs = durationMs,
            };
            return Task.FromResult(new SongStartedResultData(SongStartedOutcome.Set, entry.Lobby));
        }
    }

    public Task<LeaveResultsResultData> LeaveResultsAsync(string lobbyId, string userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(lobbyId, out var entry))
        {
            return Task.FromResult(new LeaveResultsResultData(LeaveResultsOutcome.NotFound, null));
        }

        lock (entry.Lock)
        {
            if (!entry.Members.Contains(userId))
            {
                return Task.FromResult(new LeaveResultsResultData(LeaveResultsOutcome.NotMember, null));
            }

            if (entry.IsBackInLobby.TryGetValue(userId, out var already) && already)
            {
                return Task.FromResult(new LeaveResultsResultData(LeaveResultsOutcome.AlreadyBackInLobby, entry.Lobby));
            }

            entry.IsBackInLobby[userId] = true;
            return Task.FromResult(new LeaveResultsResultData(LeaveResultsOutcome.MarkedBackInLobby, entry.Lobby));
        }
    }

    /// <summary>
    /// Remove a non-host member from the lobby under <c>entry.Lock</c>. Updates the queue
    /// (drops the member's entries, strips them from <c>MissingFor</c>) and the shared song
    /// library (full intersection recalc), and refreshes the <c>Lobby</c> snapshot's
    /// <c>PlayerCount</c>/<c>SharedSongCount</c>. Caller is responsible for the host-handoff
    /// decision and for honoring <c>entry.Members.Count == 0</c> as a lobby-close signal.
    /// </summary>
    private static (SongLibraryDelta Delta, List<long>? RemovedQueueEntries, List<QueueAvailabilityDelta>? QueueUpdates) RemoveMemberLocked(Entry entry, string userId)
    {
        entry.Members.Remove(userId);
        entry.JoinOrder.Remove(userId);
        entry.DisplayNames.Remove(userId);
        entry.PlayerLibraries.Remove(userId);

        List<long>? removedQueueEntries = null;
        List<QueueAvailabilityDelta>? queueUpdates = null;

        // Drop the leaving player's queue entries, and strip them from MissingFor on others.
        for (var i = entry.SongQueue.Count - 1; i >= 0; i--)
        {
            var queued = entry.SongQueue[i];
            if (queued.RequesterId == userId)
            {
                removedQueueEntries ??= new List<long>();
                removedQueueEntries.Add(queued.Sequence);
                entry.SongQueue.RemoveAt(i);
                continue;
            }

            if (queued.MissingFor.Contains(userId))
            {
                var trimmed = queued.MissingFor.Where(id => id != userId).ToArray();
                entry.SongQueue[i] = queued with { MissingFor = trimmed };
                queueUpdates ??= new List<QueueAvailabilityDelta>();
                queueUpdates.Add(new QueueAvailabilityDelta(
                    queued.Sequence,
                    Array.Empty<string>(),
                    new[] { userId }));
            }
        }

        // Full intersection recalc across remaining libraries.
        HashSet<string>? newShared = null;
        foreach (var (_, plib) in entry.PlayerLibraries)
        {
            if (newShared is null)
            {
                newShared = new HashSet<string>(plib, StringComparer.Ordinal);
            }
            else
            {
                newShared.IntersectWith(plib);
            }

            if (newShared.Count == 0) break;
        }
        newShared ??= new HashSet<string>(StringComparer.Ordinal);

        var added = newShared.Except(entry.LobbySongLibrary, StringComparer.Ordinal).ToList();
        var removed = entry.LobbySongLibrary.Except(newShared, StringComparer.Ordinal).ToList();
        entry.LobbySongLibrary = newShared;

        entry.Lobby = entry.Lobby with
        {
            PlayerCount = entry.Members.Count,
            SharedSongCount = entry.LobbySongLibrary.Count,
        };

        return (new SongLibraryDelta(added, removed), removedQueueEntries, queueUpdates);
    }
}
