using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Sent the instant the local player releases a sustain early (i.e. before
// the sustain's natural end). Does NOT break combo — the originating note
// remains hit — but it stops sustain scoring at SongTime. NOT batched; the
// prediction layer is sensitive to event arrival time.
//
// PeerId follows the same client-sends-0 / server-stamps-it convention as
// the other relay packets.
//
// NoteIndex identifies the sustain's root note in the chart's flattened
// note list for the sender's instrument+difficulty. Receivers reconstruct
// the same list from their copy of the chart, so indices are stable across
// peers as long as the chart hash matches (gated at game start).
//
// SongTime is the engine-time moment the player released. Receivers rewind
// their mirrored engine to just before this time and re-simulate the
// sustain as ending at SongTime instead of running to its natural end.
//
// Apply semantics on the receiver: idempotent. If the same NoteIndex
// arrives twice the second arrival is a no-op.
public struct SustainReleasedPacket : INetSerializable
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
