using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Online.Lobbies.Auth;
using YARG.Online.Lobbies.Errors;
using YARG.Online.Lobbies.Lobbies;

namespace YARG.Online.Lobbies.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes, IHostEnvironment env)
    {
        // Dev endpoint is only registered in Development. It mints an unverified identity from
        // any caller-supplied display name and must never be reachable in a deployed environment.
        if (!env.IsDevelopment())
        {
            return routes;
        }

        var group = routes.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapPost("/dev", IssueDevToken)
            .WithName("AuthDev")
            .WithSummary("Issue a dev auth token.")
            .AllowAnonymous()
            .RequireRateLimiting("dev-auth")
            .Produces<DevAuthResponse>(StatusCodes.Status200OK)
            .Produces<ClientVersionError>(StatusCodes.Status426UpgradeRequired)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return routes;
    }

    private static Results<Ok<DevAuthResponse>, ValidationProblem, JsonHttpResult<ClientVersionError>> IssueDevToken(
        [FromBody] DevAuthRequest request,
        IValidator<DevAuthRequest> validator,
        IJwtTokenService tokens,
        IOptions<LobbyOptions> options,
        ILogger<AuthEndpointsLog> logger)
    {
        logger.LogTrace("IssueDevToken: RequestName={RequestName} ClientVersion={ClientVersion}",
            request.Name, request.ClientVersion);

        // TODO(version-gate): temporarily disabled at the auth layer. The
        // currently-shipped clients in prod don't recognize the 426 and surface
        // it as a generic "Server Unavailable" dialog, which is more confusing
        // than letting them auth and then fail at the hub. Re-enable once enough
        // users have updated to a build that handles ClientUpdateRequiredException.
        // See also: LobbyHub.OnConnectedAsync for the hub-side check that's doing
        // the actual rejection for now.
        var minVersion = options.Value.MinClientVersion;
        if (!string.IsNullOrEmpty(minVersion)
            && !ClientVersionGate.IsVersionAccepted(request.ClientVersion, minVersion))
        {
            logger.LogTrace(
                "IssueDevToken: version gate would reject ClientVersion={ClientVersion} MinClientVersion={MinClientVersion} (auth gate disabled; hub will reject)",
                request.ClientVersion, minVersion);
            // TODO(version-gate): re-enable by uncommenting:
            // return TypedResults.Json(
            //     new ClientVersionError("client_update_required", minVersion),
            //     statusCode: StatusCodes.Status426UpgradeRequired);
        }

        var validation = validator.Validate(request);
        if (!validation.IsValid)
        {
            var firstError = validation.Errors[0];
            logger.LogTrace(
                "IssueDevToken validation failed: Property={Property} Error={Error}",
                firstError.PropertyName, firstError.ErrorMessage);
            return ValidationProblemFactory.FromFluentValidation(validation);
        }

        var displayName = request.Name.Trim();
        var userId = DevUserIdentity.Generate();
        var issued = tokens.IssueAuthToken(userId, displayName, request.ClientVersion);

        logger.LogTrace(
            "IssueDevToken issued: UserId={UserId} DisplayName={DisplayName} ExpiresAt={ExpiresAt}",
            userId, displayName, issued.ExpiresAt);

        return TypedResults.Ok(new DevAuthResponse(issued.Token, userId, displayName, issued.ExpiresAt));
    }

    // Marker type used purely as the ILogger<T> category. Keeps the category at
    // "YARG.Online.Lobbies.Endpoints.AuthEndpointsLog" for namespace-level filtering.
    internal sealed class AuthEndpointsLog;
}
