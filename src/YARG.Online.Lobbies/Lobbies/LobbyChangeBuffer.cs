using System.Collections.Concurrent;

namespace YARG.Online.Lobbies.Lobbies;

public sealed class LobbyChangeBuffer : ILobbyChangeBuffer
{
    private readonly ConcurrentDictionary<string, LobbyChange> _pending = new(StringComparer.Ordinal);

    public void Enqueue(LobbyChange change)
    {
        _pending.AddOrUpdate(change.LobbyId, change, (_, existing) =>
        {
            // Removed always wins — don't resurrect a deleted lobby within the same tick.
            if (existing.Kind == LobbyChangeKind.Removed) return existing;
            return change;
        });
    }

    public IReadOnlyList<LobbyChange> Drain()
    {
        if (_pending.IsEmpty) return Array.Empty<LobbyChange>();

        var snapshot = _pending.ToArray();
        var drained = new List<LobbyChange>(snapshot.Length);
        foreach (var kvp in snapshot)
        {
            if (_pending.TryRemove(kvp))
            {
                drained.Add(kvp.Value);
            }
        }
        return drained;
    }
}
