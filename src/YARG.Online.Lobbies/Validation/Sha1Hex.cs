using System.Text.RegularExpressions;

namespace YARG.Online.Lobbies.Validation;

/// <summary>
/// Shared SHA-1 hex validation. A valid value is exactly 40 hexadecimal
/// characters (upper or lower case). Used both by request validators and by
/// the streamed song-library drain path in the lobby hub.
/// </summary>
public static partial class Sha1Hex
{
    public static bool IsMatch(string value) => Sha1HexRegex().IsMatch(value);

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex Sha1HexRegex();
}
