using System;
using LiteNetLib.Utils;
using YARG.Online.Game.Contracts.Enums;

namespace YARG.Online.Game.Contracts.Packets;

// Sent by each client after handshake. The game server gates GameStart on
// having received one of these from every expected peer.
public sealed class SetLoadoutPacket : INetSerializable
{
    public InstrumentId Instrument { get; set; }
    public DifficultyId Difficulty { get; set; }

    // Identifies which engine preset (timing windows / HOPO logic) the player
    // wants to play with. Receivers look this up in their local preset library;
    // unknown Guids fall back to the instrument default.
    public Guid EnginePreset { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put((byte)Instrument);
        writer.Put((byte)Difficulty);
        writer.Put(EnginePreset.ToByteArray());
    }

    public void Deserialize(NetDataReader reader)
    {
        Instrument = (InstrumentId)reader.GetByte();
        Difficulty = (DifficultyId)reader.GetByte();
        var guidBytes = new byte[16];
        reader.GetBytes(guidBytes, 16);
        EnginePreset = new Guid(guidBytes);
    }
}
