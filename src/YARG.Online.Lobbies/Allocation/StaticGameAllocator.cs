using Microsoft.Extensions.Options;
using YARG.Online.Lobbies.Lobbies;

namespace YARG.Online.Lobbies.Allocation;

/// <summary>
/// Returns the statically-configured <see cref="GameServerOptions.Endpoint"/> on every
/// allocation. Used out-of-cluster where there is no Agones fleet to draw from.
/// </summary>
public sealed class StaticGameAllocator : IGameAllocator
{
    private readonly string _endpoint;

    public StaticGameAllocator(IOptions<GameServerOptions> options)
    {
        _endpoint = options.Value.Endpoint;
    }

    public Task<GameAllocation> AllocateAsync(int playerCount, CancellationToken ct)
        => Task.FromResult(new GameAllocation(_endpoint, GameServerName: null, SlotCount: playerCount));
}
