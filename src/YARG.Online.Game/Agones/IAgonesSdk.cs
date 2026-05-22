namespace YARG.Online.Game.Agones;

public interface IAgonesSdk
{
    Task ReadyAsync(CancellationToken ct);

    Task HealthAsync(CancellationToken ct);

    Task ShutdownAsync(CancellationToken ct);
}
