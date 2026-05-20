using System.Collections.Concurrent;
using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using YARG.Online.Game.Contracts.Enums;
using YARG.Online.Game.Contracts.Packets;

namespace YARG.Online.E2E.Tests;

internal sealed class UdpGameClient : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _manager;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollLoop;
    private readonly TaskCompletionSource<HandshakeOutcome> _outcome =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<PacketOpcode> ReceivedOpcodes { get; } = new();
    public GameStartPacket? ReceivedGameStart { get; private set; }

    public UdpGameClient()
    {
        _manager = new NetManager(_listener) { UnconnectedMessagesEnabled = false };

        _listener.PeerConnectedEvent += p =>
        {
            ServerPeer = p;
            _outcome.TrySetResult(HandshakeOutcome.Connected);
        };
        _listener.PeerDisconnectedEvent += (_, _) => _outcome.TrySetResult(HandshakeOutcome.Rejected);
        _listener.NetworkReceiveEvent += (_, reader, _, _) =>
        {
            if (reader.AvailableBytes >= 1)
            {
                var opcode = (PacketOpcode)reader.GetByte();
                ReceivedOpcodes.Enqueue(opcode);
                if (opcode == PacketOpcode.GameStart)
                {
                    var packet = new GameStartPacket();
                    packet.Deserialize(reader);
                    ReceivedGameStart = packet;
                }
            }
            reader.Recycle();
        };
    }

    public NetPeer? ServerPeer { get; private set; }

    public async Task<HandshakeOutcome> ConnectAsync(IPEndPoint endpoint, string connectionKey, string jwt)
    {
        if (!_manager.Start())
        {
            throw new InvalidOperationException("UdpGameClient NetManager failed to start.");
        }

        _pollLoop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    _manager.PollEvents();
                }
            }
            catch (OperationCanceledException) { }
        });

        var writer = new NetDataWriter();
        writer.Put(connectionKey);
        writer.Put(jwt);
        _manager.Connect(endpoint, writer);

        var completed = await Task.WhenAny(_outcome.Task, Task.Delay(ConnectTimeout));
        return completed == _outcome.Task ? _outcome.Task.Result : HandshakeOutcome.TimedOut;
    }

    public void SendLoadout(InstrumentId instrument, DifficultyId difficulty, Guid enginePreset = default)
    {
        if (ServerPeer is null)
        {
            throw new InvalidOperationException("UdpGameClient not connected; cannot send loadout.");
        }

        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.SetLoadout, new SetLoadoutPacket
        {
            Instrument = instrument,
            Difficulty = difficulty,
            EnginePreset = enginePreset,
        });
        ServerPeer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendPeerReady()
    {
        if (ServerPeer is null)
        {
            throw new InvalidOperationException("UdpGameClient not connected; cannot send PeerReady.");
        }

        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.PeerReady, new PeerReadyPacket());
        ServerPeer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendGameComplete()
    {
        if (ServerPeer is null)
        {
            throw new InvalidOperationException("UdpGameClient not connected; cannot send GameComplete.");
        }

        var writer = new NetDataWriter();
        GamePacketWriter.Write(writer, PacketOpcode.GameComplete, new GameCompletePacket());
        ServerPeer.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    public async Task WaitForOpcodeCountAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ReceivedOpcodes.Count >= count)
            {
                return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException(
            $"UdpGameClient received {ReceivedOpcodes.Count}/{count} opcodes within {timeout}.");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_pollLoop is not null)
        {
            try { await _pollLoop; } catch { /* swallow */ }
        }
        _manager.Stop();
        _cts.Dispose();
    }
}

internal enum HandshakeOutcome { Connected, Rejected, TimedOut }
