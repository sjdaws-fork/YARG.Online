namespace YARG.Online.Lobbies.Allocation;

/// <summary>
/// Result of a game-server allocation. <see cref="Endpoint"/> is the externally-reachable
/// "host:port" players will connect to. <see cref="GameServerName"/> is the Agones
/// GameServer resource name (null for static allocators) — needed by
/// <see cref="IGameEndedHandler"/> to release the reserved slots on match end.
/// <see cref="SlotCount"/> is the number of slots reserved on the GameServer's
/// capacity list at allocation time.
/// </summary>
public sealed record GameAllocation(string Endpoint, string? GameServerName, int SlotCount);
