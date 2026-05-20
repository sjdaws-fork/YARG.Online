using System.Security.Claims;

namespace YARG.Online.Lobbies.Endpoints;

internal static class CallerExtensions
{
    public static (string UserId, string Name) RequireCaller(this ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue("sub")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Missing sub claim on authenticated principal.");
        var name = user.FindFirstValue("name")
            ?? user.Identity?.Name
            ?? string.Empty;
        return (sub, name);
    }
}
