namespace YARG.Online.Game.Contracts.Packets;

public enum PacketOpcode : byte
{
    GameStart = 1,
    GameEnd = 2,
    // 3 was UploadChart; charts are now distributed out-of-band via the lobby's
    // song-ownership gate. The slot is intentionally left open to keep wire
    // compatibility simple if any pre-online clients are still in flight.
    SetLoadout = 4,
    EngineInputBatch = 5,
    PeerReady = 6,
    GameStartCue = 7,
    GameComplete = 8,
    RemotePeerLeft = 9,
    SongMetadata = 10,
    Ping = 11,
    Pong = 12,
}
