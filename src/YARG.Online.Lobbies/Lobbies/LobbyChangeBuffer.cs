using System.Collections.Concurrent;

namespace YARG.Online.Lobbies.Lobbies;

public sealed class LobbyChangeBuffer : ILobbyChangeBuffer
{
    private readonly ConcurrentDictionary<string, LobbyChange> _pending = new(StringComparer.Ordinal);

    // Tracks every lobbyId we've ever broadcast as Added to the browse group. Private
    // lobbies are never inserted, so subsequent Updated/Removed enqueues for them are
    // dropped — the browse side never saw the Added, so an Updated/Removed would be a
    // stranger reference (or worse, leak the existence of a private lobby).
    private readonly ConcurrentDictionary<string, byte> _publiclyKnown = new(StringComparer.Ordinal);

    public void Enqueue(LobbyChange change)
    {
        switch (change.Kind)
        {
            case LobbyChangeKind.Added:
                // Reject private lobbies at the top of the funnel — the entire point
                // of the private-lobby gate at LobbyHub.CreateLobby is reinforced here
                // so any future Added path that forgets the IsPublic check still can't
                // leak a private lobby into the browse stream.
                if (change.Lobby is not { IsPublic: true })
                {
                    return;
                }
                _publiclyKnown[change.LobbyId] = 0;
                break;

            case LobbyChangeKind.Updated:
                // Don't surface updates for lobbies we never broadcast as Added.
                if (!_publiclyKnown.ContainsKey(change.LobbyId))
                {
                    return;
                }
                break;

            case LobbyChangeKind.Removed:
                // Only emit Removed for lobbies we previously broadcast — and drop the
                // tracking entry now that it's gone.
                if (!_publiclyKnown.TryRemove(change.LobbyId, out _))
                {
                    return;
                }
                break;
        }

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
