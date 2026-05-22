using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;

namespace YARG.Online.Lobbies.Allocation;

/// <summary>
/// Releases the slot range a lobby reserved on its allocated Agones GameServer by
/// PATCHing the slots list to empty via the <c>/status</c> subresource. Does not
/// delete the GameServer — fleet churn handles drain.
/// </summary>
public sealed class AgonesGameEndedHandler : IGameEndedHandler
{
    private const string GameServerGroup = "agones.dev";
    private const string GameServerVersion = "v1";
    private const string GameServerPlural = "gameservers";

    private readonly IKubernetes _client;
    private readonly AgonesOptions _options;
    private readonly ILogger<AgonesGameEndedHandler> _logger;

    public AgonesGameEndedHandler(
        IKubernetes client,
        IOptions<AgonesOptions> options,
        ILogger<AgonesGameEndedHandler> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task OnGameEndedAsync(GameEndedContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.GameServerName))
        {
            // Static allocator path — nothing to release.
            return;
        }

        // A lobby owns the full slot range it reserved; releasing means setting the
        // remaining values to empty. (The reference architecture's match-end PATCH
        // writes the new "remaining" array; the whole match is over here.)
        var patch = new
        {
            status = new
            {
                lists = new Dictionary<string, object>
                {
                    [_options.SlotsKey] = new { values = Array.Empty<string>() },
                },
            },
        };

        var body = new V1Patch(patch, V1Patch.PatchType.MergePatch);

        _logger.LogDebug(
            "Releasing Agones slots: LobbyId={LobbyId} GameServer={GameServer} Slots={Slots}",
            context.LobbyId, context.GameServerName, context.SlotCount);

        await _client.CustomObjects.PatchNamespacedCustomObjectStatusAsync(
            body: body,
            group: GameServerGroup,
            version: GameServerVersion,
            namespaceParameter: _options.Namespace,
            plural: GameServerPlural,
            name: context.GameServerName,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
