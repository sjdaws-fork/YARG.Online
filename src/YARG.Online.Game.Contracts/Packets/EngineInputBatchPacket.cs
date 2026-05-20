using System;
using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Network-transport shape of one or more YARG.Core GameInput records, batched
// to amortize header overhead. The same packet flows in both directions:
//   client -> server: PeerId is ignored on the wire (clients send 0)
//   server -> client: server overwrites PeerId with the sender's authenticated
//                     peer id before fanning out to other peers in the session.
//
// The packet body is intentionally opaque to the server — the server only
// reads PeerId, rewrites it, and re-emits the same payload bytes. Adding new
// GameInput-shaped fields is a contracts-only change for the server.
public sealed class EngineInputBatchPacket : INetSerializable
{
    public int PeerId { get; set; }
    public EngineInputRecord[] Inputs { get; set; } = Array.Empty<EngineInputRecord>();

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(PeerId);
        writer.Put(Inputs.Length);
        for (int i = 0; i < Inputs.Length; i++)
        {
            writer.Put(Inputs[i].Time);
            writer.Put(Inputs[i].Action);
            writer.Put(Inputs[i].Value);
        }
    }

    public void Deserialize(NetDataReader reader)
    {
        PeerId = reader.GetInt();
        int count = reader.GetInt();
        Inputs = new EngineInputRecord[count];
        for (int i = 0; i < count; i++)
        {
            Inputs[i] = new EngineInputRecord(
                reader.GetDouble(),
                reader.GetInt(),
                reader.GetInt());
        }
    }
}

// Mirror of YARG.Core's GameInput on the wire. Value carries the raw 4-byte
// payload of GameInput's int/float/bool union; receivers reinterpret it via
// the GameInput struct layout.
public readonly record struct EngineInputRecord(double Time, int Action, int Value);
