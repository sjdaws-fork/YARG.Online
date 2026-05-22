using LiteNetLib.Utils;

namespace YARG.Online.Game.Contracts.Packets;

// Periodic authoritative engine-state snapshot. Sent by each peer roughly
// every half second of song time. The receiver uses the snapshot to:
//
//   * Trim its local event log of decisions older than SongTime — they're
//     baked into the snapshot and no longer need to be retained for
//     rollback purposes.
//   * Anchor the mirror engine's state to the sender's authoritative
//     values, eliminating any drift accumulated since the last snapshot.
//   * Replay any local events with songTime > SongTime on top of the
//     restored snapshot so the mirror's "current" state stays correct.
//
// A definitive FINAL snapshot is also sent immediately before GameComplete
// at song end. Receivers gate the transition to the results screen on
// having received every connected peer's final snapshot (or a timeout).
//
// The server treats SnapshotKind + SnapshotData as opaque. Only the
// destination client decodes the snapshot bytes into an EngineSnapshot.
// SnapshotKind discriminates instrument-specific subtypes (currently only
// 1 = Guitar; future entries 2 = Drums etc. when lobby play supports them).
public sealed class EngineStateSnapshotPacket : INetSerializable
{
    public int PeerId { get; set; }

    /// <summary>Sender's <c>engine.CurrentTime</c> at the moment the snapshot
    /// was captured. Used by receivers to trim event-log entries older than
    /// this time and to detect the "final" snapshot (sender's song completed
    /// when SongTime &gt;= chart length).</summary>
    public double SongTime { get; set; }

    /// <summary>Tag identifying which engine subclass produced the snapshot
    /// so the receiver picks the right deserializer. 1 = Guitar.</summary>
    public byte SnapshotKind { get; set; }

    /// <summary>Opaque serialized engine state. Format is owned by the
    /// Unity-side <c>EngineSnapshotSerializer</c> — the server just relays
    /// the byte array verbatim.</summary>
    public byte[] SnapshotData { get; set; } = System.Array.Empty<byte>();

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(PeerId);
        writer.Put(SongTime);
        writer.Put(SnapshotKind);
        writer.PutBytesWithLength(SnapshotData);
    }

    public void Deserialize(NetDataReader reader)
    {
        PeerId = reader.GetInt();
        SongTime = reader.GetDouble();
        SnapshotKind = reader.GetByte();
        SnapshotData = reader.GetBytesWithLength();
    }
}
