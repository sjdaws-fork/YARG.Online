using LiteNetLib;
using LiteNetLib.Utils;

namespace YARG.Online.Game.Networking;

// Default IRelaySender. Hands the writer straight to LiteNetLib with no
// shaping. The compiler should inline this in release; it exists purely so
// the DI seam is clean and the latency simulator can replace it at startup
// without GameNetworkService having a feature flag in its hot path.
public sealed class PassthroughRelaySender : IRelaySender
{
    public void Send(NetPeer target, NetDataWriter writer, DeliveryMethod delivery)
    {
        target.Send(writer, delivery);
    }
}
