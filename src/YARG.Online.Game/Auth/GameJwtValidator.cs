using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace YARG.Online.Game.Auth;

public sealed class GameJwtValidator : IGameJwtValidator
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly TokenValidationParameters _parameters;
    private readonly ILogger<GameJwtValidator> _logger;

    public GameJwtValidator(IOptions<GameAuthOptions> options, ILogger<GameJwtValidator> logger)
    {
        _logger = logger;
        var opts = options.Value;
        var keyBytes = Encoding.UTF8.GetBytes(opts.SigningSecret ?? string.Empty);

        _parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = opts.Issuer,
            ValidateAudience = true,
            ValidAudience = opts.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            NameClaimType = "name",
            RoleClaimType = "role",
            ClockSkew = opts.ClockSkew,
        };
    }

    public GameAuthResult Validate(string token)
    {
        _logger.LogTrace("Validate: TokenLength={TokenLength}", token?.Length ?? 0);

        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogTrace("Validate failure: Reason=empty token");
            return GameAuthResult.Failure("empty token");
        }

        // ValidateTokenAsync is CPU-bound (HMAC verify); blocking on the LiteNetLib poll thread is fine.
        var result = _handler.ValidateTokenAsync(token, _parameters).GetAwaiter().GetResult();

        if (!result.IsValid)
        {
            var reason = result.Exception?.Message ?? "invalid token";
            _logger.LogTrace("Validate failure: Reason={Reason}", reason);
            return GameAuthResult.Failure(reason);
        }

        var sub = result.Claims.TryGetValue("sub", out var subClaim) ? subClaim?.ToString() : null;
        if (string.IsNullOrWhiteSpace(sub))
        {
            _logger.LogTrace("Validate failure: Reason=token missing sub claim");
            return GameAuthResult.Failure("token missing sub claim");
        }

        var lobbyId = result.Claims.TryGetValue("lobby_id", out var lobbyClaim) ? lobbyClaim?.ToString() : null;
        if (string.IsNullOrWhiteSpace(lobbyId))
        {
            _logger.LogTrace("Validate failure: Reason=token missing lobby_id claim Sub={Sub}", sub);
            return GameAuthResult.Failure("token missing lobby_id claim");
        }

        if (!result.Claims.TryGetValue("expected_members", out var expectedClaim)
            || !TryParseExpectedMembers(expectedClaim, out var expectedMembers)
            || expectedMembers <= 0)
        {
            _logger.LogTrace(
                "Validate failure: Reason=token missing or invalid expected_members claim Sub={Sub} LobbyId={LobbyId}",
                sub, lobbyId);
            return GameAuthResult.Failure("token missing or invalid expected_members claim");
        }

        var isHost = result.Claims.TryGetValue("is_host", out var isHostClaim)
            && TryParseIsHost(isHostClaim);

        var name = result.Claims.TryGetValue("name", out var nameClaim) ? nameClaim?.ToString() : null;
        var displayName = string.IsNullOrWhiteSpace(name) ? sub : name!;

        _logger.LogTrace(
            "Validate success: Sub={Sub} DisplayName={DisplayName} LobbyId={LobbyId} ExpectedMembers={ExpectedMembers} IsHost={IsHost}",
            sub, displayName, lobbyId, expectedMembers, isHost);

        return GameAuthResult.Success(
            sub,
            displayName,
            lobbyId!,
            expectedMembers,
            isHost);
    }

    private static bool TryParseExpectedMembers(object? claim, out int value)
    {
        switch (claim)
        {
            case int i:
                value = i;
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                value = (int)l;
                return true;
            default:
                return int.TryParse(claim?.ToString(), out value);
        }
    }

    private static bool TryParseIsHost(object? claim)
    {
        return claim switch
        {
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            string s => bool.TryParse(s, out var parsed)
                ? parsed
                : (int.TryParse(s, out var asInt) && asInt != 0),
            _ => false,
        };
    }
}
