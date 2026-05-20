namespace YARG.Online.Lobbies.Lobbies;

public interface ILobbyChangeBuffer
{
    void Enqueue(LobbyChange change);
    IReadOnlyList<LobbyChange> Drain();
}
