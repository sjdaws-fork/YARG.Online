using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Sent by each client once GameStart arrived, the chart loaded locally, and
// the gameplay scene is initialized. The server gates GameStartCue on having
// received one of these from every peer in the session.
public struct PeerReadyPacket : INetSerializable
{
    public void Serialize(NetDataWriter writer)
    {
    }

    public void Deserialize(NetDataReader reader)
    {
    }
}
