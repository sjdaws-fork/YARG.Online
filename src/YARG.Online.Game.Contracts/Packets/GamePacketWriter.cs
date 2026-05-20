using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

public static class GamePacketWriter
{
    public static void Write<T>(NetDataWriter writer, PacketOpcode opcode, T packet)
        where T : INetSerializable
    {
        writer.Put((byte)opcode);
        packet.Serialize(writer);
    }
}
