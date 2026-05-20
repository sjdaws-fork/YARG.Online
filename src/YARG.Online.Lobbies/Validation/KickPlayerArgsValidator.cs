using FluentValidation;
using YARG.Online.Lobbies.Contracts.Hubs;

namespace YARG.Online.Lobbies.Validation;

public sealed class KickPlayerArgsValidator : AbstractValidator<KickPlayerArgs>
{
    public KickPlayerArgsValidator()
    {
        RuleFor(x => x.TargetUserId)
            .NotEmpty()
            .WithMessage("TargetUserId must not be empty.");
    }
}
