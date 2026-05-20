using FluentValidation;
using YARG.Online.Lobbies.Contracts.Hubs;

namespace YARG.Online.Lobbies.Validation;

public sealed class SendChatMessageArgsValidator : AbstractValidator<SendChatMessageArgs>
{
    public SendChatMessageArgsValidator()
    {
        RuleFor(x => x.Text)
            .Must(t => !string.IsNullOrWhiteSpace(t))
            .WithMessage("Text must not be empty.")
            .Must(t => (t ?? string.Empty).Trim().Length <= 256)
            .WithMessage("Text must be 256 characters or fewer.");
    }
}
