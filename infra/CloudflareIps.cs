using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace YARG.Online.Infrastructure;

/// <summary>
/// Fetches Cloudflare's published proxy IP ranges from
/// <c>https://api.cloudflare.com/client/v4/ips</c> so the NLB NSG can be
/// pinned to "Cloudflare origin traffic only" — closing the bypass-Cloudflare
/// path against the NLB's reserved public IP.
///
/// Synchronous on purpose: the result is consumed at Pulumi resource
/// construction time, not as an <see cref="Output{T}"/>. Cloudflare's IP
/// list changes ~once a year; periodic <c>pulumi up</c> picks up updates.
/// </summary>
public static class CloudflareIps
{
    private const string Endpoint = "https://api.cloudflare.com/client/v4/ips";

    public sealed record Ranges(IReadOnlyList<string> V4, IReadOnlyList<string> V6);

    /// <summary>
    /// Fetches the current Cloudflare proxy IPv4 and IPv6 CIDR lists.
    /// Throws on any failure (network, non-200, empty result, parse error)
    /// so <c>pulumi up</c> fails loudly rather than silently producing an
    /// NSG that accepts no traffic.
    /// </summary>
    public static Ranges Fetch()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var response = http.GetAsync(Endpoint, cts.Token).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var json = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            throw new InvalidOperationException(
                $"Cloudflare /ips API returned success=false. Body: {json}");
        }

        var result = root.GetProperty("result");
        var v4 = ReadCidrList(result, "ipv4_cidrs");
        var v6 = ReadCidrList(result, "ipv6_cidrs");

        if (v4.Count == 0 || v6.Count == 0)
        {
            throw new InvalidOperationException(
                "Cloudflare /ips API returned an empty IPv4 or IPv6 list — refusing " +
                "to emit NSG rules from a degenerate response.");
        }

        return new Ranges(v4, v6);
    }

    private static List<string> ReadCidrList(JsonElement parent, string key)
    {
        var arr = parent.GetProperty(key);
        var list = new List<string>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            var cidr = item.GetString();
            if (!string.IsNullOrWhiteSpace(cidr))
                list.Add(cidr);
        }
        return list;
    }
}
