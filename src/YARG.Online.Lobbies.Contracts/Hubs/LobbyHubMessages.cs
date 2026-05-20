using System;
using System.Threading.Tasks;

using YARG.Online.Lobbies.Contracts.Enums;
using YARG.Online.Lobbies.Contracts.Rest;

namespace YARG.Online.Lobbies.Contracts.Hubs;

public sealed record CreateLobbyArgs(
    string Name,
    GameMode GameMode,
    Region Region,
    string? Song,
    int MaxPlayers,
    SongLibraryDto Library);

public sealed record CreateLobbyResult(LobbyDto Lobby);

public sealed record EnterLobbyArgs(string LobbyId, SongLibraryDto Library);

public sealed record LobbyMemberDto(string UserId, string DisplayName);

public sealed record EnterLobbyResult(
    LobbyDto Lobby,
    LobbyMemberDto[] CurrentMembers,
    ChatMessage[] ChatHistory,
    string[] LibraryRemovals,
    QueuedSongDto[] SongQueue);

public sealed record SendChatMessageArgs(string Text);

public sealed record ChatMessage(
    long Sequence,
    string UserId,
    string DisplayName,
    string Text,
    DateTimeOffset SentAt);

public sealed record QueuedSongDto(
    long Sequence,
    string SongHash,
    string RequesterId,
    DateTimeOffset QueuedAt,
    string[] MissingFor);

public sealed record QueueSongArgs(string SongHash);

public sealed record RemoveQueuedSongArgs(long Sequence);

public sealed record TransferHostArgs(string TargetUserId);

public sealed record KickPlayerArgs(string TargetUserId);
