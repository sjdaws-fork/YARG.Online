namespace YARG.Online.Lobbies.Auth;

public sealed class GameAuthOptions
{
    public const string SectionName = "GameAuth";

    /// <summary>`iss` claim placed in issued game tokens. Must match what YARG.Online.Game expects.</summary>
    public string Issuer { get; init; } = "";

    /// <summary>`aud` claim placed in issued game tokens. Distinct from the Lobbies auth audience so the two JWT systems are isolated.</summary>
    public string Audience { get; init; } = "";

    /// <summary>HMAC SHA-256 shared secret. Must be at least 32 bytes when UTF-8 encoded. Shared with YARG.Online.Game.</summary>
    public string? SigningSecret { get; init; }

    public TimeSpan TokenLifetime { get; init; } = TimeSpan.FromMinutes(15);
}
