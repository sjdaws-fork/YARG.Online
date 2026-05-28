using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Sent by each client when its local song time crosses end-of-chart. The
// server tracks completions per session and broadcasts GameEnd once all peers
// report. Peers that go silent without reporting are cleaned up via LiteNetLib's
// DisconnectTimeout — the resulting RemovePeer trims them out of the completion
// check so the remaining peers can still finish the session.
public struct GameCompletePacket : INetSerializable
{
    public void Serialize(NetDataWriter writer)
    {
    }

    public void Deserialize(NetDataReader reader)
    {
    }
}
