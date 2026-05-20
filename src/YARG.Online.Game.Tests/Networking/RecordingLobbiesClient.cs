using System.Collections.Concurrent;
using YARG.Online.Game.Lobbies;

namespace YARG.Online.Game.Tests.Networking;

internal sealed record SongStartedCall(string LobbyId, long SongOriginUtcMs, int DurationMs);

internal sealed class RecordingLobbiesClient : ILobbiesClient
{
    public ConcurrentQueue<string> FinishCalls { get; } = new();
    public ConcurrentQueue<SongStartedCall> SongStartedCalls { get; } = new();

    public Func<string, CancellationToken, Task>? OnFinishGame { get; set; }
    public Func<SongStartedCall, CancellationToken, Task>? OnSongStarted { get; set; }

    public Task FinishGameAsync(string lobbyId, CancellationToken cancellationToken = default)
    {
        FinishCalls.Enqueue(lobbyId);
        return OnFinishGame is null ? Task.CompletedTask : OnFinishGame(lobbyId, cancellationToken);
    }

    public Task SongStartedAsync(
        string lobbyId,
        long songOriginUtcMs,
        int durationMs,
        CancellationToken cancellationToken = default)
    {
        var call = new SongStartedCall(lobbyId, songOriginUtcMs, durationMs);
        SongStartedCalls.Enqueue(call);
        return OnSongStarted is null ? Task.CompletedTask : OnSongStarted(call, cancellationToken);
    }
}
