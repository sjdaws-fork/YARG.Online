using Microsoft.AspNetCore.SignalR.Client;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Contracts.Rest;

namespace YARG.Online.E2E.Tests;

internal sealed class ClientHarness
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

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

    public Task<(string LobbyId, string UserId, string DisplayName)> NextPlayerJoined() =>
        Pull(_joins, ref _joinWaiter);

    public Task<(string LobbyId, QueuedSongDto Song)> NextSongQueued() =>
        Pull(_songsQueued, ref _songQueuedWaiter);

    public Task<(string LobbyId, long Sequence, SongRemovalReason Reason)> NextSongRemoved() =>
        Pull(_songsRemoved, ref _songRemovedWaiter);

    public Task<(string LobbyId, string Endpoint, string ConnectionKey, string GameToken, DateTimeOffset ExpiresAt)> NextGameStarted() =>
        Pull(_gameStarteds, ref _gameStartedWaiter);

    public Task<(string LobbyId, LobbyStatus Status)> NextStatusChanged() =>
        Pull(_statusChanges, ref _statusChangedWaiter);

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
