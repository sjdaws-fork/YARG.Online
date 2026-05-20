using Xunit;
using YARG.Online.Game.Networking;

namespace YARG.Online.Game.Tests.Networking;

public class AuthenticatedPeerRegistryTests
{
    [Fact]
    public void TryAdd_then_TryGet_returns_the_stored_identity()
    {
        var registry = new AuthenticatedPeerRegistry();
        var identity = new AuthenticatedPeer("u_alice", "Alice", "lob_test", 1, false);

        Assert.True(registry.TryAdd(7, identity));
        Assert.True(registry.TryGet(7, out var got));
        Assert.Equal(identity, got);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void TryGet_for_unknown_peer_returns_false()
    {
        var registry = new AuthenticatedPeerRegistry();

        Assert.False(registry.TryGet(42, out var got));
        Assert.Null(got);
    }

    [Fact]
    public void TryRemove_clears_the_entry_and_updates_count()
    {
        var registry = new AuthenticatedPeerRegistry();
        registry.TryAdd(1, new AuthenticatedPeer("u_alice", "Alice", "lob_test", 1, false));
        registry.TryAdd(2, new AuthenticatedPeer("u_bob", "Bob", "lob_test", 1, false));

        Assert.True(registry.TryRemove(1, out var removed));
        Assert.Equal("u_alice", removed!.UserId);
        Assert.Equal(1, registry.Count);
        Assert.False(registry.TryGet(1, out _));
        Assert.True(registry.TryGet(2, out _));
    }

    [Fact]
    public void TryAdd_for_existing_peer_returns_false()
    {
        var registry = new AuthenticatedPeerRegistry();
        registry.TryAdd(1, new AuthenticatedPeer("u_alice", "Alice", "lob_test", 1, false));

        Assert.False(registry.TryAdd(1, new AuthenticatedPeer("u_bob", "Bob", "lob_test", 1, false)));

        Assert.True(registry.TryGet(1, out var got));
        Assert.Equal("u_alice", got!.UserId);
    }
}
