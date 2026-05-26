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

        // Version gate: reject clients older than MinClientVersion.
        var minVersion = options.Value.MinClientVersion;
        if (!string.IsNullOrEmpty(minVersion)
            && !IsVersionAccepted(request.ClientVersion, minVersion))
        {
            logger.LogTrace(
                "IssueDevToken rejected: ClientVersion={ClientVersion} MinClientVersion={MinClientVersion}",
                request.ClientVersion, minVersion);
            return TypedResults.Json(
                new ClientVersionError("client_update_required", minVersion),
                statusCode: StatusCodes.Status426UpgradeRequired);
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
        var issued = tokens.IssueAuthToken(userId, displayName);

        logger.LogTrace(
            "IssueDevToken issued: UserId={UserId} DisplayName={DisplayName} ExpiresAt={ExpiresAt}",
            userId, displayName, issued.ExpiresAt);

        return TypedResults.Ok(new DevAuthResponse(issued.Token, userId, displayName, issued.ExpiresAt));
    }

    /// <summary>Compare client version against minimum. Simple string comparison for now.</summary>
    private static bool IsVersionAccepted(string clientVersion, string minVersion)
    {
        if (string.IsNullOrEmpty(clientVersion)) return false;
        return string.Compare(clientVersion, minVersion, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Marker type used purely as the ILogger<T> category. Keeps the category at
    // "YARG.Online.Lobbies.Endpoints.AuthEndpointsLog" for namespace-level filtering.
    internal sealed class AuthEndpointsLog;
}
