using FluentValidation;
using YARG.Online.Lobbies.Contracts.Hubs;

namespace YARG.Online.Lobbies.Validation;

public sealed class CreateLobbyArgsValidator : AbstractValidator<CreateLobbyArgs>
{
    public CreateLobbyArgsValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .Length(1, 48);

        RuleFor(x => x.Song)
            .Length(1, 128)
            .When(x => !string.IsNullOrEmpty(x.Song));

        RuleFor(x => x.MaxPlayers)
            .InclusiveBetween(2, 8);

        RuleFor(x => x.GameMode).IsInEnum();
        RuleFor(x => x.Region).IsInEnum();
    }
}
