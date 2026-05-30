using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Sent by a client at the moment it activates star power. SP *fills* are
// deterministic from the missed-note set (and whammy samples on sustains),
// so the receiver can compute SP availability locally — but the moment of
// activation is a player choice that cannot be derived, so it gets its own
// packet.
//
// PeerId follows the same client-sends-0 / server-stamps-it convention as
// the other relay packets.
//
// SongTime is the engine-time moment at which the sender activated. The
// receiver injects an activation into its mirrored engine for the sender
// at this time. Activations arriving past the rollback window are still
// applied (truth wins) but may not animate cleanly.
public class StarPowerActivatedPacket : INetSerializable
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
