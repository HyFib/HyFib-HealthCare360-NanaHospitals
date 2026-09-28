using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Features.Indicators;

public sealed record IndicatorLatest(DateOnly PeriodStart, string PeriodLabel, decimal Value, bool? MeetsTarget);

public sealed record IndicatorDto(
    Guid Id,
    string Code,
    string Name,
    string Category,
    string NumeratorDefinition,
    string DenominatorDefinition,
    IndicatorUnit Unit,
    decimal Multiplier,
    decimal? Target,
    IndicatorDirection Direction,
    IndicatorFrequency Frequency,
    string? NabhReference,
    int SortOrder,
    bool IsActive,
    IndicatorLatest? Latest,
    uint Version);

/// <summary>Defines a KPI; the multiplier defaults from the unit (100 for %, 1,000 per thousand).</summary>
public sealed record SaveIndicatorRequest(
    string Code,
    string Name,
    string Category,
    string NumeratorDefinition,
    string DenominatorDefinition,
    IndicatorUnit Unit,
    decimal? Multiplier,
    decimal? Target,
    IndicatorDirection Direction,
    IndicatorFrequency Frequency,
    string? NabhReference,
    int SortOrder,
    bool IsActive,
    uint? Version);

/// <summary>Records (or corrects) the value for the period containing <see cref="PeriodStart"/>.</summary>
public sealed record RecordIndicatorValueRequest(DateOnly PeriodStart, decimal Numerator, decimal Denominator, string? Notes);

public sealed record IndicatorValueDto(
    Guid Id,
    DateOnly PeriodStart,
    string PeriodLabel,
    decimal Numerator,
    decimal Denominator,
    decimal Value,
    bool? MeetsTarget,
    string? Notes);

public sealed record IndicatorTrendDto(
    Guid IndicatorId,
    string Code,
    string Name,
    IndicatorUnit Unit,
    decimal? Target,
    IndicatorDirection Direction,
    IReadOnlyList<IndicatorValueDto> Points,
    decimal? Latest,
    decimal? Average,
    TrendDirection Trend);

public sealed record ScorecardRow(
    Guid IndicatorId,
    string Code,
    string Name,
    string Category,
    IndicatorUnit Unit,
    decimal? Target,
    IndicatorDirection Direction,
    DateOnly PeriodStart,
    string PeriodLabel,
    decimal? Value,
    bool? MeetsTarget);

public sealed class SaveIndicatorRequestValidator : AbstractValidator<SaveIndicatorRequest>
{
    public SaveIndicatorRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32).Must(c => c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            .WithMessage("Use letters, digits, '-' or '_'.");
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.Category).NotEmpty().SafeText(64);
        RuleFor(x => x.NumeratorDefinition).NotEmpty().SafeText(500);
        RuleFor(x => x.DenominatorDefinition).NotEmpty().SafeText(500);
        RuleFor(x => x.Multiplier).GreaterThan(0);
        RuleFor(x => x.NabhReference).SafeText(32);
    }
}

public sealed class RecordIndicatorValueRequestValidator : AbstractValidator<RecordIndicatorValueRequest>
{
    public RecordIndicatorValueRequestValidator()
    {
        RuleFor(x => x.Numerator).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Denominator).GreaterThan(0).WithMessage("The denominator must be more than zero.");
        RuleFor(x => x.Notes).SafeText(1000);
    }
}
