using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

public static class GamePacketReader
{
    public static PacketOpcode ReadOpcode(NetDataReader reader)
    {
        return (PacketOpcode)reader.GetByte();
    }
}
