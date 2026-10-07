using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;

/// <summary>Packs of one set in a load; <paramref name="WashCycleId"/> names the passed wash cycle their trays came from.</summary>
public sealed record LoadPackInput(Guid InstrumentSetId, int Quantity, Guid? WashCycleId = null);

public sealed record CreateLoadRequest(
    string SterilizerName,
    SterilizationMethod Method,
    string? Program,
    bool ContainsImplants,
    string? OperatorName,
    string? Notes,
    IReadOnlyList<LoadPackInput> Packs);

public sealed record CompleteLoadRequest(decimal TemperatureC, decimal? PressureKpa, int ExposureMinutes, IndicatorResult ChemicalIndicator);

public sealed record BiologicalIndicatorRequest(IndicatorResult Result, string? Lot);

public sealed record FailLoadRequest(string Reason);

public sealed record LoadListItem(
    Guid Id,
    string CycleNumber,
    string SterilizerName,
    SterilizationMethod Method,
    LoadStatus Status,
    DateTimeOffset LoadedAt,
    DateTimeOffset? CompletedAt,
    IndicatorResult ChemicalIndicator,
    IndicatorResult BiologicalIndicator,
    DateTimeOffset? ReleasedAt,
    string? FailureReason,
    bool ContainsImplants,
    int Packs);

public sealed record SterilePackDto(
    Guid Id,
    string PackNumber,
    Guid LoadId,
    string? CycleNumber,
    Guid InstrumentSetId,
    string? SetName,
    PackStatus Status,
    DateOnly? SterileUntil,
    bool IsExpired,
    Guid? IssuedToDepartmentId,
    Guid? WashCycleId,
    string? WashCycleNumber);

public sealed record RecallItem(
    Guid IssueId,
    string? PackNumber,
    string? SetName,
    Guid DepartmentId,
    string? DepartmentName,
    DateTimeOffset IssuedAt,
    SetIssueStatus Status,
    Guid? PatientId,
    string? PatientUhid,
    string? PatientName,
    DateTimeOffset? RecallFlaggedAt,
    DateTimeOffset? RecallAcknowledgedAt,
    string? RecallAction);

/// <summary>Traceability of a load: where every pack went (CSSD-05); a recall is required when a released load failed.</summary>
public sealed record RecallReport(
    Guid LoadId,
    string CycleNumber,
    bool RecallRequired,
    int PacksOnShelfRecalled,
    int IssuesFlagged,
    int IssuesAcknowledged,
    int PatientsExposed,
    IReadOnlyList<RecallItem> Issues);

public sealed record LoadDto(
    Guid Id,
    string CycleNumber,
    string SterilizerName,
    SterilizationMethod Method,
    string? Program,
    bool ContainsImplants,
    LoadStatus Status,
    DateTimeOffset LoadedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    decimal? TemperatureC,
    decimal? PressureKpa,
    int? ExposureMinutes,
    IndicatorResult ChemicalIndicator,
    IndicatorResult BiologicalIndicator,
    string? BiologicalIndicatorLot,
    DateTimeOffset? BiologicalReadAt,
    string? OperatorName,
    DateTimeOffset? ReleasedAt,
    string? ReleasedByName,
    string? FailureReason,
    string? Notes,
    IReadOnlyList<SterilePackDto> Packs,
    uint Version);

public sealed class CreateLoadRequestValidator : AbstractValidator<CreateLoadRequest>
{
    public CreateLoadRequestValidator()
    {
        RuleFor(x => x.SterilizerName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.Program).MaximumLength(100);
        RuleFor(x => x.OperatorName).MaximumLength(200);
        RuleFor(x => x.Notes).SafeText(500);
        RuleFor(x => x.Packs).NotEmpty().WithMessage("Add at least one set to the load.");
        RuleForEach(x => x.Packs).ChildRules(pack =>
        {
            pack.RuleFor(p => p.InstrumentSetId).NotEmpty();
            pack.RuleFor(p => p.Quantity).InclusiveBetween(1, 50);
        });
        RuleFor(x => x.Packs).Must(packs => packs is null || packs.Sum(p => p.Quantity) <= 200).WithMessage("A load can hold at most 200 packs.");
    }
}

public sealed class CompleteLoadRequestValidator : AbstractValidator<CompleteLoadRequest>
{
    public CompleteLoadRequestValidator()
    {
        RuleFor(x => x.TemperatureC).InclusiveBetween(0m, 200m);
        RuleFor(x => x.PressureKpa).InclusiveBetween(0m, 1000m).When(x => x.PressureKpa is not null);
        RuleFor(x => x.ExposureMinutes).InclusiveBetween(0, 24 * 60);
        RuleFor(x => x.ChemicalIndicator).IsInEnum().NotEqual(IndicatorResult.Pending).WithMessage("Record the chemical indicator as passed or failed.");
    }
}

public sealed class BiologicalIndicatorRequestValidator : AbstractValidator<BiologicalIndicatorRequest>
{
    public BiologicalIndicatorRequestValidator()
    {
        RuleFor(x => x.Result).IsInEnum().NotEqual(IndicatorResult.Pending).WithMessage("Record the biological indicator as passed or failed.");
        RuleFor(x => x.Lot).MaximumLength(64);
    }
}

public sealed class FailLoadRequestValidator : AbstractValidator<FailLoadRequest>
{
    public FailLoadRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}
