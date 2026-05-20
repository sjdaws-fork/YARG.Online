using FluentValidation;
using YARG.Online.Lobbies.Contracts.Hubs;

namespace YARG.Online.Lobbies.Validation;

public sealed class TransferHostArgsValidator : AbstractValidator<TransferHostArgs>
{
    public TransferHostArgsValidator()
    {
        RuleFor(x => x.TargetUserId)
            .NotEmpty()
            .WithMessage("TargetUserId must not be empty.");
    }
}
