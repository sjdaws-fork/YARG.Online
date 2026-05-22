using System;
using LiteNetLib.Utils;
using YARG.Online.Game.Contracts.Enums;

namespace YARG.Online.Game.Contracts.Packets;

// Sent by each client after handshake. The game server gates GameStart on
// having received one of these from every expected peer.
//
// ChartHash is a 20-byte fingerprint (SHA1) of the parsed chart, identical
// across all peers playing the same song+notes. The server compares all
// peers' hashes at quorum and aborts the session if they disagree. This is
// what makes NoteIndex meaningful in the event-based sync protocol —
// without a hash check, two peers could parse subtly different note lists
// from the same source and silently desync.
public sealed class SetLoadoutPacket : INetSerializable
{
    public const int ChartHashLength = 20;

    public InstrumentId Instrument { get; set; }
    public DifficultyId Difficulty { get; set; }

    // Identifies which engine preset (timing windows / HOPO logic) the player
    // wants to play with. Receivers look this up in their local preset library;
    // unknown Guids fall back to the instrument default.
    public Guid EnginePreset { get; set; }

    // Scroll speed multiplier (YargProfile.NoteSpeed). Per-player visual preference,
    // not the song's tempo — doesn't affect timing or sync. Receivers apply it to
    // their mirror engine for that peer's track only.
    public float NoteSpeed { get; set; }

    // Active modifiers bitmask (YARG.Core.Game.Modifier cast to ulong). The wire stays
    // decoupled from the engine enum so contracts don't drag in YARG.Core.
    public ulong Modifiers { get; set; }

    // 20-byte SHA1 of the parsed chart. All-zero is treated as "client did
    // not supply a hash" — the server will refuse to start the session in
    // that case once the strict-hash check is on.
    public byte[] ChartHash { get; set; } = new byte[ChartHashLength];

    public void Serialize(NetDataWriter writer)
    {
        writer.Put((byte)Instrument);
        writer.Put((byte)Difficulty);
        writer.Put(EnginePreset.ToByteArray());
        writer.Put(NoteSpeed);
        writer.Put(Modifiers);
        // Fixed-size payload — no length prefix. Receivers must read exactly
        // ChartHashLength bytes back out. If the client supplied a wrong-size
        // array, pad/truncate here so the wire format stays well-defined.
        if (ChartHash.Length == ChartHashLength)
        {
            writer.Put(ChartHash);
        }
        else
        {
            var padded = new byte[ChartHashLength];
            Array.Copy(ChartHash, padded, Math.Min(ChartHash.Length, ChartHashLength));
            writer.Put(padded);
        }
    }

    public void Deserialize(NetDataReader reader)
    {
        Instrument = (InstrumentId)reader.GetByte();
        Difficulty = (DifficultyId)reader.GetByte();
        var guidBytes = new byte[16];
        reader.GetBytes(guidBytes, 16);
        EnginePreset = new Guid(guidBytes);
        NoteSpeed = reader.GetFloat();
        Modifiers = reader.GetULong();
        ChartHash = new byte[ChartHashLength];
        reader.GetBytes(ChartHash, ChartHashLength);
    }
}
