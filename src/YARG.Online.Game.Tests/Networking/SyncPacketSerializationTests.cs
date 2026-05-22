using LiteNetLib.Utils;
using Xunit;
using YARG.Online.Game.Contracts.Packets;

namespace YARG.Online.Game.Tests.Networking;

// Roundtrip serialization for the event-based sync packets. The relay fast
// path treats packet bodies as opaque, but clients on the receive side rely
// on Serialize/Deserialize being byte-exact mirrors of each other.
public class SyncPacketSerializationTests
{
    [Fact]
    public void NoteMissedPacket_roundtrips()
    {
        var sent = new NoteMissedPacket
        {
            PeerId = 12,
            NoteIndex = 100,
            SongTime = 12.345,
        };

        var received = Roundtrip(sent, new NoteMissedPacket());

        Assert.Equal(12, received.PeerId);
        Assert.Equal(100, received.NoteIndex);
        Assert.Equal(12.345, received.SongTime);
    }

    [Fact]
    public void StarPowerActivatedPacket_roundtrips()
    {
        var sent = new StarPowerActivatedPacket { PeerId = 3, SongTime = 42.5 };

        var received = Roundtrip(sent, new StarPowerActivatedPacket());

        Assert.Equal(3, received.PeerId);
        Assert.Equal(42.5, received.SongTime);
    }

    [Fact]
    public void WhammyPacket_roundtrips()
    {
        var sent = new WhammyPacket
        {
            PeerId = 9,
            SongTime = 0.066,
            Value = 0.75f,
        };

        var received = Roundtrip(sent, new WhammyPacket());

        Assert.Equal(9, received.PeerId);
        Assert.Equal(0.066, received.SongTime);
        Assert.Equal(0.75f, received.Value);
    }

    private static T Roundtrip<T>(T sent, T dest) where T : INetSerializable
    {
        var writer = new NetDataWriter();
        sent.Serialize(writer);
        var reader = new NetDataReader(writer.CopyData());
        dest.Deserialize(reader);
        return dest;
    }
}
