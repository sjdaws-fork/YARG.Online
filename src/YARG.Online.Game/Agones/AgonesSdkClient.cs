using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace YARG.Online.Game.Agones;

/// <summary>
/// Talks to the Agones SDK sidecar over its local HTTP listener (default
/// 127.0.0.1:9358). Only the two calls the architecture requires — Ready and
/// Health — are implemented. Health swallows transient failures so the periodic
/// pinger doesn't tear itself down on a single dropped request.
/// </summary>
public sealed class AgonesSdkClient : IAgonesSdk
{
    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json");
    private const string EmptyJsonBody = "{}";

    private readonly HttpClient _http;
    private readonly ILogger<AgonesSdkClient> _logger;

    public AgonesSdkClient(HttpClient http, ILogger<AgonesSdkClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task ReadyAsync(CancellationToken ct)
    {
        using var response = await PostEmptyAsync("ready", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task HealthAsync(CancellationToken ct)
    {
        try
        {
            using var response = await PostEmptyAsync("health", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agones SDK /health ping failed.");
        }
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        // Tolerate failures: this fires during app teardown and we don't want a
        // dead sidecar to block process exit. The 2s HttpClient timeout bounds it.
        try
        {
            using var response = await PostEmptyAsync("shutdown", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agones SDK /shutdown call failed.");
        }
    }

    private Task<HttpResponseMessage> PostEmptyAsync(string path, CancellationToken ct)
    {
        var content = new StringContent(EmptyJsonBody, Encoding.UTF8);
        content.Headers.ContentType = JsonContentType;
        return _http.PostAsync(path, content, ct);
    }
}
