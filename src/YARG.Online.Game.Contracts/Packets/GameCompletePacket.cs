using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Sent by each client when its local song time crosses end-of-chart. The
// server tracks completions per session and broadcasts GameEnd once all peers
// report. Peers that go silent without reporting are cleaned up via LiteNetLib's
// DisconnectTimeout — the resulting RemovePeer trims them out of the completion
// check so the remaining peers can still finish the session.
//
// The server also fans out each incoming GameComplete to every other peer
// (with PeerId stamped) so receivers can rebuild the remote player's
// ReplayFrame from authoritative inputs — without this, remote replays are
// empty and the score-screen verifier flags them as inconsistent.
public class GameCompletePacket : INetSerializable
{
    public GameCompletePacket() { }

    // Stamped by the server before fanout; 0 when client-originated.
    public int PeerId { get; set; }

    // Raw GameInput[] for the sending peer's local player, in record order.
    public ReplayInput[] ReplayInputs { get; set; } = System.Array.Empty<ReplayInput>();

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(PeerId);
        writer.Put(ReplayInputs.Length);
        for (int i = 0; i < ReplayInputs.Length; i++)
        {
            var input = ReplayInputs[i];
            writer.Put(input.Time);
            writer.Put(input.Action);
            writer.Put(input.Value);
        }
    }

    public void Deserialize(NetDataReader reader)
    {
        PeerId = reader.GetInt();
        int count = reader.GetInt();
        ReplayInputs = new ReplayInput[count];
        for (int i = 0; i < count; i++)
        {
            double time = reader.GetDouble();
            int action = reader.GetInt();
            int value = reader.GetInt();
            ReplayInputs[i] = new ReplayInput(time, action, value);
        }
    }
}

public readonly struct ReplayInput
{
    public readonly double Time;
    public readonly int Action;
    public readonly int Value;

    public ReplayInput(double time, int action, int value)
    {
        Time = time;
        Action = action;
        Value = value;
    }
}
