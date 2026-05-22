namespace YARG.Online.Game.Agones;

/// <summary>
/// Signal flipped once the UDP listener is bound and accepting traffic, awaited
/// by <see cref="AgonesReadyService"/> before it POSTs to the SDK sidecar's
/// /ready endpoint. Keeps <c>GameNetworkService</c> Agones-agnostic — it just
/// announces readiness; a separate hosted service decides what to do with it.
/// </summary>
public sealed class AgonesReadinessSignal
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync(CancellationToken ct) => _tcs.Task.WaitAsync(ct);

    public void Set() => _tcs.TrySetResult();
}
