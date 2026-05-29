using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YARG.Online.Lobbies.Auth;
using YARG.Online.Lobbies.Lobbies;

namespace YARG.Online.Lobbies.Hubs;

/// <summary>
/// TODO(version-gate): rejects outdated clients at every hub method invocation by
/// throwing a HubException, which SignalR propagates back through the caller's
/// InvokeAsync (unlike OnConnectedAsync exceptions, which just close the transport
/// silently). This is the only place an outdated prod client can be made to surface
/// an error dialog without a client-side update.
///
/// The auth endpoint (AuthEndpoints.IssueDevToken) is intentionally letting outdated
/// clients through right now -- they hit this filter the moment they try to CreateLobby
/// or EnterLobby and the existing client-side catch on OnlineMenu wraps the message
/// into a "Could not join lobby" dialog. Remove this whole file (and the AddFilter
/// registration in Program.cs, and the client_version claim stamping in JwtTokenService)
/// once prod clients ship a build that handles the auth-time 426.
/// </summary>
public sealed class ClientVersionHubFilter : IHubFilter
{
    // LeaveLobby / LeaveResults run during teardown and must always succeed so the
    // server can clean up stale lobby membership. Skip the gate for them.
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "LeaveLobby",
        "LeaveResults",
    };

    private readonly IOptions<LobbyOptions> _lobbyOptions;
    private readonly ILogger<ClientVersionHubFilter> _logger;

    public ClientVersionHubFilter(IOptions<LobbyOptions> lobbyOptions, ILogger<ClientVersionHubFilter> logger)
    {
        _lobbyOptions = lobbyOptions;
        _logger = logger;
    }

    public ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var minVersion = _lobbyOptions.Value.MinClientVersion;
        if (string.IsNullOrEmpty(minVersion)
            || AllowedMethods.Contains(invocationContext.HubMethodName))
        {
            return next(invocationContext);
        }

        var clientVersion = invocationContext.Context.User?.FindFirst("client_version")?.Value;
        if (!ClientVersionGate.IsVersionAccepted(clientVersion, minVersion))
        {
            var sub = invocationContext.Context.User?.FindFirst("sub")?.Value;
            _logger.LogInformation(
                "ClientVersionHubFilter rejecting outdated client: ConnectionId={ConnectionId} Sub={Sub} Method={Method} ClientVersion={ClientVersion} MinClientVersion={MinClientVersion}",
                invocationContext.Context.ConnectionId, sub, invocationContext.HubMethodName,
                clientVersion, minVersion);
            throw new HubException(
                $"Your client is outdated. Please update to at least version {minVersion}.");
        }

        return next(invocationContext);
    }
}
