using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Server -> all peers in a lobby. Sent whenever the loadout-ready tally
// for the session changes (peer submitted/cleared a loadout, or a peer
// dropped pre-cue and was removed from the quorum). Drives the
// DifficultySelect "waiting for players (X/Y)" indicator so the count
// reflects actual server-side state rather than a client-only guess.
public struct LoadoutReadyCountPacket : INetSerializable
{
    public int ReadyCount { get; set; }
    public int TotalExpected { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(ReadyCount);
        writer.Put(TotalExpected);
    }

    public void Deserialize(NetDataReader reader)
    {
        ReadyCount = reader.GetInt();
        TotalExpected = reader.GetInt();
    }
}
