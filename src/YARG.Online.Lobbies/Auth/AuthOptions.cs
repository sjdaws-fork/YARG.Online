namespace YARG.Online.Lobbies.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>`iss` claim placed in issued auth tokens.</summary>
    public string Issuer { get; init; } = "";

    /// <summary>`aud` claim placed in issued auth tokens. Distinct from the Game audience so the two JWT systems are isolated.</summary>
    public string Audience { get; init; } = "";

    /// <summary>HMAC SHA-256 shared secret. Must be at least 32 bytes when UTF-8 encoded.</summary>
    public string? SigningSecret { get; init; }

    public TimeSpan AuthTokenLifetime { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(1);
}
