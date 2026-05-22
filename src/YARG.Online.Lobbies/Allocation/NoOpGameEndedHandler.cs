namespace YARG.Online.Lobbies.Allocation;

public sealed class NoOpGameEndedHandler : IGameEndedHandler
{
    public Task OnGameEndedAsync(GameEndedContext context, CancellationToken ct) => Task.CompletedTask;
}
