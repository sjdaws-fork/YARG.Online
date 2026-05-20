using Xunit;
using YARG.Online.Lobbies.Hubs;

namespace YARG.Online.Lobbies.Tests;

public class ConnectionTrackerTests
{
    [Fact]
    public void SetLobby_records_the_mapping()
    {
        var tracker = new ConnectionTracker();
        tracker.SetLobby("conn-1", "user-1", "lobby-A");

        Assert.Equal("lobby-A", tracker.GetLobby("conn-1"));
        Assert.Equal("user-1", tracker.GetUserId("conn-1"));
    }

    [Fact]
    public void GetLobby_for_unknown_connection_returns_null()
    {
        var tracker = new ConnectionTracker();
        Assert.Null(tracker.GetLobby("nope"));
        Assert.Null(tracker.GetUserId("nope"));
    }

    [Fact]
    public void ClearLobby_removes_the_mapping()
    {
        var tracker = new ConnectionTracker();
        tracker.SetLobby("conn-1", "user-1", "lobby-A");
        tracker.ClearLobby("conn-1");

        Assert.Null(tracker.GetLobby("conn-1"));
    }

    [Fact]
    public void SetLobby_to_same_lobby_is_idempotent()
    {
        var tracker = new ConnectionTracker();
        tracker.SetLobby("conn-1", "user-1", "lobby-A");
        tracker.SetLobby("conn-1", "user-1", "lobby-A");

        Assert.Equal("lobby-A", tracker.GetLobby("conn-1"));
    }

    [Fact]
    public void SetLobby_to_a_different_lobby_throws()
    {
        var tracker = new ConnectionTracker();
        tracker.SetLobby("conn-1", "user-1", "lobby-A");

        Assert.Throws<InvalidOperationException>(() => tracker.SetLobby("conn-1", "user-1", "lobby-B"));
    }
}
