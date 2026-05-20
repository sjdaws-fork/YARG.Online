using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YARG.Online.Lobbies.Contracts.Rest;
using YARG.Online.Lobbies.Auth;
using YARG.Online.Lobbies.Errors;

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
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return routes;
    }

    private static Results<Ok<DevAuthResponse>, ValidationProblem> IssueDevToken(
        [FromBody] DevAuthRequest request,
        IValidator<DevAuthRequest> validator,
        IJwtTokenService tokens,
        ILogger<AuthEndpointsLog> logger)
    {
        logger.LogTrace("IssueDevToken: RequestName={RequestName}", request.Name);

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

    // Marker type used purely as the ILogger<T> category. Keeps the category at
    // "YARG.Online.Lobbies.Endpoints.AuthEndpointsLog" for namespace-level filtering.
    internal sealed class AuthEndpointsLog;
}
