using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace YARG.Online.Game.Agones;

/// <summary>
/// Waits for the network service to bind, then POSTs to the Agones SDK
/// sidecar's /ready endpoint so the GameServer transitions Ready → eligible
/// for allocation. Retries a few times on transient failure (sidecar may not be
/// up the very first millisecond); gives up after that and lets the sidecar's
/// health timeout do its job.
/// </summary>
public sealed class AgonesReadyService : BackgroundService
{
    private static readonly TimeSpan[] RetryBackoffs =
    {
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    };

    private readonly AgonesReadinessSignal _signal;
    private readonly IAgonesSdk _sdk;
    private readonly ILogger<AgonesReadyService> _logger;

    public AgonesReadyService(
        AgonesReadinessSignal signal,
        IAgonesSdk sdk,
        ILogger<AgonesReadyService> logger)
    {
        _signal = signal;
        _sdk = sdk;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);

        for (var attempt = 0; attempt <= RetryBackoffs.Length; attempt++)
        {
            try
            {
                await _sdk.ReadyAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Agones SDK Ready acknowledged.");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (attempt < RetryBackoffs.Length)
            {
                _logger.LogWarning(
                    ex,
                    "Agones SDK Ready attempt {Attempt} failed; retrying in {Delay}.",
                    attempt + 1,
                    RetryBackoffs[attempt]);
                await Task.Delay(RetryBackoffs[attempt], stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agones SDK Ready failed after {Attempts} attempts; giving up.", attempt);
                return;
            }
        }
    }
}
