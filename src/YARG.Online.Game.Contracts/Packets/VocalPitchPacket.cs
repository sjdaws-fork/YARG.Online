using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Per-frame-ish vocal pitch sample, fanned out from a singer to all other peers.
// Sender rate-limits to roughly 20 Hz; receivers interpolate between samples on
// the visual layer so the on-track pitch blob slides smoothly instead of
// snapping with the packet cadence.
//
// PitchMidi is the engine's last sang pitch as a MIDI note number (matches
// VocalsEngine.PitchSang). 0f means "no pitch detected this frame" — used as
// a sentinel; pair with IsSinging to disambiguate "silent" from "valid 0 midi".
public sealed class VocalPitchPacket : INetSerializable
{
    public int PeerId { get; set; }
    public double SongTime { get; set; }
    public float PitchMidi { get; set; }
    public bool IsSinging { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(PeerId);
        writer.Put(SongTime);
        writer.Put(PitchMidi);
        writer.Put(IsSinging);
    }

    public void Deserialize(NetDataReader reader)
    {
        PeerId = reader.GetInt();
        SongTime = reader.GetDouble();
        PitchMidi = reader.GetFloat();
        IsSinging = reader.GetBool();
    }
}
