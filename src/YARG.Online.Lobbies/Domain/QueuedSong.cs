namespace YARG.Online.Lobbies.Domain;

public sealed record QueuedSong(
    long Sequence,
    string SongHash,
    string RequesterId,
    DateTimeOffset QueuedAt,
    IReadOnlyList<string> MissingFor,
    float SongSpeed);
