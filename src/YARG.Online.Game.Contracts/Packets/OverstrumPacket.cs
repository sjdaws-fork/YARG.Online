using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Sent by a client the instant its local engine reports an overstrum (or
// instrument-equivalent unnecessary input that breaks combo + docks the
// rock meter). Receivers apply it to the matching mirror engine, which
// drops all active sustains, strips SP from the current note, breaks
// combo, and bumps the overstrum counter — same side-effects as a local
// overstrum.
//
// SongTime is the engine-time moment the overstrum fired; receivers route
// it through ForceOverstrum(songTime) which advances the mirror engine
// to that point before running the engine's normal Overstrum() path.
public struct OverstrumPacket : INetSerializable
{
    public int PeerId { get; set; }
    public double SongTime { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(PeerId);
        writer.Put(SongTime);
    }

    public void Deserialize(NetDataReader reader)
    {
        PeerId = reader.GetInt();
        SongTime = reader.GetDouble();
    }
}
