using YARG.Online.Lobbies.Contracts.Enums;

namespace YARG.Online.Lobbies.Domain;

public sealed record Lobby(
    string Id,
    string Name,
    string HostUserId,
    string HostName,
    GameMode GameMode,
    Region Region,
    string Song,
    int PlayerCount,
    int MaxPlayers,
    DateTimeOffset CreatedAt,
    int SharedSongCount,
    bool IsPublic = true,
    LobbyStatus Status = LobbyStatus.SongSelect,
    DateTimeOffset? SongStartedAt = null,
    int? SongDurationMs = null);
