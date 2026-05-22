namespace YARG.Online.Game.Networking;

public sealed class NetworkOptions
{
    public const string SectionName = "Network";

    public int Port { get; init; } = 9050;

    public int MaxConnections { get; init; } = 32;
}
