using System;

using YARG.Online.Lobbies.Contracts.Enums;

namespace YARG.Online.Lobbies.Contracts.Hubs;

public sealed record PlayerJoinedEvent(string LobbyId, string UserId, string DisplayName)
{
    /// <summary>The joining member's selected instrument as a YARG.Core.Instrument byte. 0 if unspecified.</summary>
    public byte Instrument { get; init; }
}

public sealed record PlayerLeftEvent(string LobbyId, string UserId);

public sealed record LobbyClosedEvent(string LobbyId, string Reason);

public sealed record ChatMessageEvent(string LobbyId, ChatMessage Message);

public sealed record LobbySongLibraryUpdatedEvent(string LobbyId, string[] Added, string[] Removed);

public sealed record SongQueuedEvent(string LobbyId, QueuedSongDto Song);

public sealed record SongRemovedFromQueueEvent(string LobbyId, long Sequence, SongRemovalReason Reason)
{
    /// <summary>
    /// User id of the member who triggered the removal. Set only when
    /// <see cref="Reason"/> is <see cref="SongRemovalReason.Removed"/> — for the
    /// other reasons (Played, RequesterLeft) there's no acting user, so this is
    /// null. Clients resolve the display name through the lobby's member map.
    /// </summary>
    public string? RemovedByUserId { get; init; }
}

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
    string GameToken,
    DateTimeOffset ExpiresAt);

public sealed record LobbyStatusChangedEvent(string LobbyId, LobbyStatus Status);

/// <summary>
/// Broadcast when a member's "back in lobby" flag flips. Flipped to false
/// for every member when the host starts a song, and back to true for an
/// individual member when they invoke <see cref="ILobbyHub.LeaveResults"/>
/// (the post-game results screen calls this on Continue).
/// </summary>
public sealed record PlayerLobbyReadyChangedEvent(string LobbyId, string UserId, bool IsBackInLobby);
