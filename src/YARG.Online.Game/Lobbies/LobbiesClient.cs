using System.Net.Http.Json;

namespace YARG.Online.Game.Lobbies;

public sealed class LobbiesClient : ILobbiesClient
{
    private readonly HttpClient _http;

    public LobbiesClient(HttpClient http)
    {
        _http = http;
    }

    public async Task FinishGameAsync(string lobbyId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lobbyId))
        {
            throw new ArgumentException("lobbyId is required.", nameof(lobbyId));
        }

        var response = await _http.PostAsync(
            $"api/v1/lobbies/{Uri.EscapeDataString(lobbyId)}/game-finished",
            content: null,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SongStartedAsync(
        string lobbyId,
        long songOriginUtcMs,
        int durationMs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lobbyId))
        {
            throw new ArgumentException("lobbyId is required.", nameof(lobbyId));
        }

        var body = new SongStartedRequestBody(songOriginUtcMs, durationMs);
        var response = await _http.PostAsJsonAsync(
            $"api/v1/lobbies/{Uri.EscapeDataString(lobbyId)}/song-started",
            body,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // Wire shape mirrors GameLifecycleEndpoints.SongStartedRequest on the lobbies side.
    private sealed record SongStartedRequestBody(long SongOriginUtcMs, int DurationMs);
}
