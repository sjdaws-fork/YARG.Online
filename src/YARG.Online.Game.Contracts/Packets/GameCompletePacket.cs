using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Sent by each client when its local song time crosses end-of-chart. The
// server tracks completions per session and broadcasts GameEnd once all peers
// report (with a straggler timeout for crashed/disconnected clients).
public sealed class GameCompletePacket : INetSerializable
{
    public void Serialize(NetDataWriter writer)
    {
    }

    public void Deserialize(NetDataReader reader)
    {
    }
}
