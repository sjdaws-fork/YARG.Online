using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentValidation;
using k8s;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Allocation;
using YARG.Online.Lobbies.Auth;
using YARG.Online.Lobbies.Endpoints;
using YARG.Online.Lobbies.Hubs;
using YARG.Online.Lobbies.Lobbies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer), "Auth:Issuer is required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Audience), "Auth:Audience is required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.SigningSecret), "Auth:SigningSecret is required.")
    .Validate(
        o => string.IsNullOrEmpty(o.SigningSecret) || Encoding.UTF8.GetByteCount(o.SigningSecret) >= 32,
        "Auth:SigningSecret must be at least 32 UTF-8 bytes for HS256.")
    .ValidateOnStart();

builder.Services.AddOptions<GameAuthOptions>()
    .Bind(builder.Configuration.GetSection(GameAuthOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Issuer), "GameAuth:Issuer is required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.Audience), "GameAuth:Audience is required.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.SigningSecret), "GameAuth:SigningSecret is required.")
    .Validate(
        o => string.IsNullOrEmpty(o.SigningSecret) || Encoding.UTF8.GetByteCount(o.SigningSecret) >= 32,
        "GameAuth:SigningSecret must be at least 32 UTF-8 bytes for HS256.")
    .ValidateOnStart();

builder.Services.AddOptions<LobbyOptions>()
    .Bind(builder.Configuration.GetSection(LobbyOptions.SectionName));

// In-cluster lobby pods talk to Agones for capacity-aware allocation; out-of-cluster
// (local dev) just reuses a static endpoint. The allocator is only responsible
// for the endpoint.
if (KubernetesClientConfiguration.IsInCluster())
{
    builder.Services.AddOptions<AgonesOptions>()
        .Bind(builder.Configuration.GetSection(AgonesOptions.SectionName))
        .Validate(o => !string.IsNullOrWhiteSpace(o.Namespace), "Agones:Namespace is required.")
        .Validate(o => !string.IsNullOrWhiteSpace(o.FleetName), "Agones:FleetName is required.")
        .Validate(o => !string.IsNullOrWhiteSpace(o.SlotsKey), "Agones:SlotsKey is required.")
        .ValidateOnStart();

    builder.Services.AddOptions<GameServerOptions>()
        .Bind(builder.Configuration.GetSection(GameServerOptions.SectionName));

    builder.Services.AddSingleton<IKubernetes>(_ =>
        new Kubernetes(KubernetesClientConfiguration.InClusterConfig()));
    builder.Services.AddSingleton<IGameAllocator, AgonesGameAllocator>();
    builder.Services.AddSingleton<IGameEndedHandler, AgonesGameEndedHandler>();
}
else
{
    builder.Services.AddOptions<GameServerOptions>()
        .Bind(builder.Configuration.GetSection(GameServerOptions.SectionName))
        .Validate(o => !string.IsNullOrWhiteSpace(o.Endpoint), "GameServer:Endpoint is required.")
        .Validate(o => string.IsNullOrEmpty(o.Endpoint) || o.Endpoint.Contains(':'),
            "GameServer:Endpoint must be in 'host:port' form.")
        .ValidateOnStart();

    builder.Services.AddSingleton<IGameAllocator, StaticGameAllocator>();
    builder.Services.AddSingleton<IGameEndedHandler, NoOpGameEndedHandler>();
}

builder.Services.AddHealthChecks();

builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<IJwtTokenService>(sp => sp.GetRequiredService<JwtTokenService>());
builder.Services.AddSingleton<IGameJwtTokenService, GameJwtTokenService>();
builder.Services.AddSingleton<ILobbyIdGenerator, Base32LobbyIdGenerator>();
builder.Services.AddSingleton<ILobbyRepository, InMemoryLobbyRepository>();
builder.Services.AddSingleton<IConnectionTracker, ConnectionTracker>();
builder.Services.AddSingleton<ILobbyChangeBuffer, LobbyChangeBuffer>();
builder.Services.AddHostedService<LobbyBroadcastService>();

builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddAutoMapper(typeof(Program).Assembly);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.Events = new JwtBearerEvents
        {
            // WebSocket upgrade requests can't carry an Authorization header from browser clients,
            // so SignalR's convention is to read the JWT from ?access_token= on the hub path.
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken)
                    && path.StartsWithSegments(HubRoutes.Lobby))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
        };
    });

builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<AuthOptions>>((opts, authOptions) =>
    {
        var a = authOptions.Value;
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = a.Issuer,
            ValidateAudience = true,
            ValidAudience = a.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(a.SigningSecret!)),
            NameClaimType = "name",
            RoleClaimType = "role",
            ClockSkew = a.ClockSkew,
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 32 * 1024 * 1024; // 32 MB
}).AddJsonProtocol(o =>
{
    o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.PayloadSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("dev-auth", context =>
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    });
});

builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info = new OpenApiInfo
        {
            Title = "YARG.Online.Lobbies HTTP API",
            Version = "v0",
            Description = "Hosting and discovery for YARG multiplayer lobbies.",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes["bearerAuth"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "JWT issued by POST /api/v1/auth/dev. Send as `Authorization: Bearer <token>`.",
        };
        return Task.CompletedTask;
    });

    // Tag operations whose endpoint requires authorization so the generated client
    // (and Swagger UI) know to send the bearer token.
    options.AddOperationTransformer((operation, context, _) =>
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var requiresAuth = metadata.OfType<IAuthorizeData>().Any()
                           && !metadata.OfType<IAllowAnonymous>().Any();
        if (requiresAuth)
        {
            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "bearerAuth",
                    },
                }] = Array.Empty<string>(),
            });
        }
        return Task.CompletedTask;
    });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi("/openapi/v1.json");

// Probes — these stay anonymous so kubelet can reach them without a JWT.
app.MapHealthChecks("/healthz").AllowAnonymous();
app.MapHealthChecks("/readyz").AllowAnonymous();

app.MapAuthEndpoints(app.Environment);
app.MapGameLifecycleEndpoints();

app.MapHub<LobbyHub>(HubRoutes.Lobby).RequireAuthorization();

app.MapGet("/", () => Results.Redirect("/openapi/v1.json")).ExcludeFromDescription();

app.Run();

public partial class Program;
