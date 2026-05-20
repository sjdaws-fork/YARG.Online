using System.Text.RegularExpressions;

namespace YARG.Online.Lobbies.Domain;

public static partial class LobbyId
{
    public const int Length = 8;

    [GeneratedRegex(@"^[A-HJ-KM-NP-Z2-9]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    public static bool IsValid(string? id) => id is not null && SlugRegex().IsMatch(id);
}
