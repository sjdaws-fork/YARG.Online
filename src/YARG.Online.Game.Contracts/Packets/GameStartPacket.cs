using System;
using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Broadcast by the server once chart + quorum + per-peer loadouts have all
// arrived. The Loadouts array contains every peer's selection so each client
// can construct an engine-per-player at game start.
public sealed class GameStartPacket : INetSerializable
{
    public PeerLoadout[] Loadouts { get; set; } = Array.Empty<PeerLoadout>();

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Loadouts.Length);
        foreach (var loadout in Loadouts)
        {
            loadout.Serialize(writer);
        }
    }

    public void Deserialize(NetDataReader reader)
    {
        int count = reader.GetInt();
        Loadouts = new PeerLoadout[count];
        for (int i = 0; i < count; i++)
        {
            var loadout = new PeerLoadout();
            loadout.Deserialize(reader);
            Loadouts[i] = loadout;
        }
    }
}
