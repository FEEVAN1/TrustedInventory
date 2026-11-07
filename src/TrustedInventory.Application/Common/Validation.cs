using System.Text.RegularExpressions;
using FluentValidation;
using TrustedInventory.Application.Errors;

namespace TrustedInventory.Application.Common;

public static partial class Validation
{
    /// <summary>Runs a validator and converts failures to a 422 VALIDATION_FAILED with field errors.</summary>
    public static void ValidateOrThrow<T>(this IValidator<T> validator, T model)
    {
        var result = validator.Validate(model);
        if (result.IsValid)
            return;
        var errors = result.Errors
            .Select(e => new FieldError(CamelPath(e.PropertyName), ErrorCode.ValidationFailed, e.ErrorMessage))
            .ToList();
        throw new AppException(ErrorCode.ValidationFailed, "The request has invalid fields.", errors);
    }

    // Contract formats (docs/openapi.yaml components): Code, Name, Reason, IanaTimeZone.
    public static IRuleBuilderOptions<T, string> ValidCode<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(50).Matches(CodePattern()).WithMessage("Use letters, digits and . _ / - only.");

    public static IRuleBuilderOptions<T, string> ValidName<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(200).Must(s => s == s.Trim()).WithMessage("Remove leading or trailing spaces.");

    public static IRuleBuilderOptions<T, string> ValidReason<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(1000).Must(s => !string.IsNullOrWhiteSpace(s)).WithMessage("A reason is required.");

    public static IRuleBuilderOptions<T, string> ValidTimeZone<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(64).Must(IsIanaTimeZone).WithMessage("Use an IANA time zone such as Africa/Lagos.");

    public static IRuleBuilderOptions<T, decimal?> ValidCelsius<T>(this IRuleBuilder<T, decimal?> rule) =>
        rule.InclusiveBetween(-273.15m, 200m).Must(c => c is null || decimal.Round(c.Value, 2) == c.Value)
            .WithMessage("Use at most two decimal places.");

    public static bool IsIanaTimeZone(string id) =>
        !string.IsNullOrWhiteSpace(id) && IanaPattern().IsMatch(id)
        && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz) && tz.HasIanaId;

    private static string CamelPath(string propertyName) =>
        string.Join('.', propertyName.Split('.').Select(p => p.Length == 0 ? p : char.ToLowerInvariant(p[0]) + p[1..]));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[A-Za-z_]+(/[A-Za-z0-9_+-]+)*$")]
    private static partial Regex IanaPattern();
}
