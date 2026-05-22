namespace YARG.Online.Lobbies.Lobbies;

public sealed class GameServerOptions
{
    public const string SectionName = "GameServer";

    /// <summary>Externally-reachable address of the YARG.Online.Game UDP server,
    /// formatted as "host:port" (e.g. "game.example.com:9050"). Sent to clients
    /// in OnGameStarted so they know where to connect after the host starts a game.</summary>
    public string Endpoint { get; init; } = "";
}
