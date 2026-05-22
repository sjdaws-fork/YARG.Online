using System.Text.Json;
using k8s;
using Microsoft.Extensions.Options;

namespace YARG.Online.Lobbies.Allocation;

/// <summary>
/// Allocates a GameServer slot range on an Agones fleet by POSTing a
/// GameServerAllocation to the allocation.agones.dev API. Uses the two-selector
/// pattern from the reference architecture (prefer existing Allocated servers with
/// room, fall back to a Ready server) and atomically reserves <c>playerCount</c>
/// slot placeholders on the chosen GameServer's capacity list.
/// </summary>
public sealed class AgonesGameAllocator : IGameAllocator
{
    private const string AllocationGroup = "allocation.agones.dev";
    private const string AllocationVersion = "v1";
    private const string AllocationPlural = "gameserverallocations";
    private const string FleetLabel = "agones.dev/fleet";

    private readonly IKubernetes _client;
    private readonly AgonesOptions _options;
    private readonly ILogger<AgonesGameAllocator> _logger;

    public AgonesGameAllocator(
        IKubernetes client,
        IOptions<AgonesOptions> options,
        ILogger<AgonesGameAllocator> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GameAllocation> AllocateAsync(int playerCount, CancellationToken ct)
    {
        if (playerCount <= 0) throw new ArgumentOutOfRangeException(nameof(playerCount));

        // Reserve `playerCount` placeholder values on the slots list. Placeholder
        // values have no semantic meaning — the count is what reserves capacity.
        var placeholders = new string[playerCount];
        for (var i = 0; i < playerCount; i++) placeholders[i] = "x";

        var body = new
        {
            apiVersion = $"{AllocationGroup}/{AllocationVersion}",
            kind = "GameServerAllocation",
            spec = new
            {
                selectors = new object[]
                {
                    new
                    {
                        matchLabels = new Dictionary<string, string> { [FleetLabel] = _options.FleetName },
                        gameServerState = "Allocated",
                        lists = new Dictionary<string, object>
                        {
                            [_options.SlotsKey] = new { minAvailable = playerCount },
                        },
                    },
                    new
                    {
                        matchLabels = new Dictionary<string, string> { [FleetLabel] = _options.FleetName },
                        gameServerState = "Ready",
                        lists = new Dictionary<string, object>
                        {
                            [_options.SlotsKey] = new { minAvailable = playerCount },
                        },
                    },
                },
                priorities = new[]
                {
                    new { type = "List", key = _options.SlotsKey, order = "Ascending" },
                },
                lists = new Dictionary<string, object>
                {
                    [_options.SlotsKey] = new { append = placeholders },
                },
            },
        };

        _logger.LogDebug(
            "Allocating Agones GameServer: Namespace={Namespace} Fleet={Fleet} Slots={Slots}",
            _options.Namespace, _options.FleetName, playerCount);

        var response = await _client.CustomObjects.CreateNamespacedCustomObjectAsync(
            body: body,
            group: AllocationGroup,
            version: AllocationVersion,
            namespaceParameter: _options.Namespace,
            plural: AllocationPlural,
            cancellationToken: ct).ConfigureAwait(false);

        // CustomObjects returns an opaque object — round-trip through JSON to read fields.
        var json = JsonSerializer.SerializeToElement(response);
        if (!json.TryGetProperty("status", out var status))
        {
            throw new InvalidOperationException("GameServerAllocation response missing status.");
        }

        var state = status.TryGetProperty("state", out var stateEl) ? stateEl.GetString() : null;
        if (!string.Equals(state, "Allocated", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"GameServerAllocation did not succeed: state={state ?? "<null>"}.");
        }

        var address = status.TryGetProperty("address", out var addrEl) ? addrEl.GetString() : null;
        var gameServerName = status.TryGetProperty("gameServerName", out var nameEl) ? nameEl.GetString() : null;
        var port = ReadFirstPort(status);

        if (string.IsNullOrWhiteSpace(address) || port is null || string.IsNullOrWhiteSpace(gameServerName))
        {
            throw new InvalidOperationException(
                $"GameServerAllocation response missing address/port/gameServerName (address={address}, port={port}, name={gameServerName}).");
        }

        var endpoint = $"{address}:{port.Value}";
        _logger.LogInformation(
            "Allocated Agones GameServer: Name={GameServerName} Endpoint={Endpoint} Slots={Slots}",
            gameServerName, endpoint, playerCount);

        return new GameAllocation(endpoint, gameServerName, playerCount);
    }

    // TODO: Review this cause it looks sus
    private static int? ReadFirstPort(JsonElement status)
    {
        if (!status.TryGetProperty("ports", out var ports) || ports.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var p in ports.EnumerateArray())
        {
            if (p.TryGetProperty("port", out var portEl) && portEl.TryGetInt32(out var portValue))
            {
                return portValue;
            }
        }
        return null;
    }
}
