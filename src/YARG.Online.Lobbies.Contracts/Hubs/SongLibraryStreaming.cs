namespace YARG.Online.Lobbies.Contracts.Hubs;

/// <summary>
/// Shared constants for the client-to-server song-library upload stream. The library is
/// streamed as <c>IAsyncEnumerable&lt;string[]&gt;</c> chunks (see <see cref="ILobbyHub.CreateLobby"/>,
/// <see cref="ILobbyHub.EnterLobby"/>, <see cref="ILobbyHub.UpdateLibrary"/>) so neither side
/// buffers the whole library in a single SignalR message.
/// </summary>
public static class SongLibraryStreaming
{
    /// <summary>
    /// Hashes per streamed chunk. Sized so one chunk stays under SignalR's default
    /// 32 KB MaximumReceiveMessageSize (a 40-char hex hash is ~43 JSON bytes, so
    /// 500 hashes ≈ 21.5 KB).
    /// </summary>
    public const int ChunkSize = 500;

    /// <summary>Server-enforced ceiling on the total number of hashes per upload.</summary>
    public const int MaxHashes = 50_000;
}
