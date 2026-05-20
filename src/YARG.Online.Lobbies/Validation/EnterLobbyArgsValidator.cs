using FluentValidation;
using YARG.Online.Lobbies.Contracts.Hubs;
using YARG.Online.Lobbies.Domain;

namespace YARG.Online.Lobbies.Validation;

public sealed class EnterLobbyArgsValidator : AbstractValidator<EnterLobbyArgs>
{
    public EnterLobbyArgsValidator()
    {
        RuleFor(x => x.LobbyId)
            .NotEmpty()
            .Must(LobbyId.IsValid)
            .WithMessage("Lobby ID is invalid.");

        RuleFor(x => x.Library)
            .NotNull()
            .SetValidator(new SongLibraryDtoValidator()!);
    }
}
