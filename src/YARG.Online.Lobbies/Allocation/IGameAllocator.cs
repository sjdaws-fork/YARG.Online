namespace YARG.Online.Lobbies.Allocation;

/// <summary>
/// Resolves a UDP game-server endpoint for a starting match. Implementations may reuse
/// a single static endpoint (local dev) or dynamically reserve capacity on an Agones
/// fleet. The allocator is only responsible for the endpoint; the LiteNetLib
/// connection key is sourced separately from <see cref="GameServerOptions"/>.
/// </summary>
public interface IGameAllocator
{
    Task<GameAllocation> AllocateAsync(int playerCount, CancellationToken ct);
}
