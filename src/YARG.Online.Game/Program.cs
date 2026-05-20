using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using YARG.Online.Game.Auth;
using YARG.Online.Game.Lobbies;
using YARG.Online.Game.Networking;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<NetworkOptions>()
    .Bind(builder.Configuration.GetSection(NetworkOptions.SectionName));

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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IGameJwtValidator, GameJwtValidator>();
builder.Services.AddSingleton<AuthenticatedPeerRegistry>();
builder.Services.AddSingleton<GameSessionManager>();

builder.Services.AddHttpClient<ILobbiesClient, LobbiesClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<IOptions<LobbiesOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl.EndsWith('/') ? opts.BaseUrl : opts.BaseUrl + "/");
    http.Timeout = opts.RequestTimeout;
});

builder.Services.AddHostedService<GameNetworkService>();

builder.Build().Run();
