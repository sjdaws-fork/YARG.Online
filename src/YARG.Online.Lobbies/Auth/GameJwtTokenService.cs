using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace YARG.Online.Lobbies.Auth;

public sealed class GameJwtTokenService : IGameJwtTokenService
{
    private readonly GameAuthOptions _options;
    private readonly TimeProvider _clock;
    private readonly SigningCredentials _creds;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly ILogger<GameJwtTokenService> _logger;

    public GameJwtTokenService(IOptions<GameAuthOptions> options, TimeProvider clock, ILogger<GameJwtTokenService> logger)
    {
        _options = options.Value;
        _clock = clock;
        _logger = logger;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningSecret!));
        _creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    public IssuedToken IssueGameToken(string userId, string displayName, string lobbyId, int expectedMembers, bool isHost)
    {
        var now = _clock.GetUtcNow();
        var expires = now + _options.TokenLifetime;

        _logger.LogTrace(
            "IssueGameToken: UserId={UserId} DisplayName={DisplayName} LobbyId={LobbyId} ExpectedMembers={ExpectedMembers} IsHost={IsHost} ExpiresAt={ExpiresAt}",
            userId, displayName, lobbyId, expectedMembers, isHost, expires);

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
                ["lobby_id"] = lobbyId,
                ["expected_members"] = expectedMembers,
                ["is_host"] = isHost,
            },
        };

        return new IssuedToken(_handler.CreateToken(descriptor), expires);
    }
}
