using System.Text;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using YARG.Online.Game.Agones;
using YARG.Online.Game.Auth;
using YARG.Online.Game.Lobbies;
using YARG.Online.Game.Networking;
using YARG.Online.Game.Observability;

// WebApplication so we can host a tiny Kestrel listener for /metrics + /healthz
// alongside the UDP GameNetworkService. The UDP listener lives on port 9050
// (LiteNetLib); Kestrel binds to a separate port for observability only.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ObservabilityOptions>()
    .Bind(builder.Configuration.GetSection(ObservabilityOptions.SectionName))
    .Validate(o => o.MetricsPort is > 0 and < 65536,
        "Observability:MetricsPort must be in (0, 65536).")
    .ValidateOnStart();

var observability = builder.Configuration
    .GetSection(ObservabilityOptions.SectionName)
    .Get<ObservabilityOptions>() ?? new ObservabilityOptions();

// UseUrls fully replaces the default ASPNETCORE_URLS / localhost:5000 binding —
// the only Kestrel listener is the metrics port. The UDP game listener is a
// separate stack inside GameNetworkService and is unaffected.
builder.WebHost.UseUrls($"http://0.0.0.0:{observability.MetricsPort}");

builder.Services.AddOptions<NetworkOptions>()
    .Bind(builder.Configuration.GetSection(NetworkOptions.SectionName));

builder.Services.AddOptions<LatencySimulatorOptions>()
    .Bind(builder.Configuration.GetSection(LatencySimulatorOptions.SectionName))
    .Validate(o => o.DelayMs >= 0, "LatencySimulator:DelayMs must be non-negative.")
    .Validate(o => o.JitterMs >= 0, "LatencySimulator:JitterMs must be non-negative.")
    .Validate(o => o.LossPercent is >= 0 and <= 100,
        "LatencySimulator:LossPercent must be in [0,100].");

builder.Services.AddOptions<GameAuthOptions>()
    .Bind(builder.Configuration.GetSection(GameAuthOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer), "GameAuth:Issuer is required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Audience), "GameAuth:Audience is required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.SigningSecret), "GameAuth:SigningSecret is required.")
    .Validate(
        o => string.IsNullOrEmpty(o.SigningSecret) || Encoding.UTF8.GetByteCount(o.SigningSecret) >= 32,
        "GameAuth:SigningSecret must be at least 32 UTF-8 bytes for HS256.")
    .ValidateOnStart();

builder.Services.AddOptions<LobbiesOptions>()
    .Bind(builder.Configuration.GetSection(LobbiesOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.BaseUrl), "Lobbies:BaseUrl is required.")
    .Validate(
        o => string.IsNullOrEmpty(o.BaseUrl)
            || Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _),
        "Lobbies:BaseUrl must be an absolute URI.")
    .ValidateOnStart();

builder.Services.AddOptions<AgonesOptions>()
    .Bind(builder.Configuration.GetSection(AgonesOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IGameJwtValidator, GameJwtValidator>();
builder.Services.AddSingleton<AuthenticatedPeerRegistry>();
builder.Services.AddSingleton<GameSessionManager>();
builder.Services.AddSingleton<AgonesReadinessSignal>();

// Relay sender: pass-through by default, replaced with the delaying impl
// when the LatencySimulator is enabled. Resolved at startup, not per-send.
builder.Services.AddSingleton<IRelaySender>(sp =>
{
    var simOpts = sp.GetRequiredService<IOptions<LatencySimulatorOptions>>().Value;
    if (!simOpts.Enabled)
    {
        return new PassthroughRelaySender();
    }
    return ActivatorUtilities.CreateInstance<DelayingRelaySender>(sp);
});

builder.Services.AddHttpClient<ILobbiesClient, LobbiesClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<LobbiesOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl.EndsWith('/') ? opts.BaseUrl : opts.BaseUrl + "/");
    http.Timeout = opts.RequestTimeout;
});

// In-cluster pods get Agones__Enabled=true from the Fleet pod template; local
// dotnet run doesn't set it, so the SDK pinger never tries to reach a sidecar
// that isn't there.
var agonesEnabled = builder.Configuration.GetValue("Agones:Enabled", defaultValue: false);
if (agonesEnabled)
{
    builder.Services.AddHttpClient<IAgonesSdk, AgonesSdkClient>((sp, http) =>
    {
        var opts = sp.GetRequiredService<IOptions<AgonesOptions>>().Value;
        // Agones auto-injects AGONES_SDK_HTTP_PORT on every game container. Prefer it
        // over the configured default so an operator override of sdkServer.httpPort in
        // the Fleet spec is respected without a config change here.
        var port = int.TryParse(Environment.GetEnvironmentVariable("AGONES_SDK_HTTP_PORT"), out var envPort)
            ? envPort
            : opts.SdkHttpPort;
        http.BaseAddress = new Uri($"http://{opts.SdkHost}:{port}/");
        http.Timeout = TimeSpan.FromSeconds(2);
    });

    builder.Services.AddHostedService<AgonesReadyService>();
    builder.Services.AddHostedService<AgonesHealthService>();
    builder.Services.AddHostedService<AgonesShutdownService>();
}

builder.Services.AddHostedService<GameNetworkService>();

// OpenTelemetry metrics. Exposed on the Kestrel side listener only — no
// AspNetCoreInstrumentation: there's no public HTTP surface to measure, and
// instrumenting the scrape endpoint would pollute dashboards with its own
// scrape requests.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("yarg-online-game"))
    .WithMetrics(m => m
        .AddMeter("System.Net.Http")
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();
app.MapPrometheusScrapingEndpoint("/metrics");
app.MapGet("/healthz", () => Results.Ok());
app.Run();
