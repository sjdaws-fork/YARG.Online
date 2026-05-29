using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace YARG.Online.Lobbies.Auth;

public sealed class JwtTokenService : IJwtTokenService
{
    private readonly AuthOptions _options;
    private readonly TimeProvider _clock;
    private readonly SigningCredentials _creds;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly ILogger<JwtTokenService> _logger;

    public JwtTokenService(IOptions<AuthOptions> options, TimeProvider clock, ILogger<JwtTokenService> logger)
    {
        _options = options.Value;
        _clock = clock;
        _logger = logger;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningSecret!));
        _creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    public IssuedToken IssueAuthToken(string userId, string displayName, string? clientVersion)
    {
        var now = _clock.GetUtcNow();
        var expires = now + _options.AuthTokenLifetime;

        _logger.LogTrace(
            "IssueAuthToken: UserId={UserId} DisplayName={DisplayName} ClientVersion={ClientVersion} ExpiresAt={ExpiresAt}",
            userId, displayName, clientVersion, expires);

        var claims = new Dictionary<string, object>
        {
            ["sub"] = userId,
            ["name"] = displayName,
            ["auth_mode"] = "dev",
        };

        // TODO(version-gate): client_version is stamped here so LobbyHub.OnConnectedAsync
        // can reject outdated clients with Context.Abort(). Remove this claim (and the
        // hub-side check) once enough prod clients ship a build that handles the
        // 426 ClientUpdateRequiredException at auth time.
        if (!string.IsNullOrEmpty(clientVersion))
        {
            claims["client_version"] = clientVersion!;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = _creds,
            Claims = claims,
        };

        return new IssuedToken(_handler.CreateToken(descriptor), expires);
    }
}
