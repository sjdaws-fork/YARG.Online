using Xunit;
using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Online.Lobbies.Lobbies;

namespace YARG.Online.Lobbies.Tests;

public class LobbyChangeBufferTests
{
    private static LobbyDto Dto(string id, int playerCount = 1) => new(
        Id: id,
        Name: "name",
        HostUserId: "u",
        HostName: "u",
        GameMode: GameMode.Band,
        Region: Region.UsEast,
        Song: "song",
        PlayerCount: playerCount,
        MaxPlayers: 4,
        CreatedAt: DateTimeOffset.UnixEpoch,
        SharedSongCount: 0);

    [Fact]
    public void Drain_returns_each_lobby_once()
    {
        var buffer = new LobbyChangeBuffer();
        buffer.Enqueue(new LobbyChange("A", LobbyChangeKind.Added, Dto("A")));
        buffer.Enqueue(new LobbyChange("B", LobbyChangeKind.Added, Dto("B")));

        var drained = buffer.Drain();

        Assert.Equal(2, drained.Count);
        Assert.Contains(drained, c => c.LobbyId == "A");
        Assert.Contains(drained, c => c.LobbyId == "B");
    }

    [Fact]
    public void Added_then_Updated_collapses_to_Updated()
    {
        var buffer = new LobbyChangeBuffer();
        buffer.Enqueue(new LobbyChange("A", LobbyChangeKind.Added, Dto("A", 1)));
        buffer.Enqueue(new LobbyChange("A", LobbyChangeKind.Updated, Dto("A", 2)));

        var drained = buffer.Drain();

        var single = Assert.Single(drained);
        Assert.Equal(LobbyChangeKind.Updated, single.Kind);
        Assert.Equal(2, single.Lobby!.PlayerCount);
    }

    [Fact]
    public void Updated_then_Removed_collapses_to_Removed()
    {
        var buffer = new LobbyChangeBuffer();
        buffer.Enqueue(new LobbyChange("A", LobbyChangeKind.Updated, Dto("A")));
        buffer.Enqueue(new LobbyChange("A", LobbyChangeKind.Removed, null));

        var drained = buffer.Drain();

        var single = Assert.Single(drained);
        Assert.Equal(LobbyChangeKind.Removed, single.Kind);
    }

    [Fact]
    public void Removed_wins_against_a_later_Added()
    {
        // Same-tick resurrection (Removed → Added) must stay Removed per spec.
        var buffer = new LobbyChangeBuffer();
        buffer.Enqueue(new LobbyChange("A", LobbyChangeKind.Removed, null));
        buffer.Enqueue(new LobbyChange("A", LobbyChangeKind.Added, Dto("A")));

        var drained = buffer.Drain();

        var single = Assert.Single(drained);
        Assert.Equal(LobbyChangeKind.Removed, single.Kind);
    }

    [Fact]
    public void Drain_clears_the_buffer()
    {
        var buffer = new LobbyChangeBuffer();
        buffer.Enqueue(new LobbyChange("A", LobbyChangeKind.Added, Dto("A")));
        _ = buffer.Drain();

        Assert.Empty(buffer.Drain());
    }
}
