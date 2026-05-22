using System.Threading.Tasks;

using YARG.Online.Lobbies.Contracts.Rest;

namespace YARG.Online.Lobbies.Contracts.Hubs;

/// <summary>
/// Client-side callbacks invoked by the lobby hub. A connection only ever receives messages
/// for the group it is in: browse-scope messages while in the "browse" group, in-lobby messages
/// while in the "lobby:{id}" group.
/// </summary>
public interface ILobbyHubClient
{
    // Browse-scope (sent to "browse" group)
    Task OnLobbySnapshot(LobbyDto[] lobbies);
    Task OnLobbyBatch(LobbyBatchUpdate batch);

    // In-lobby (sent to "lobby:{id}" group)
    Task OnPlayerJoined(PlayerJoinedEvent e);
    Task OnPlayerLeft(PlayerLeftEvent e);
    Task OnLobbyClosed(LobbyClosedEvent e);
    Task OnChatMessage(ChatMessageEvent e);
    Task OnLobbySongLibraryUpdated(LobbySongLibraryUpdatedEvent e);
    Task OnSongQueued(SongQueuedEvent e);
    Task OnSongRemovedFromQueue(SongRemovedFromQueueEvent e);
    Task OnQueuedSongAvailabilityChanged(QueuedSongAvailabilityChangedEvent e);
    Task OnHostChanged(HostChangedEvent e);
    Task OnPlayerKicked(PlayerKickedEvent e);
    Task OnGameStarted(GameStartedEvent e);
    Task OnLobbyStatusChanged(LobbyStatusChangedEvent e);
    Task OnPlayerLobbyReadyChanged(PlayerLobbyReadyChangedEvent e);
}
