using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace YARG.Online.Lobbies.Errors;

internal static class ValidationProblemFactory
{
    public static ValidationProblem FromFluentValidation(ValidationResult result)
    {
        var errors = result.Errors
            .GroupBy(e => string.IsNullOrEmpty(e.PropertyName) ? "_" : ToCamel(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        return TypedResults.ValidationProblem(errors);
    }

    private static string ToCamel(string name) =>
        string.IsNullOrEmpty(name) || char.IsLower(name[0])
            ? name
            : char.ToLowerInvariant(name[0]) + name[1..];
}
