namespace YARG.Online.Lobbies.Auth;

public interface IJwtTokenService
{
    IssuedToken IssueAuthToken(string userId, string displayName);
}

public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt);
