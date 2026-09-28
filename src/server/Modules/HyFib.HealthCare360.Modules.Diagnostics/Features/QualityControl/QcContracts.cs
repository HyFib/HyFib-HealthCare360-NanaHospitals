using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.QualityControl;

public sealed record QcLotDto(
    Guid Id,
    Guid LabTestId,
    string TestCode,
    string TestName,
    string Material,
    int Level,
    string LotNumber,
    DateOnly ExpiryDate,
    decimal TargetMean,
    decimal TargetSd,
    string? Unit,
    string? InstrumentCode,
    bool IsActive,
    bool IsExpired,
    uint Version);

public sealed record SaveQcLotRequest(
    Guid LabTestId,
    string Material,
    int Level,
    string LotNumber,
    DateOnly ExpiryDate,
    decimal TargetMean,
    decimal TargetSd,
    string? Unit,
    string? InstrumentCode,
    bool IsActive,
    uint? Version);

public sealed record RecordQcRunRequest(Guid LotId, decimal Value, DateTimeOffset? RunAt);

public sealed record QcRunDto(
    Guid Id,
    Guid QcLotId,
    Guid LabTestId,
    string LotNumber,
    int Level,
    string? InstrumentCode,
    decimal Value,
    decimal ZScore,
    DateTimeOffset RunAt,
    QcOutcome Outcome,
    IReadOnlyList<string> Violations,
    string? RecordedBy,
    DateTimeOffset? ReviewedAt,
    string? ReviewedBy,
    string? ReviewNote,
    string? CorrectiveAction,
    bool Overridden);

public sealed record ReviewQcRunRequest(string? Note, string? CorrectiveAction);

public sealed record QcOverrideRequest(Guid LabTestId, string Reason);

/// <summary>An assay whose verification is locked because today's latest QC run on a control lot was rejected.</summary>
public sealed record QcLockout(Guid LabTestId, string TestName, IReadOnlyList<Guid> RejectedRunIds, IReadOnlyList<string> Violations);

public sealed record LimitLine(string Label, decimal Value);

public sealed record LeveyJenningsPoint(Guid RunId, DateTimeOffset RunAt, decimal Value, decimal ZScore, QcOutcome Outcome, IReadOnlyList<string> Violations);

/// <summary>Data for a Levey-Jennings chart: target mean, ±1/2/3 SD limit lines and the plotted runs (LIS-06).</summary>
public sealed record LeveyJenningsChart(
    Guid LotId,
    string TestName,
    string Material,
    int Level,
    string LotNumber,
    string? Unit,
    decimal Mean,
    decimal Sd,
    IReadOnlyList<LimitLine> Limits,
    IReadOnlyList<LeveyJenningsPoint> Points);

public sealed record QcStatusRow(
    Guid LabTestId,
    string TestCode,
    string TestName,
    Guid LotId,
    string LotNumber,
    int Level,
    string? InstrumentCode,
    QcOutcome? LatestOutcome,
    DateTimeOffset? LatestRunAt,
    bool Locked,
    bool Overridden);

public sealed class SaveQcLotRequestValidator : AbstractValidator<SaveQcLotRequest>
{
    public SaveQcLotRequestValidator()
    {
        RuleFor(x => x.LabTestId).NotEmpty();
        RuleFor(x => x.Material).NotEmpty().SafeText(200);
        RuleFor(x => x.Level).InclusiveBetween(1, 3);
        RuleFor(x => x.LotNumber).NotEmpty().MaximumLength(64);
        RuleFor(x => x.TargetSd).GreaterThan(0).WithMessage("The target SD must be greater than zero.");
        RuleFor(x => x.Unit).MaximumLength(32);
        RuleFor(x => x.InstrumentCode).MaximumLength(32);
    }
}

public sealed class RecordQcRunRequestValidator : AbstractValidator<RecordQcRunRequest>
{
    public RecordQcRunRequestValidator()
    {
        RuleFor(x => x.LotId).NotEmpty();
        RuleFor(x => x.Value).InclusiveBetween(-1_000_000m, 100_000_000m);
    }
}

public sealed class ReviewQcRunRequestValidator : AbstractValidator<ReviewQcRunRequest>
{
    public ReviewQcRunRequestValidator()
    {
        RuleFor(x => x.Note).SafeText(1000);
        RuleFor(x => x.CorrectiveAction).SafeText(1000);
    }
}

public sealed class QcOverrideRequestValidator : AbstractValidator<QcOverrideRequest>
{
    public QcOverrideRequestValidator()
    {
        RuleFor(x => x.LabTestId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).WithMessage("Explain why results may be released despite failed QC (at least 10 characters).").SafeText(500);
    }
}
