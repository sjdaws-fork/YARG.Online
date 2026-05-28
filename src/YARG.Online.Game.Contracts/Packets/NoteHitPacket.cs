using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Sent by a client the instant one of its local notes is hit. Paired with
// NoteMissedPacket — together they form the full per-note outcome stream.
// Receivers use the stream to predict each upcoming note's outcome based
// on the previous note's confirmed status, then roll back any predictions
// that turn out to be wrong (in either direction — predicted-miss-but-hit
// flips to a confirmed hit and re-shows visuals; predicted-hit-but-miss
// flips to a confirmed miss).
//
// NoteIndex / SongTime semantics match NoteMissedPacket: NoteIndex is the
// authoritative identifier (stable across peers via chart-hash gating);
// SongTime is telemetry only.
public struct NoteHitPacket : INetSerializable
{
    public int PeerId { get; set; }
    public int NoteIndex { get; set; }
    public double SongTime { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(PeerId);
        writer.Put(NoteIndex);
        writer.Put(SongTime);
    }

    public void Deserialize(NetDataReader reader)
    {
        PeerId = reader.GetInt();
        NoteIndex = reader.GetInt();
        SongTime = reader.GetDouble();
    }
}
