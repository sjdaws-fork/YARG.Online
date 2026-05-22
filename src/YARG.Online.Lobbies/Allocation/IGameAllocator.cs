namespace YARG.Online.Lobbies.Allocation;

/// <summary>
/// Resolves a UDP game-server endpoint for a starting match. Implementations may reuse
/// a single static endpoint (local dev) or dynamically reserve capacity on an Agones
/// fleet. The allocator is only responsible for the endpoint.
/// </summary>
public interface IGameAllocator
{
    Task<GameAllocation> AllocateAsync(int playerCount, CancellationToken ct);
}
