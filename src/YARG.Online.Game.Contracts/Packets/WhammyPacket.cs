using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

public struct WhammyPacket : INetSerializable
{
    public int PeerId { get; set; }
    public double SongTime { get; set; }
    public float Value { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(PeerId);
        writer.Put(SongTime);
        writer.Put(Value);
    }

    public void Deserialize(NetDataReader reader)
    {
        PeerId = reader.GetInt();
        SongTime = reader.GetDouble();
        Value = reader.GetFloat();
    }
}
