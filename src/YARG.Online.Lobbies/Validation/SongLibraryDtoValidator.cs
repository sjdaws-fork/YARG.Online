using System.Text.RegularExpressions;
using FluentValidation;
using YARG.Online.Lobbies.Contracts.Rest;

namespace YARG.Online.Lobbies.Validation;

public sealed partial class SongLibraryDtoValidator : AbstractValidator<SongLibraryDto>
{
    public const int MaxHashes = 50_000;

    public SongLibraryDtoValidator()
    {
        RuleFor(x => x.SongHashes)
            .NotNull()
            .Must(h => h is not null && h.Length > 0)
            .WithMessage("Library must contain at least one song hash.")
            .Must(h => h is null || h.Length <= MaxHashes)
            .WithMessage($"Library must contain at most {MaxHashes} song hashes.");

        RuleForEach(x => x.SongHashes)
            .Must(h => h is not null && Sha1HexRegex().IsMatch(h))
            .WithMessage("Each hash must be 40 hex characters.")
            .When(x => x.SongHashes is not null);
    }

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex Sha1HexRegex();
}
