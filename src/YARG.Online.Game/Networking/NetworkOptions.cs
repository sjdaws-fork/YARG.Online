namespace YARG.Online.Game.Networking;

public sealed class NetworkOptions
{
    public const string SectionName = "Network";

    public int Port { get; init; } = 9050;

    public string ConnectionKey { get; init; } = "yarg-online-game-dev";

    public int MaxConnections { get; init; } = 32;
}
