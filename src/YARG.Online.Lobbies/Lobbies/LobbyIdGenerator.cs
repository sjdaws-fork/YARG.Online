using System.Security.Cryptography;
using YARG.Online.Lobbies.Domain;

namespace YARG.Online.Lobbies.Lobbies;

/// <summary>
/// Generates 6-character lobby codes from a 31-char alphabet that omits visually
/// ambiguous glyphs (no D/I/O letters, no 6/8 digits — see <see cref="LobbyId"/>
/// for the full justification). Codes are short enough to dictate over voice
/// and easy to enter on a controller; the ~887M-value space keeps collision
/// rates low enough that the repository's internal retry budget handles the
/// edge case without exposing collisions to the hub.
/// </summary>
public sealed class LobbyIdGenerator : ILobbyIdGenerator
{
    // Kept in lockstep with LobbyId.SlugRegex. Any change here MUST be mirrored
    // in the regex (and vice versa) or freshly-generated codes will start failing
    // IsValid() and the hub will reject every CreateLobby.
    private const string Alphabet = "ABCEFGHJKLMNPQRSTUVWXYZ01234579";

    public string Next()
    {
        Span<byte> buf = stackalloc byte[LobbyId.Length];
        Span<char> chars = stackalloc char[LobbyId.Length];
        RandomNumberGenerator.Fill(buf);
        for (var i = 0; i < LobbyId.Length; i++)
        {
            // % 31 over a uniform byte (0..255) has a small modulo bias — the first
            // 256 % 31 = 8 alphabet positions get one extra chance per byte. That's
            // a ~0.4% per-character skew, far below any meaningful adversarial floor
            // for a six-character collision-tolerant code where the dominant collision
            // risk is birthday-paradox over concurrently-active lobbies, not bias.
            chars[i] = Alphabet[buf[i] % Alphabet.Length];
        }
        return new string(chars);
    }
}
