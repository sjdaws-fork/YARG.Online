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

    public IssuedToken IssueAuthToken(string userId, string displayName)
    {
        var now = _clock.GetUtcNow();
        var expires = now + _options.AuthTokenLifetime;

        _logger.LogTrace(
            "IssueAuthToken: UserId={UserId} DisplayName={DisplayName} ExpiresAt={ExpiresAt}",
            userId, displayName, expires);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = _creds,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = userId,
                ["name"] = displayName,
                ["auth_mode"] = "dev",
            },
        };

        return new IssuedToken(_handler.CreateToken(descriptor), expires);
    }
}
