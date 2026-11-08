using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.ColdChain;

public enum ColdChainCheckStatus
{
    /// <summary>The check's window has not opened yet.</summary>
    Upcoming = 0,

    /// <summary>The window is open and no reading has been entered.</summary>
    Due,

    Taken,

    /// <summary>The window closed with no manual reading.</summary>
    Missed,
}

/// <param name="MinCelsius">Lowest allowed reading; null takes the kind's default (2 °C, a freezer −25 °C).</param>
/// <param name="MaxCelsius">Highest allowed reading; null takes the kind's default (8 °C, a freezer −15 °C).</param>
public sealed record SaveColdStorageUnitRequest(
    Guid StoreId,
    string Code,
    string Name,
    ColdUnitKind Kind,
    decimal? MinCelsius,
    decimal? MaxCelsius,
    bool IsActive,
    uint? Version = null);

public sealed record ColdStorageUnitDto(
    Guid Id,
    Guid StoreId,
    string StoreName,
    string Code,
    string Name,
    ColdUnitKind Kind,
    decimal MinCelsius,
    decimal MaxCelsius,
    bool IsActive,
    bool HasLogger,
    DateTimeOffset? LoggerKeyIssuedAt,
    DateTimeOffset? LastReadingAt,
    decimal? LastCelsius,
    bool LastOutOfRange,
    Guid? OpenExcursionId,
    DateTimeOffset? LastLoggerReadingAt,
    ColdChainCheckStatus MorningCheck,
    ColdChainCheckStatus EveningCheck,
    uint Version);

/// <param name="TakenAt">When the thermometer was read; now when omitted. Up to 12 hours back.</param>
public sealed record RecordTemperatureRequest(decimal Celsius, DateTimeOffset? TakenAt, string? Note);

public sealed record TemperatureReadingRecorded(Guid Id, decimal Celsius, bool OutOfRange, string AllowedRange, Guid? ExcursionId);

/// <summary>A data logger's key, shown once, with where and how to send readings.</summary>
public sealed record ColdChainLoggerKey(Guid UnitId, string Key, string Path, string Header);

public sealed record LoggerReadingInput(DateTimeOffset TakenAt, decimal Celsius);

/// <summary>What a data logger sends: its readings, oldest or newest first, up to 500 at a time.</summary>
public sealed record LoggerUploadRequest(IReadOnlyList<LoggerReadingInput> Readings);

public sealed record LoggerUploadResult(int Accepted, int Duplicates, IReadOnlyList<string> Rejected);

public sealed record ExcursionActionRequest(string Action);

public sealed record TemperatureReadingDto(
    Guid Id,
    DateTimeOffset TakenAt,
    decimal Celsius,
    TemperatureSource Source,
    bool OutOfRange,
    decimal MinCelsius,
    decimal MaxCelsius,
    string? RecordedBy,
    string? Note);

public sealed record ColdChainCheck(ColdChainSlot Slot, ColdChainCheckStatus Status, DateTimeOffset? TakenAt, decimal? Celsius, string? RecordedBy);

public sealed record ColdChainExcursionDto(
    Guid Id,
    Guid UnitId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    decimal LowestCelsius,
    decimal HighestCelsius,
    int ReadingsOutOfRange,
    decimal MinCelsius,
    decimal MaxCelsius,
    string Extreme,
    string? ItemsAtRisk,
    string? ActionTaken,
    string? ActionBy,
    DateTimeOffset? ActionAt);

/// <summary>The cold-chain register for one unit and day: the two checks, every reading, the low and high, and the excursions.</summary>
public sealed record ColdChainDailyRegister(
    Guid UnitId,
    string Code,
    string Name,
    string StoreName,
    ColdUnitKind Kind,
    DateOnly Date,
    decimal MinCelsius,
    decimal MaxCelsius,
    decimal? LowestCelsius,
    decimal? HighestCelsius,
    IReadOnlyList<ColdChainCheck> Checks,
    IReadOnlyList<TemperatureReadingDto> Readings,
    IReadOnlyList<ColdChainExcursionDto> Excursions);

public sealed class SaveColdStorageUnitRequestValidator : AbstractValidator<SaveColdStorageUnitRequest>
{
    public SaveColdStorageUnitRequestValidator()
    {
        RuleFor(x => x.StoreId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().MaximumLength(24).Must(CatalogRulesProxy.ValidCode).WithMessage("Use letters, digits, '-', '_' or '/'.");
        RuleFor(x => x.Name).NotEmpty().SafeText(100);
        RuleFor(x => x.MinCelsius).InclusiveBetween(-90, 30).When(x => x.MinCelsius is not null);
        RuleFor(x => x.MaxCelsius).InclusiveBetween(-90, 30).When(x => x.MaxCelsius is not null);
        RuleFor(x => x.MaxCelsius).GreaterThan(x => x.MinCelsius).When(x => x.MinCelsius is not null && x.MaxCelsius is not null)
            .WithMessage("The highest allowed temperature must be above the lowest.");
    }
}

public sealed class RecordTemperatureRequestValidator : AbstractValidator<RecordTemperatureRequest>
{
    public RecordTemperatureRequestValidator()
    {
        RuleFor(x => x.Celsius).InclusiveBetween(-100, 100).PrecisionScale(5, 1, true).WithMessage("Enter the temperature in °C, to one decimal place.");
        RuleFor(x => x.Note).SafeText(300);
    }
}

public sealed class LoggerUploadRequestValidator : AbstractValidator<LoggerUploadRequest>
{
    public LoggerUploadRequestValidator() =>
        RuleFor(x => x.Readings).NotEmpty().Must(r => r.Count <= 500).WithMessage("Send between 1 and 500 readings at a time.");
}

public sealed class ExcursionActionRequestValidator : AbstractValidator<ExcursionActionRequest>
{
    public ExcursionActionRequestValidator() => RuleFor(x => x.Action).NotEmpty().SafeText(1000);
}

internal static class CatalogRulesProxy
{
    public static bool ValidCode(string? code) => Items.CatalogRules.ValidCode(code);
}
