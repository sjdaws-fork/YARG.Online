using System.Security.Cryptography;
using YARG.Online.Lobbies.Domain;

namespace YARG.Online.Lobbies.Lobbies;

/// <summary>
/// Generates 8-character base32 IDs over the unambiguous alphabet specified in the v0 API
/// (no <c>0/O/1/I/L</c>): <c>[A-HJ-KM-NP-Z2-9]</c>.
/// </summary>
public sealed class Base32LobbyIdGenerator : ILobbyIdGenerator
{
    private static ReadOnlySpan<char> Alphabet => "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public string Next()
    {
        Span<byte> buf = stackalloc byte[LobbyId.Length];
        Span<char> chars = stackalloc char[LobbyId.Length];
        RandomNumberGenerator.Fill(buf);
        for (var i = 0; i < LobbyId.Length; i++)
        {
            chars[i] = Alphabet[buf[i] % Alphabet.Length];
        }
        return new string(chars);
    }
}
