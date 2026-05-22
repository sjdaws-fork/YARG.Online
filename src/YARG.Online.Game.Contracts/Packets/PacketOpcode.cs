namespace YARG.Online.Game.Contracts.Packets;

public enum PacketOpcode : byte
{
    GameStart = 1,
    GameEnd = 2,
    SetLoadout = 4,
    PeerReady = 6,
    GameStartCue = 7,
    GameComplete = 8,
    RemotePeerLeft = 9,
    SongMetadata = 10,
    Ping = 11,
    Pong = 12,
    NoteMissed = 13,
    StarPowerActivated = 14,
    Whammy = 15,
    SustainReleased = 16,
    Overstrum = 17,
    NoteHit = 18,
    ClearLoadout = 20,
    // Per-frame-ish vocal pitch sample. Sender rate-limits (~20 Hz); receivers
    // interpolate between samples. Fan-out only — server treats it like Whammy.
    VocalPitch = 21,
    EngineStateSnapshot = 23,
}
