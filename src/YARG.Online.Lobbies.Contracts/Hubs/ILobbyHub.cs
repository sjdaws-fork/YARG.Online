using System.Threading.Tasks;

namespace YARG.Online.Lobbies.Contracts.Hubs;

/// <summary>
/// Server methods on the lobby hub. The connection is automatically placed in the "browse"
/// group on connect and receives an initial <c>OnLobbySnapshot</c>; subsequent batch updates
/// arrive via <c>OnLobbyBatch</c>. To participate in a lobby, invoke <see cref="CreateLobby"/>
/// or <see cref="EnterLobby"/>; to return to browse, invoke <see cref="LeaveLobby"/>.
/// </summary>
public interface ILobbyHub
{
    Task<CreateLobbyResult> CreateLobby(CreateLobbyArgs args);
    Task<EnterLobbyResult> EnterLobby(EnterLobbyArgs args);
    Task LeaveLobby();
    Task SendChatMessage(SendChatMessageArgs args);
    Task<QueuedSongDto> QueueSong(QueueSongArgs args);
    Task RemoveQueuedSong(RemoveQueuedSongArgs args);
    Task TransferHost(TransferHostArgs args);
    Task KickPlayer(KickPlayerArgs args);
    Task StartGame();

    /// <summary>
    /// Signal that the caller has returned to the lobby from the post-game
    /// results screen (or never visited it — defaults true for everyone in
    /// the SongSelect state). The server flips the caller's per-member
    /// "back in lobby" flag to true and broadcasts
    /// <see cref="ILobbyHubClient.OnPlayerLobbyReadyChanged"/> so the host's
    /// Start button can ungate once every peer has reported in.
    /// </summary>
    Task LeaveResults();

    /// <summary>
    /// Replace the caller's per-member song library with a freshly-scanned set
    /// (called from the lobby picker's orange → ScanSongs popup after the local
    /// SongContainer refresh completes). The server recomputes the lobby's
    /// shared-library intersection across all members and broadcasts
    /// <see cref="ILobbyHubClient.OnLobbySongLibraryUpdated"/> with the diff
    /// against the previous shared set, plus any
    /// <see cref="ILobbyHubClient.OnQueuedSongAvailabilityChanged"/> deltas for
    /// queued songs the caller now does (or no longer does) have locally.
    /// </summary>
    Task UpdateLibrary(UpdateLibraryArgs args);
}
