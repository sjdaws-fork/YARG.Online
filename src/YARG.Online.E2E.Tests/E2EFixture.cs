using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using YARG.Online.Game.Auth;
using YARG.Online.Game.Lobbies;
using YARG.Online.Game.Networking;

namespace YARG.Online.E2E.Tests;

public sealed class E2EFixture : IAsyncLifetime
{
    public const string GameSecret = "yarg-e2e-test-secret-must-be-32-bytes-or-more-please";
    public const string GameIssuer = "yarg-server-browser";
    public const string GameAudience = "yarg-game";
    public const string AuthSecret = "yarg-e2e-test-auth-secret-must-be-32-bytes-or-more!";
    public const string AuthIssuer = "yarg-server-browser";
    public const string AuthAudience = "yarg-api";
    public const string ConnectionKey = "yarg-online-game-dev";

    public WebApplicationFactory<Program> Lobbies { get; private set; } = null!;
    public IHost GameHost { get; private set; } = null!;
    public IPEndPoint GameEndpoint { get; private set; } = null!;

    private int _udpPort;

    public Task InitializeAsync()
    {
        _udpPort = GetFreeUdpPort();
        GameEndpoint = new IPEndPoint(IPAddress.Loopback, _udpPort);

        Lobbies = new LobbiesFactory(_udpPort);

        // Force the factory to actually build the host so .Server.CreateHandler() is wired up.
        _ = Lobbies.CreateClient();

        GameHost = BuildGameHost(_udpPort, Lobbies);
        return GameHost.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (GameHost is not null)
        {
            await GameHost.StopAsync();
            GameHost.Dispose();
        }
        Lobbies?.Dispose();
    }

    private IHost BuildGameHost(int udpPort, WebApplicationFactory<Program> lobbiesFactory)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Network:Port"] = udpPort.ToString(),
            ["Network:ConnectionKey"] = ConnectionKey,
            ["Network:MaxConnections"] = "32",
            ["GameAuth:Issuer"] = GameIssuer,
            ["GameAuth:Audience"] = GameAudience,
            ["GameAuth:SigningSecret"] = GameSecret,
            ["GameAuth:ClockSkew"] = "00:00:05",
            // The Lobbies HTTP call is intercepted by the test-server handler below, but BaseAddress
            // still has to be a valid absolute URI for HttpClient to accept relative request paths.
            ["Lobbies:BaseUrl"] = "http://lobbies.test/",
            ["Lobbies:RequestTimeout"] = "00:00:10",
        });

        builder.Services.AddOptions<NetworkOptions>()
            .Bind(builder.Configuration.GetSection(NetworkOptions.SectionName));
        builder.Services.AddOptions<GameAuthOptions>()
            .Bind(builder.Configuration.GetSection(GameAuthOptions.SectionName));
        builder.Services.AddOptions<LobbiesOptions>()
            .Bind(builder.Configuration.GetSection(LobbiesOptions.SectionName));

        builder.Services.AddSingleton<IGameJwtValidator, GameJwtValidator>();
        builder.Services.AddSingleton<AuthenticatedPeerRegistry>();
        builder.Services.AddSingleton<GameSessionManager>();

        builder.Services.AddHttpClient<ILobbiesClient, LobbiesClient>((sp, http) =>
        {
            var opts = sp.GetRequiredService<IOptions<LobbiesOptions>>().Value;
            http.BaseAddress = new Uri(opts.BaseUrl);
            http.Timeout = opts.RequestTimeout;
        })
        .ConfigurePrimaryHttpMessageHandler(() => lobbiesFactory.Server.CreateHandler());

        builder.Services.AddHostedService<GameNetworkService>();

        return builder.Build();
    }

    private static int GetFreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private sealed class LobbiesFactory : WebApplicationFactory<Program>
    {
        private readonly int _udpPort;

        public LobbiesFactory(int udpPort)
        {
            _udpPort = udpPort;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:Issuer"] = AuthIssuer,
                    ["Auth:Audience"] = AuthAudience,
                    ["Auth:SigningSecret"] = AuthSecret,
                    ["GameAuth:Issuer"] = GameIssuer,
                    ["GameAuth:Audience"] = GameAudience,
                    ["GameAuth:SigningSecret"] = GameSecret,
                    ["GameServer:Endpoint"] = $"127.0.0.1:{_udpPort}",
                    ["GameServer:ConnectionKey"] = ConnectionKey,
                });
            });
        }
    }
}
