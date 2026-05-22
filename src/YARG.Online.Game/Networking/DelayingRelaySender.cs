using System.Collections.Concurrent;
using System.Diagnostics;
using LiteNetLib;
using LiteNetLib.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace YARG.Online.Game.Networking;

// IRelaySender that applies the configured delay/jitter/loss before
// forwarding to the underlying LiteNetLib peer. Intended for testing the
// prediction/rollback layer under realistic remote-player conditions while
// the rest of the system runs locally.
//
// Implementation notes:
//   - Each `Send` snapshots the writer bytes immediately so the caller can
//     recycle/reuse the writer with no aliasing risk.
//   - The actual deferred send runs as a fire-and-forget Task per packet.
//     Tasks run their delays in parallel — they do NOT serialize behind a
//     per-target lock. An older implementation held a per-target semaphore
//     across the entire Task.Delay, which stacked latencies catastrophically
//     under sustained sender rate (each packet had to wait for every prior
//     packet's full delay to elapse before its own delay started). Fixed by
//     pre-computing an arrival timestamp per packet that is at least
//     (now + delay) but never earlier than the previous packet's arrival
//     for the same peer; ordering is preserved without stacking.
public sealed class DelayingRelaySender : IRelaySender
{
    private readonly LatencySimulatorOptions _options;
    private readonly ILogger<DelayingRelaySender> _logger;

    // Per-target arrival-time high-water mark (Stopwatch ticks). Each Send
    // computes max(now + delay, _nextArrivalTicks[target]) so the new packet
    // never arrives before the previous one for the same target. Updated
    // atomically via ConcurrentDictionary.AddOrUpdate; lock-free.
    private readonly ConcurrentDictionary<int, long> _nextArrivalTicks = new();

    // Per-target serialization gate held only briefly across the actual
    // target.Send() call. Cheap to acquire under steady-state because
    // arrival times are spaced — contention happens only for clustered
    // arrivals (same scheduled time on the timeline). Without this, two
    // tasks whose Task.Delay completes at the same instant could call
    // target.Send() in either order and break LiteNetLib's sequence
    // assignment for ReliableOrdered.
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _sendGate = new();

    private static readonly double TicksPerMs = Stopwatch.Frequency / 1000.0;

    public DelayingRelaySender(
        IOptions<LatencySimulatorOptions> options,
        ILogger<DelayingRelaySender> logger)
    {
        _options = options.Value;
        _logger = logger;

        _logger.LogWarning(
            "LatencySimulator ENABLED: delay={DelayMs}ms ±{JitterMs}ms loss={LossPercent}% ordered={Ordered}. " +
            "Do not run with this enabled in production.",
            _options.DelayMs, _options.JitterMs, _options.LossPercent, _options.PreserveOrdering);
    }

    public void Send(NetPeer target, NetDataWriter writer, DeliveryMethod delivery)
    {
        if (_options.LossPercent > 0 && Random.Shared.Next(100) < _options.LossPercent)
        {
            return;
        }

        var payload = new byte[writer.Length];
        Buffer.BlockCopy(writer.Data, 0, payload, 0, writer.Length);

        int totalDelayMs = _options.DelayMs;
        if (_options.JitterMs > 0)
        {
            totalDelayMs += Random.Shared.Next(-_options.JitterMs, _options.JitterMs + 1);
            if (totalDelayMs < 0) totalDelayMs = 0;
        }

        long now = Stopwatch.GetTimestamp();
        long delayTicks = (long)(totalDelayMs * TicksPerMs);
        long candidateTicks = now + delayTicks;
        long arrivalTicks;

        if (_options.PreserveOrdering)
        {
            // Each packet arrives at least 1 tick after the previous packet for
            // the same target (Stopwatch granularity ~10ns, so the +1 is
            // effectively free and gives us strict ordering even if two
            // concurrent senders race here).
            arrivalTicks = _nextArrivalTicks.AddOrUpdate(
                target.Id,
                candidateTicks,
                (_, prev) => Math.Max(prev + 1, candidateTicks));
        }
        else
        {
            arrivalTicks = candidateTicks;
        }

        long waitTicks = arrivalTicks - now;
        if (waitTicks <= 0)
        {
            // No wait needed — send immediately. Still go through the send
            // gate so we don't reorder against an in-flight delayed packet.
            _ = DispatchAsync(target, payload, delivery, 0);
            return;
        }

        int waitMs = (int)(waitTicks / TicksPerMs);
        _ = DispatchAsync(target, payload, delivery, waitMs);
    }

    private async Task DispatchAsync(NetPeer target, byte[] payload, DeliveryMethod delivery, int delayMs)
    {
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
            }

            if (_options.PreserveOrdering)
            {
                var gate = _sendGate.GetOrAdd(target.Id, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    SendPayload(target, payload, delivery);
                }
                finally
                {
                    gate.Release();
                }
            }
            else
            {
                SendPayload(target, payload, delivery);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Deferred relay send failed for peer {PeerId}.", target.Id);
        }
    }

    private static void SendPayload(NetPeer target, byte[] payload, DeliveryMethod delivery)
    {
        var view = NetDataWriter.FromBytes(payload, copy: false);
        target.Send(view, delivery);
    }
}
