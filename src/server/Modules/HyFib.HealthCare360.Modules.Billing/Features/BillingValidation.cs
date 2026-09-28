namespace HyFib.HealthCare360.Modules.Billing.Features;

/// <summary>Validation rules shared by billing requests: rupee amounts, percentages, quantities and master codes.</summary>
public static class BillingValidation
{
    /// <summary>₹10 crore — far above any single bill, low enough to catch a mistyped amount.</summary>
    public const decimal MaxAmount = 100_000_000m;

    public const decimal MaxQuantity = 10_000m;

    public static IRuleBuilderOptions<T, decimal> Amount<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.GreaterThan(0m).WithMessage("Enter an amount greater than zero.")
            .LessThanOrEqualTo(MaxAmount).WithMessage("The amount is too large.")
            .PrecisionScale(18, 2, true).WithMessage("Use at most 2 decimal places.");

    public static IRuleBuilderOptions<T, decimal> NonNegativeAmount<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.GreaterThanOrEqualTo(0m).WithMessage("The amount cannot be negative.")
            .LessThanOrEqualTo(MaxAmount).WithMessage("The amount is too large.")
            .PrecisionScale(18, 2, true).WithMessage("Use at most 2 decimal places.");

    public static IRuleBuilderOptions<T, decimal?> OptionalAmount<T>(this IRuleBuilder<T, decimal?> rule) =>
        rule.GreaterThanOrEqualTo(0m).WithMessage("The amount cannot be negative.")
            .LessThanOrEqualTo(MaxAmount).WithMessage("The amount is too large.")
            .PrecisionScale(18, 2, true).WithMessage("Use at most 2 decimal places.");

    public static IRuleBuilderOptions<T, decimal> Percent<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.InclusiveBetween(0m, 100m).WithMessage("Enter a percentage between 0 and 100.")
            .PrecisionScale(5, 2, true).WithMessage("Use at most 2 decimal places.");

    public static IRuleBuilderOptions<T, decimal?> OptionalPercent<T>(this IRuleBuilder<T, decimal?> rule) =>
        rule.InclusiveBetween(0m, 100m).WithMessage("Enter a percentage between 0 and 100.")
            .PrecisionScale(5, 2, true).WithMessage("Use at most 2 decimal places.");

    public static IRuleBuilderOptions<T, decimal> Quantity<T>(this IRuleBuilder<T, decimal> rule) =>
        rule.GreaterThan(0m).WithMessage("Enter a quantity greater than zero.")
            .LessThanOrEqualTo(MaxQuantity).WithMessage("The quantity is too large.")
            .PrecisionScale(18, 3, true).WithMessage("Use at most 3 decimal places.");

    public static IRuleBuilderOptions<T, string> Code<T>(this IRuleBuilder<T, string> rule, int maxLength = 32) =>
        rule.NotEmpty().WithMessage("Enter a code.")
            .MaximumLength(maxLength)
            .Matches("^[A-Za-z0-9][A-Za-z0-9._-]*$").WithMessage("Use letters, digits, dots, dashes or underscores.");

    public static IRuleBuilderOptions<T, string?> Reason<T>(this IRuleBuilder<T, string> rule, int maxLength = 300) =>
        rule.NotEmpty().WithMessage("Enter a reason.")
            .MinimumLength(5).WithMessage("Describe the reason in a few words.")
            .SafeText(maxLength);
}
