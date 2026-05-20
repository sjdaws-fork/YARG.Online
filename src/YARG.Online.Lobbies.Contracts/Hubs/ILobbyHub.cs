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
}
