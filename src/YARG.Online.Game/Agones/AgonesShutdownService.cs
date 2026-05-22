using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace YARG.Online.Game.Agones;

/// <summary>
/// Hooks <see cref="IHostApplicationLifetime.ApplicationStopping"/> to send
/// SDK.Shutdown() before the host begins tearing down hosted services. Without
/// this, when Agones (or kubectl) deletes the pod, the sidecar waits the full
/// <c>terminationGracePeriodSeconds</c> for a shutdown signal that never comes
/// — pods take ~30s to terminate instead of exiting promptly.
/// </summary>
public sealed class AgonesShutdownService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IAgonesSdk _sdk;
    private readonly ILogger<AgonesShutdownService> _logger;
    private CancellationTokenRegistration _registration;

    public AgonesShutdownService(
        IHostApplicationLifetime lifetime,
        IAgonesSdk sdk,
        ILogger<AgonesShutdownService> logger)
    {
        _lifetime = lifetime;
        _sdk = sdk;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registration = _lifetime.ApplicationStopping.Register(OnStopping);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registration.Dispose();
        return Task.CompletedTask;
    }

    private void OnStopping()
    {
        // ApplicationStopping callbacks are synchronous, so block on the call.
        // HttpClient's 2s timeout (set in Program.cs) bounds the wait.
        try
        {
            _sdk.ShutdownAsync(CancellationToken.None).GetAwaiter().GetResult();
            _logger.LogInformation("Agones SDK Shutdown acknowledged.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agones SDK Shutdown call failed during stopping callback.");
        }
    }
}
