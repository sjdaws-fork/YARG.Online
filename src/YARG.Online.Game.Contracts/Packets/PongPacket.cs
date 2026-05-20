using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Server reply to PingPacket. Carries the echoed ClientTickMs (so the client can
// pair the reply with its original probe and measure RTT) plus the server's
// wall-clock UTC milliseconds sampled as late as possible before serialization.
// Clients estimate clock offset via Cristian's algorithm:
//   rtt    = receiveLocalUtcMs - ClientTickMs   (both sampled by the client)
//   offset = ServerUtcMs + rtt/2 - receiveLocalUtcMs
// Then serverEstimatedUtcMs = localUtcMs + offset for any later instant.
public sealed class PongPacket : INetSerializable
{
    public long ClientTickMs { get; set; }
    public long ServerUtcMs { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(ClientTickMs);
        writer.Put(ServerUtcMs);
    }

    public void Deserialize(NetDataReader reader)
    {
        ClientTickMs = reader.GetLong();
        ServerUtcMs = reader.GetLong();
    }
}
