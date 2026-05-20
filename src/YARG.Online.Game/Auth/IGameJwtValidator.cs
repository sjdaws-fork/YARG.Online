namespace YARG.Online.Game.Auth;

public interface IGameJwtValidator
{
    GameAuthResult Validate(string token);
}

public sealed record GameAuthResult(
    bool IsValid,
    string? UserId,
    string? DisplayName,
    string? LobbyId,
    int ExpectedMembers,
    bool IsHost,
    string? FailureReason)
{
    public static GameAuthResult Success(string userId, string displayName, string lobbyId, int expectedMembers, bool isHost) =>
        new(true, userId, displayName, lobbyId, expectedMembers, isHost, null);

    public static GameAuthResult Failure(string reason) =>
        new(false, null, null, null, 0, false, reason);
}
