using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace YARG.Online.Game.Tests;

internal static class TestJwt
{
    public const string DefaultSecret = "test-secret-must-be-32-bytes-or-more-please";
    public const string DefaultIssuer = "yarg-server-browser";
    public const string DefaultAudience = "yarg-game";

    public static string Mint(
        string userId = "u_alice",
        string displayName = "Alice",
        string lobbyId = "lob_test",
        int expectedMembers = 1,
        bool isHost = false,
        string secret = DefaultSecret,
        string issuer = DefaultIssuer,
        string audience = DefaultAudience,
        TimeSpan? lifetime = null,
        DateTimeOffset? now = null,
        IDictionary<string, object>? extraClaims = null)
    {
        var nowUtc = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new Dictionary<string, object>
        {
            ["sub"] = userId,
            ["name"] = displayName,
            ["lobby_id"] = lobbyId,
            ["expected_members"] = expectedMembers,
            ["is_host"] = isHost,
        };
        if (extraClaims is not null)
        {
            foreach (var (k, v) in extraClaims)
            {
                claims[k] = v;
            }
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = nowUtc,
            NotBefore = nowUtc,
            Expires = nowUtc + (lifetime ?? TimeSpan.FromMinutes(5)),
            SigningCredentials = creds,
            Claims = claims,
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
