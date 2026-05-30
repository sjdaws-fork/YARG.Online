using FluentValidation;
using YARG.Online.Lobbies.Contracts.Hubs;

namespace YARG.Online.Lobbies.Validation;

public sealed class QueueSongArgsValidator : AbstractValidator<QueueSongArgs>
{
    public QueueSongArgsValidator()
    {
        RuleFor(x => x.SongHash)
            .NotNull()
            .Must(h => h is not null && Sha1Hex.IsMatch(h))
            .WithMessage("Hash must be 40 hex characters.");

        RuleFor(x => x.SongSpeed)
            .InclusiveBetween(0.1f, 50f)
            .WithMessage("SongSpeed must be a multiplier between 0.1 and 50 (10% to 5000%).");
    }
}
