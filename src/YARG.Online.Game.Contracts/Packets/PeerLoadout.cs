using System;
using LiteNetLib.Utils;
using YARG.Online.Game.Contracts.Enums;

namespace YARG.Online.Game.Contracts.Packets;

// One per-player tuple inside GameStartPacket. Carries everything a peer needs
// to instantiate an engine for any other peer at game start.
//
// PeerId is the server-assigned LiteNetLib peer id and matches the value the
// server stamps onto EngineInputBatch packets it fans out. Clients use this to
// map incoming remote inputs back to a YargPlayer.
public sealed class PeerLoadout : INetSerializable
{
    public int PeerId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public InstrumentId Instrument { get; set; }
    public DifficultyId Difficulty { get; set; }
    public Guid EnginePreset { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(PeerId);
        writer.Put(UserId);
        writer.Put(DisplayName);
        writer.Put((byte)Instrument);
        writer.Put((byte)Difficulty);
        writer.Put(EnginePreset.ToByteArray());
    }

    public void Deserialize(NetDataReader reader)
    {
        PeerId = reader.GetInt();
        UserId = reader.GetString();
        DisplayName = reader.GetString();
        Instrument = (InstrumentId)reader.GetByte();
        Difficulty = (DifficultyId)reader.GetByte();
        var guidBytes = new byte[16];
        reader.GetBytes(guidBytes, 16);
        EnginePreset = new Guid(guidBytes);
    }
}
