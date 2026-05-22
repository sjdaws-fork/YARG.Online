using AutoMapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using YARG.Online.Lobbies.Allocation;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Online.Lobbies.Hubs;
using YARG.Online.Lobbies.Lobbies;

namespace YARG.Online.Lobbies.Endpoints;

public static class GameLifecycleEndpoints
{
    public static IEndpointRouteBuilder MapGameLifecycleEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/lobbies").WithTags("GameLifecycle");

        group.MapPost("/{lobbyId}/game-finished", FinishGame)
            .AllowAnonymous()
            .WithName("FinishGame")
            .WithSummary("Mark a lobby's game as finished and return it to song select.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{lobbyId}/song-started", SongStarted)
            .AllowAnonymous()
            .WithName("SongStarted")
            .WithSummary("Record the wall-clock start time and duration of the song now playing.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    public sealed record SongStartedRequest(long SongOriginUtcMs, int DurationMs);

    private static async Task<Results<NoContent, ProblemHttpResult>> FinishGame(
        string lobbyId,
        ILobbyRepository repo,
        IHubContext<LobbyHub, ILobbyHubClient> hub,
        IMapper mapper,
        ILobbyChangeBuffer buffer,
        IGameEndedHandler gameEndedHandler,
        ILogger<LobbyHub> logger,
        CancellationToken ct)
    {
        var result = await repo.FinishGameAsync(lobbyId, ct);
        switch (result.Outcome)
        {
            case FinishGameOutcome.Finished:
                await hub.Clients.Group(LobbyHub.LobbyGroup(lobbyId))
                    .OnLobbyStatusChanged(new LobbyStatusChangedEvent(lobbyId, LobbyStatus.SongSelect));
                if (result.PlayedSong is { } played)
                {
                    await hub.Clients.Group(LobbyHub.LobbyGroup(lobbyId))
                        .OnSongRemovedFromQueue(new SongRemovedFromQueueEvent(
                            lobbyId, played.Sequence, SongRemovalReason.Played));
                }
                buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Updated, mapper.Map<LobbyDto>(result.Lobby!)));

                if (result.Allocation is { } allocation)
                {
                    // Slot release is best-effort — the song is over either way, and the
                    // FleetAutoscaler / lobby idle cleanup are tolerant of stuck slot values.
                    try
                    {
                        await gameEndedHandler.OnGameEndedAsync(
                            new GameEndedContext(lobbyId, allocation.GameServerName, allocation.SlotCount), ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "IGameEndedHandler failed for LobbyId={LobbyId} GameServer={GameServer}",
                            lobbyId, allocation.GameServerName);
                    }
                }

                return TypedResults.NoContent();
            case FinishGameOutcome.NotFound:
                return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "lobby_not_found");
            case FinishGameOutcome.NotStarted:
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "not_started");
            default:
                throw new InvalidOperationException($"Unexpected finish outcome {result.Outcome}.");
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> SongStarted(
        string lobbyId,
        SongStartedRequest request,
        ILobbyRepository repo,
        IMapper mapper,
        ILobbyChangeBuffer buffer,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(request.SongOriginUtcMs);
        var result = await repo.SongStartedAsync(lobbyId, startedAt, request.DurationMs, ct);
        switch (result.Outcome)
        {
            case SongStartedOutcome.Set:
                // No SignalR notification — members already know the song is starting (they got
                // GameStartCue directly from the game server). The buffer push is what surfaces
                // the new SongStartedAt/SongDurationMs to the lobby browser feed.
                buffer.Enqueue(new LobbyChange(lobbyId, LobbyChangeKind.Updated, mapper.Map<LobbyDto>(result.Lobby!)));
                return TypedResults.NoContent();
            case SongStartedOutcome.NotFound:
                return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "lobby_not_found");
            case SongStartedOutcome.NotStarted:
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "not_started");
            default:
                throw new InvalidOperationException($"Unexpected song-started outcome {result.Outcome}.");
        }
    }
}
