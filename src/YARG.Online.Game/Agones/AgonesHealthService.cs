using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace YARG.Online.Game.Agones;

/// <summary>
/// Periodically POSTs to the Agones SDK sidecar's /health endpoint. The sidecar
/// marks the GameServer Unhealthy if it doesn't see a ping inside its own
/// periodSeconds*failureThreshold window, so this loop ticks well inside that
/// budget (default 2s vs. fleet's 10s/3-failure default).
/// </summary>
public sealed class AgonesHealthService : BackgroundService
{
    private readonly AgonesOptions _options;
    private readonly IAgonesSdk _sdk;
    private readonly ILogger<AgonesHealthService> _logger;

    public AgonesHealthService(
        IOptions<AgonesOptions> options,
        IAgonesSdk sdk,
        ILogger<AgonesHealthService> logger)
    {
        _options = options.Value;
        _sdk = sdk;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.HealthInterval);
        _logger.LogInformation(
            "Agones SDK health pinger started (interval={Interval}).", _options.HealthInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await _sdk.HealthAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
