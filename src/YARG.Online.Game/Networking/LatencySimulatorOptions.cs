namespace YARG.Online.Game.Networking;

// Server-side network shaping for the relay path (input/event fanout). Lets
// you reproduce realistic remote-player conditions while sitting on a LAN.
//
// Disabled by default. When Enabled = true, every relayed packet through the
// gameplay-sync fanout path is held for (DelayMs ± JitterMs) before being
// forwarded to its target peer. Optionally drops a configurable percentage
// of packets to simulate loss.
//
// Only affects the gameplay-event relay path. Lifecycle packets (GameStart,
// GameStartCue, GameEnd, RemotePeerLeft, etc.) are sent directly so session
// state stays predictable under simulation.
public sealed class LatencySimulatorOptions
{
    public const string SectionName = "Network:LatencySimulator";

    // Master switch. False = pass-through, the simulator vanishes.
    public bool Enabled { get; init; } = false;

    // One-way delay applied to every outbound relayed packet, in ms.
    public int DelayMs { get; init; } = 0;

    // Per-packet random jitter on top of DelayMs, sampled uniformly from
    // [-JitterMs, +JitterMs]. Clamped so the total delay never goes negative.
    public int JitterMs { get; init; } = 0;

    // Percentage [0..100] of relayed packets to drop entirely. Reliable-
    // ordered delivery means the underlying transport will resend, so the
    // user-visible effect is added latency for the dropped packet, not data
    // loss. Useful for testing how the prediction layer behaves under bursty
    // delivery.
    public int LossPercent { get; init; } = 0;

    // If true and a packet is delayed, the simulator preserves per-target
    // ordering: packet B sent after packet A to the same peer never arrives
    // before A. This matches LiteNetLib's ReliableOrdered semantics on the
    // wire. False is closer to "what the network would actually do" if you
    // want to test reorder behavior — leave true unless you know why.
    public bool PreserveOrdering { get; init; } = true;
}
