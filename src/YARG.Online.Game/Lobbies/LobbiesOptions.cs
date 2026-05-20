namespace YARG.Online.Game.Lobbies;

public sealed class LobbiesOptions
{
    public const string SectionName = "Lobbies";

    public string BaseUrl { get; init; } = "";

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
