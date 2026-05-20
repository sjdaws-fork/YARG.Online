using System;

using YARG.Online.Lobbies.Contracts.Enums;

namespace YARG.Online.Lobbies.Contracts.Hubs;

public sealed record PlayerJoinedEvent(string LobbyId, string UserId, string DisplayName);

public sealed record PlayerLeftEvent(string LobbyId, string UserId);

public sealed record LobbyClosedEvent(string LobbyId, string Reason);

public sealed record ChatMessageEvent(string LobbyId, ChatMessage Message);

public sealed record LobbySongLibraryUpdatedEvent(string LobbyId, string[] Added, string[] Removed);

public sealed record SongQueuedEvent(string LobbyId, QueuedSongDto Song);

public sealed record SongRemovedFromQueueEvent(string LobbyId, long Sequence, SongRemovalReason Reason);

public sealed record QueuedSongAvailabilityChangedEvent(
    string LobbyId,
    long Sequence,
    string[] AddedMissing,
    string[] RemovedMissing);

public sealed record HostChangedEvent(string LobbyId, string NewHostUserId, string NewHostName);

public sealed record PlayerKickedEvent(string LobbyId, string UserId, string Reason);

public sealed record GameStartedEvent(
    string LobbyId,
    string GameServerEndpoint,
    string ConnectionKey,
    string GameToken,
    DateTimeOffset ExpiresAt);

public sealed record LobbyStatusChangedEvent(string LobbyId, LobbyStatus Status);
