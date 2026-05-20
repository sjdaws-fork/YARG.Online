using System.Text.RegularExpressions;
using FluentValidation;
using YARG.Online.Lobbies.Contracts.Rest;

namespace YARG.Online.Lobbies.Validation;

public sealed partial class DevAuthRequestValidator : AbstractValidator<DevAuthRequest>
{
    [GeneratedRegex(@"^[A-Za-z0-9_ \-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    public DevAuthRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .Length(1, 32)
            .Must(n => NameRegex().IsMatch(n))
            .WithMessage("Name must contain only letters, digits, spaces, underscores, or hyphens.");
    }
}
