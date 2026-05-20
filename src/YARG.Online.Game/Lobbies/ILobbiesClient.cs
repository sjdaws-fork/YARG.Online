namespace YARG.Online.Game.Lobbies;

public interface ILobbiesClient
{
    Task FinishGameAsync(string lobbyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tell the lobbies service that the song actually started playing — used to populate
    /// the lobby's SongStartedAt/SongDurationMs fields for the browser progress display.
    /// Called once per game, right after the game server broadcasts GameStartCue.
    /// </summary>
    Task SongStartedAsync(
        string lobbyId,
        long songOriginUtcMs,
        int durationMs,
        CancellationToken cancellationToken = default);
}
