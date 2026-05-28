using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Server -> remaining peers when one peer disconnects mid-session. Lets
// clients mark the absent player DNF immediately rather than waiting for
// their own connection-loss timeout, and stop expecting any more EngineInput
// packets for that PeerId.
public struct RemotePeerLeftPacket : INetSerializable
{
    public int PeerId { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(PeerId);
    }

    public void Deserialize(NetDataReader reader)
    {
        PeerId = reader.GetInt();
    }
}
