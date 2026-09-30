using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Http;

public static class ValidationExtensions
{
    /// <summary>
    /// Validates the <typeparamref name="TRequest"/> argument with its FluentValidation validator before the
    /// handler runs. Errors are returned as a 400 Validation Problem keyed by camelCase field paths
    /// (for example <c>address.pinCode</c>) so the UI can highlight fields directly.
    /// </summary>
    public static RouteHandlerBuilder WithValidation<TRequest>(this RouteHandlerBuilder builder)
        where TRequest : class =>
        builder.AddEndpointFilter<ValidationFilter<TRequest>>()
            .WithMetadata(new ValidatedRequest(typeof(TRequest)))
            .ProducesValidationProblem();
}

/// <summary>Marks an endpoint whose request is validated, so the architecture tests can check its validator exists.</summary>
public sealed record ValidatedRequest(Type RequestType);

internal sealed class ValidationFilter<TRequest> : IEndpointFilter
    where TRequest : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Fail closed: an endpoint that asks for validation never runs its handler unchecked.
        var validator = context.HttpContext.RequestServices.GetService<IValidator<TRequest>>()
            ?? throw new InvalidOperationException($"No validator is registered for {typeof(TRequest).Name}.");

        var request = context.Arguments.OfType<TRequest>().FirstOrDefault();
        if (request is null)
        {
            return TypedResults.Problem(title: "A request body is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await validator.ValidateAsync(request, context.HttpContext.RequestAborted);
        if (result.IsValid)
        {
            return await next(context);
        }

        var errors = result.Errors
            .GroupBy(e => ToCamelPath(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());

        return TypedResults.ValidationProblem(errors, title: "Please correct the highlighted fields.");
    }

    internal static string ToCamelPath(string propertyName) =>
        string.Join('.', propertyName.Split('.').Select(segment =>
        {
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            return bracket < 0
                ? JsonNamingPolicy.CamelCase.ConvertName(segment)
                : JsonNamingPolicy.CamelCase.ConvertName(segment[..bracket]) + segment[bracket..];
        }));
}

/// <summary>Reusable validation rules for Indian hospital data.</summary>
public static class CommonRules
{
    public static IRuleBuilderOptions<T, string?> IndianMobile<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(value => string.IsNullOrWhiteSpace(value) || SharedKernel.Phone.IsIndianMobile(SharedKernel.Phone.Normalize(value)))
            .WithMessage("Enter a valid 10-digit mobile number.");

    public static IRuleBuilderOptions<T, string?> PhoneNumber<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(value => string.IsNullOrWhiteSpace(value) || SharedKernel.Phone.Normalize(value) is not null)
            .WithMessage("Enter a valid phone number.");

    public static IRuleBuilderOptions<T, string?> PinCode<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Matches("^[1-9][0-9]{5}$").WithMessage("PIN code must be 6 digits.");

    public static IRuleBuilderOptions<T, string?> Gstin<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Matches("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$").WithMessage("Enter a valid 15-character GSTIN.");

    /// <summary>ABHA number: 14 digits, optionally formatted 12-3456-7890-1234.</summary>
    public static IRuleBuilderOptions<T, string?> AbhaNumber<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(value => string.IsNullOrWhiteSpace(value) || new string(value.Where(char.IsAsciiDigit).ToArray()).Length == 14)
            .WithMessage("ABHA number must have 14 digits.");

    public static IRuleBuilderOptions<T, string?> SafeText<T>(this IRuleBuilder<T, string?> rule, int maxLength) =>
        rule.MaximumLength(maxLength).Must(value => value is null || !value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            .WithMessage("Contains characters that are not allowed.");
}
