namespace YARG.Online.Lobbies.Allocation;

public sealed class AgonesOptions
{
    public const string SectionName = "Agones";

    /// <summary>Namespace the Fleet and its GameServers live in (e.g. "default").
    /// The allocator POSTs GameServerAllocations and PATCHes GameServers in this namespace.</summary>
    public string Namespace { get; init; } = "";

    /// <summary>Value of the <c>agones.dev/fleet</c> label used to scope allocations.</summary>
    public string FleetName { get; init; } = "";

    /// <summary>Key of the capacity list on each GameServer. Defaults to "slots"
    /// per the reference architecture.</summary>
    public string SlotsKey { get; init; } = "slots";
}
