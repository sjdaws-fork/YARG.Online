namespace YARG.Online.Lobbies.Allocation;

/// <summary>
/// Invoked when a match finishes. The in-cluster implementation releases the slots
/// reserved on the Agones GameServer; the out-of-cluster no-op implementation does
/// nothing. Errors are best-effort — the FinishGame REST handler must not fail the
/// client response if slot release errors out.
/// </summary>
public interface IGameEndedHandler
{
    Task OnGameEndedAsync(GameEndedContext context, CancellationToken ct);
}

public sealed record GameEndedContext(string LobbyId, string? GameServerName, int SlotCount);
