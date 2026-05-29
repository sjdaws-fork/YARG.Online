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
    SongLibraryDto Library)
{
    /// <summary>Caller's selected instrument as a YARG.Core.Instrument byte. 0 if unspecified.</summary>
    public byte Instrument { get; init; }

    /// <summary>
    /// True (default) → lobby appears in the public browser snapshot and
    /// incremental updates. False → lobby is only reachable via the join-by-code
    /// path (typed lobby ID); it is never enqueued into the browse change buffer
    /// and never surfaces in <c>OnLobbySnapshot</c> / <c>OnLobbyBatch</c>.
    /// </summary>
    public bool IsPublic { get; init; } = true;
}

public sealed record CreateLobbyResult(LobbyDto Lobby);

public sealed record EnterLobbyArgs(string LobbyId, SongLibraryDto Library)
{
    /// <summary>Caller's selected instrument as a YARG.Core.Instrument byte. 0 if unspecified.</summary>
    public byte Instrument { get; init; }
}

public sealed record LobbyMemberDto(string UserId, string DisplayName)
{
    /// <summary>This member's selected instrument as a YARG.Core.Instrument byte. 0 if unspecified.</summary>
    public byte Instrument { get; init; }

    /// <summary>
    /// True when the member is on the lobby (song-select) screen; false while
    /// they're still in gameplay or viewing the post-game results screen.
    /// The host's Start button is gated on every member being true. Defaults
    /// to true on first join so the host can immediately start the first
    /// song; flips to false on <c>StartGame</c>, back to true via
    /// <see cref="ILobbyHub.LeaveResults"/>.
    /// </summary>
    public bool IsBackInLobby { get; init; } = true;
}

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
    string[] MissingFor)
{
    /// <summary>Playback speed multiplier (1.0 = 100%). Applied for all members at game start.</summary>
    public float SongSpeed { get; init; } = 1f;
}

public sealed record QueueSongArgs(string SongHash)
{
    /// <summary>Requester's chosen playback speed multiplier (1.0 = 100%). Server clamps to [0.1, 50].</summary>
    public float SongSpeed { get; init; } = 1f;
}

public sealed record RemoveQueuedSongArgs(long Sequence);

public sealed record TransferHostArgs(string TargetUserId);

public sealed record KickPlayerArgs(string TargetUserId);

public sealed record UpdateLibraryArgs(SongLibraryDto Library);
