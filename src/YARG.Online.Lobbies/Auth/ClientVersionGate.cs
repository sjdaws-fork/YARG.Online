using System;

namespace YARG.Online.Lobbies.Auth;

/// <summary>
/// Parses YARG's "online-alpha-vX.Y.Z"-style version strings and compares them
/// against a configured minimum. Used by both the auth endpoint and the lobby
/// hub for version gating.
/// </summary>
public static class ClientVersionGate
{
    /// <summary>
    /// Both versions must have the same prefix (everything up to and including the
    /// last v/V) and the client's dotted-int tail must be &gt;= the minimum's.
    /// Unparseable strings -- including git-branch dumps from editor builds and
    /// release builds reporting a different version scheme -- are rejected.
    /// </summary>
    public static bool IsVersionAccepted(string? clientVersion, string minVersion)
    {
        if (string.IsNullOrEmpty(clientVersion)) return false;
        if (!TryParseVersion(clientVersion, out var clientPrefix, out var clientTail)) return false;
        if (!TryParseVersion(minVersion, out var minPrefix, out var minTail)) return false;
        if (!string.Equals(clientPrefix, minPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return CompareDottedInts(clientTail, minTail) >= 0;
    }

    private static bool TryParseVersion(string version, out string prefix, out int[] tail)
    {
        prefix = string.Empty;
        tail = Array.Empty<int>();

        int vPos = Math.Max(version.LastIndexOf('v'), version.LastIndexOf('V'));
        if (vPos < 0) return false;

        prefix = version.Substring(0, vPos + 1);
        var rest = version.Substring(vPos + 1);
        if (rest.Length == 0) return false;

        var parts = rest.Split('.');
        tail = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out tail[i])) return false;
        }
        return true;
    }

    private static int CompareDottedInts(int[] a, int[] b)
    {
        int len = Math.Max(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            int av = i < a.Length ? a[i] : 0;
            int bv = i < b.Length ? b[i] : 0;
            int c = av.CompareTo(bv);
            if (c != 0) return c;
        }
        return 0;
    }
}
