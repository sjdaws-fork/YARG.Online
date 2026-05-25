#!/usr/bin/env pwsh
# Fetches Grafana dashboard JSON for the observability stack.
# Output goes to infra/dashboards/ (gitignored).
# Re-run any time to refresh to the latest published revision.

$ErrorActionPreference = 'Stop'

$dashboards = @(
    # Envoy Gateway (raw from envoyproxy/gateway@main — tuned to Envoy Gateway's label set)
    @{ Url = 'https://raw.githubusercontent.com/envoyproxy/gateway/main/charts/gateway-addons-helm/dashboards/envoy-clusters.json';        File = 'envoy-gateway-clusters.json' }
    @{ Url = 'https://raw.githubusercontent.com/envoyproxy/gateway/main/charts/gateway-addons-helm/dashboards/envoy-proxy-global.json';    File = 'envoy-gateway-proxy-global.json' }
    @{ Url = 'https://raw.githubusercontent.com/envoyproxy/gateway/main/charts/gateway-addons-helm/dashboards/envoy-gateway-global.json';  File = 'envoy-gateway-global.json' }
    @{ Url = 'https://raw.githubusercontent.com/envoyproxy/gateway/main/charts/gateway-addons-helm/dashboards/global-ratelimit.json';      File = 'envoy-gateway-ratelimit.json' }
    @{ Url = 'https://raw.githubusercontent.com/envoyproxy/gateway/main/charts/gateway-addons-helm/dashboards/resources-monitor.gen.json'; File = 'envoy-gateway-resources.json' }

    # Agones
    @{ Id = 12141; File = 'agones-allocations.json' }
    @{ Id = 12142; File = 'agones-allocator-resource.json' }
    @{ Id = 12143; File = 'agones-autoscalers.json' }
    @{ Id = 12144; File = 'agones-controller-api-server-requests.json' }
    @{ Id = 12148; File = 'agones-controller-resource-usage.json' }
    @{ Id = 12149; File = 'agones-gameservers.json' }
    @{ Id = 12150; File = 'agones-status.json' }

    # Cloudflared
    @{ Id = 24874; File = 'cloudflare-tunnel.json' }

    # ASP.NET Core (Microsoft official) + .NET 9 runtime
    @{ Id = 19924; File = 'aspnetcore.json' }
    @{ Id = 19925; File = 'aspnetcore-endpoint.json' }
    @{ Id = 23178; File = 'kestrel.json' }
    @{ Id = 23179; File = 'dotnet-runtime.json' }
)

$outDir = Join-Path $PSScriptRoot 'infra/dashboards'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# Drop dashboards that were renamed/removed in a previous revision of this script so
# the working dir stays in sync with the list above.
$expected = $dashboards | ForEach-Object { $_.File }
Get-ChildItem -Path $outDir -Filter *.json -File | Where-Object { $expected -notcontains $_.Name } | ForEach-Object {
    Write-Host "Removing stale $($_.Name)"
    Remove-Item -LiteralPath $_.FullName -Force
}

foreach ($d in $dashboards) {
    $url = if ($d.ContainsKey('Url')) { $d.Url }
           else { "https://grafana.com/api/dashboards/$($d.Id)/revisions/latest/download" }
    $out = Join-Path $outDir $d.File
    Write-Host "Downloading $($d.File)"
    Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing

    # Substitute the import-time datasource placeholder with the real Prometheus
    # UID. grafana.com dashboards (and a few upstream chart dashboards) ship with
    # `"uid": "${DS_PROMETHEUS}"` expecting Grafana's import wizard to fill it
    # in; the sidecar that loads our ConfigMap dashboards does not — leaving
    # every panel with a "Datasource ${DS_PROMETHEUS} not found" error. The
    # kube-prometheus-stack chart provisions the Prometheus datasource with the
    # stable UID `prometheus`, so a literal replace is safe and idempotent.
    $contents = Get-Content -LiteralPath $out -Raw
    $patched = $contents.Replace('${DS_PROMETHEUS}', 'prometheus')
    if ($patched -ne $contents) {
        Set-Content -LiteralPath $out -Value $patched -NoNewline
    }
}

Write-Host ""
Write-Host "Done. $($dashboards.Count) dashboards in $outDir"
