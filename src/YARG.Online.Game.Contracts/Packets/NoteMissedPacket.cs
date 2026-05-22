using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Sent by a client the instant one of its local notes is missed.
//
// NoteIndex identifies the missed note in the chart's flattened note list
// for the *sender's* instrument+difficulty. Receivers reconstruct the same
// list from their copy of the chart, so indices are stable across peers as
// long as the chart hash matches (gated at game start).
//
// SongTime is included for telemetry and sanity checks; the authoritative
// identifier is NoteIndex.
//
// Apply semantics are set-add on the receiver: receiving the same NoteIndex
// twice has no additional effect, which keeps redundant retransmits harmless.
public sealed class NoteMissedPacket : INetSerializable
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
