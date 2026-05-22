using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Raw input event fanned out during free-play sections — Big Rock Endings
// (coda) for guitar/drums and activator drum-fill phrases. The normal
// NoteHit / NoteMissed sync path doesn't carry useful info during these
// sections: BRE notes short-circuit before OnSyncNoteHit fires, and
// activator-phrase auto-hits skip wire fanout entirely. To keep the remote
// highway's pad / fret flashes visually aligned with what the player is
// doing, the sender forwards each individual input action — receivers
// synthesize the matching OnPadHit (drums) / fret-flash (guitar) without
// touching their mirror engine's authoritative score state, which still
// reconciles via EngineStateSnapshot.
//
// Action: instrument-specific enum value cast to int.
//   - Drums:  DrumsAction (kick / red / yellow / blue / green / etc.)
//   - Guitar: GuitarAction (green / red / yellow / blue / orange fret + strum)
// Velocity: 0..1, used for drum dynamics (ghost/accent visuals); guitar
//   senders leave it at 0.
public sealed class FreePlayInputPacket : INetSerializable
{
    public int    PeerId { get; set; }
    public double SongTime { get; set; }
    public int    Action { get; set; }
    public float  Velocity { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(PeerId);
        writer.Put(SongTime);
        writer.Put(Action);
        writer.Put(Velocity);
    }

    public void Deserialize(NetDataReader reader)
    {
        PeerId = reader.GetInt();
        SongTime = reader.GetDouble();
        Action = reader.GetInt();
        Velocity = reader.GetFloat();
    }
}
