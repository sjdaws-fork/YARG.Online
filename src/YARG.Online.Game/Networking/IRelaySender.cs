using LiteNetLib;
using LiteNetLib.Utils;

namespace YARG.Online.Game.Networking;

// Thin abstraction over `peer.Send(...)` so the relay path can be wrapped in
// network shaping (the latency simulator) without GameNetworkService caring
// about whether sends are immediate or deferred.
//
// The pass-through implementation is a one-liner; the delaying implementation
// applies the configured delay/jitter/loss before invoking the real send.
public interface IRelaySender
{
    // Send a fully-encoded writer payload to the target peer using the given
    // delivery method. Implementations may copy the writer's bytes if they
    // defer the send, so callers can reuse or dispose the writer immediately.
    void Send(NetPeer target, NetDataWriter writer, DeliveryMethod delivery);
}
