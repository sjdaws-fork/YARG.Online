using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Server-issued "press play" signal. Sent once every peer in the session has
// reported PeerReady. Each client computes
//   localStartTime = SongOriginUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
// and starts its SongRunner so songTime=0 lines up with that wall-clock instant.
// CountdownMs is a suggested local pre-roll (typically 3000) — clients are free
// to ignore it and use their own pre-roll, since alignment is keyed off
// SongOriginUtcMs alone.
public struct GameStartCuePacket : INetSerializable
{
    public long SongOriginUtcMs { get; set; }
    public int CountdownMs { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(SongOriginUtcMs);
        writer.Put(CountdownMs);
    }

    public void Deserialize(NetDataReader reader)
    {
        SongOriginUtcMs = reader.GetLong();
        CountdownMs = reader.GetInt();
    }
}
