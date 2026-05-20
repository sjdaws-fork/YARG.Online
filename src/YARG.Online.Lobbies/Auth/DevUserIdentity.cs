using System.Security.Cryptography;

namespace YARG.Online.Lobbies.Auth;

internal static class DevUserIdentity
{
    /// <summary>
    /// Generates a fresh dev userId: <c>u_</c> + 8 lowercase hex chars from a cryptographically
    /// random source. Each call returns a different ID — calling /auth/dev twice with the same
    /// display name intentionally yields two distinct identities (per the redesign spec).
    /// </summary>
    public static string Generate()
    {
        Span<byte> buf = stackalloc byte[4];
        RandomNumberGenerator.Fill(buf);
        return $"u_{Convert.ToHexString(buf).ToLowerInvariant()}";
    }
}
