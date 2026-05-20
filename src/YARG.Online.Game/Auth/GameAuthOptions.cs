namespace YARG.Online.Game.Auth;

public sealed class GameAuthOptions
{
    public const string SectionName = "GameAuth";

    /// <summary>Expected `iss` claim. Must match what the token issuer service uses for game tokens.</summary>
    public string Issuer { get; init; } = "";

    /// <summary>Expected `aud` claim. Distinct from the Lobbies audience so the two JWT systems are isolated.</summary>
    public string Audience { get; init; } = "";

    /// <summary>HMAC SHA-256 shared secret. Must be at least 32 bytes when UTF-8 encoded.</summary>
    public string? SigningSecret { get; init; }

    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(1);
}
