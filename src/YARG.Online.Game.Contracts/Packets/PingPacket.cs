using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Client-issued clock-sync probe. The server echoes ClientTickMs verbatim in the
// matching PongPacket so the client can compute round-trip time without keeping
// any per-request state. The value is opaque to the server — a monotonic tick the
// client uses to pair the reply with the request and measure elapsed time.
public struct PingPacket : INetSerializable
{
    public long ClientTickMs { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(ClientTickMs);
    }

    public void Deserialize(NetDataReader reader)
    {
        ClientTickMs = reader.GetLong();
    }
}
