namespace YARG.Online.Lobbies.Auth;

public interface IGameJwtTokenService
{
    IssuedToken IssueGameToken(string userId, string displayName, string lobbyId, int expectedMembers, bool isHost);
}
