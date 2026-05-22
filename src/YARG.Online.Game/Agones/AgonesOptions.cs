namespace YARG.Online.Game.Agones;

public sealed class AgonesOptions
{
    public const string SectionName = "Agones";

    public bool Enabled { get; init; }

    public string SdkHost { get; init; } = "127.0.0.1";

    public int SdkHttpPort { get; init; } = 9358;

    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(2);
}
