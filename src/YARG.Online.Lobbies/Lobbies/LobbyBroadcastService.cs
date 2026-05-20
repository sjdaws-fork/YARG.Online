using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Online.Lobbies.Hubs;

namespace YARG.Online.Lobbies.Lobbies;

public sealed class LobbyBroadcastService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);

    private readonly ILobbyChangeBuffer _buffer;
    private readonly IHubContext<LobbyHub, ILobbyHubClient> _hub;
    private readonly ILogger<LobbyBroadcastService> _logger;
    private long _sequence;

    public LobbyBroadcastService(
        ILobbyChangeBuffer buffer,
        IHubContext<LobbyHub, ILobbyHubClient> hub,
        ILogger<LobbyBroadcastService> logger)
    {
        _buffer = buffer;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var changes = _buffer.Drain();
                if (changes.Count == 0) continue;

                var added = new List<LobbyDto>();
                var updated = new List<LobbyDto>();
                var removed = new List<string>();
                foreach (var c in changes)
                {
                    switch (c.Kind)
                    {
                        case LobbyChangeKind.Added when c.Lobby is { } a:
                            added.Add(a);
                            break;
                        case LobbyChangeKind.Updated when c.Lobby is { } u:
                            updated.Add(u);
                            break;
                        case LobbyChangeKind.Removed:
                            removed.Add(c.LobbyId);
                            break;
                    }
                }

                var batch = new LobbyBatchUpdate(
                    added.ToArray(),
                    updated.ToArray(),
                    removed.ToArray(),
                    Interlocked.Increment(ref _sequence));

                await _hub.Clients.Group(LobbyHub.BrowseGroup).OnLobbyBatch(batch);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lobby broadcast tick failed; will retry on next tick.");
            }
        }
    }
}
