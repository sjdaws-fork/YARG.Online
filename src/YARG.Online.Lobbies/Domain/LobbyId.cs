using System.Text.RegularExpressions;

namespace YARG.Online.Lobbies.Domain;

public static partial class LobbyId
{
    public const int Length = 6;

    // Codes draw from a 31-char alphabet (ABCEFGHJKLMNPQRSTUVWXYZ01234579) chosen
    // to omit visually-ambiguous glyphs in monospace fonts: D vs O vs 0, I vs 1 vs L,
    // 6 vs G, 8 vs B. Six characters yields ~887M values — large enough that a fresh
    // generator collides at sub-percent rates for tens of thousands of concurrent lobbies
    // while staying short enough to dictate over voice / type on a controller.
    [GeneratedRegex(@"^[A-CE-HJ-NP-Z0-579]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    public static bool IsValid(string? id) => id is not null && SlugRegex().IsMatch(id);
}
