namespace YARG.Online.Game.Observability;

/// <summary>
/// Binds the <c>Observability</c> section of configuration. Controls the
/// Kestrel listener that serves the Prometheus scrape endpoint and the
/// /healthz probe; the UDP game listener is unaffected.
/// </summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>
    /// TCP port the metrics + health Kestrel listener binds to. Defaults to 9091.
    /// </summary>
    public int MetricsPort { get; init; } = 9091;
}
