using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Sent once by the host shortly after GameStart arrives. Carries chart-level
// metadata the server needs to expose on the lobby (e.g. duration for the
// browser progress display). Server validates the sender is the host and
// stores it on the session; the value is forwarded to the lobbies service
// alongside the SongOriginUtcMs at GameStartCue time.
public class SongMetadataPacket : INetSerializable
{
    public int DurationMs { get; set; }

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(DurationMs);
    }

    public void Deserialize(NetDataReader reader)
    {
        DurationMs = reader.GetInt();
    }
}
