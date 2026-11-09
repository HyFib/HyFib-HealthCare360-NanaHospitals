using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.QualityControl;

public sealed record EqasSchemeDto(Guid Id, string Name, string Provider, string? ParticipantCode, int RoundsPerYear, bool IsActive, uint Version);

public sealed record SaveEqasSchemeRequest(string Name, string Provider, string? ParticipantCode, int RoundsPerYear, bool IsActive, uint? Version);

/// <summary>
/// One round of the register. <c>ZScore</c> and <c>Performance</c> once evaluated (|z| ≤ 2 acceptable, ≤ 3 warning, above
/// unacceptable); <c>NeedsCapa</c>: unacceptable with no corrective action linked; <c>CapaStatus</c> is the action's
/// status in the CAPA register now (null when none is linked, or the Quality module is not installed).
/// </summary>
public sealed record EqasRoundDto(
    Guid Id,
    Guid SchemeId,
    string SchemeName,
    string Provider,
    Guid LabTestId,
    string TestCode,
    string TestName,
    string? Unit,
    string RoundCode,
    DateOnly DueDate,
    DateOnly? SampleReceivedOn,
    decimal? ReportedValue,
    DateOnly? ReportedOn,
    decimal? AssignedValue,
    decimal? AssignedSd,
    string? PeerGroup,
    DateOnly? EvaluatedOn,
    decimal? ZScore,
    EqasPerformance? Performance,
    bool IsOverdue,
    bool NeedsCapa,
    Guid? CapaId,
    string? CapaNumber,
    string? CapaStatus,
    DateTimeOffset? AcceptedAt,
    string? AcceptedByName,
    string? AcceptanceNote,
    uint Version);

public sealed record ScheduleEqasRoundRequest(Guid SchemeId, Guid LabTestId, string RoundCode, DateOnly DueDate, DateOnly? SampleReceivedOn);

public sealed record ReportEqasResultRequest(decimal Value, DateOnly ReportedOn, uint Version);

public sealed record EvaluateEqasRoundRequest(decimal AssignedValue, decimal AssignedSd, string? PeerGroup, DateOnly EvaluatedOn, uint Version);

/// <summary>The corrective action, by its id or by the number the CAPA register gave it (one of the two).</summary>
public sealed record LinkEqasCapaRequest(Guid? CapaId, uint Version, string? CapaNumber = null);

public sealed record AcceptEqasRoundRequest(string? Note, uint Version);

/// <summary>
/// The lab dashboard's EQAS card: rounds whose result is overdue, unacceptable rounds still without a corrective action,
/// and the last twelve months' evaluated rounds by performance.
/// </summary>
public sealed record EqasSummary(int Overdue, int DueThisWeek, int NeedCapa, int Acceptable, int Warning, int Unacceptable, IReadOnlyList<EqasRoundDto> Attention);

public sealed class SaveEqasSchemeRequestValidator : AbstractValidator<SaveEqasSchemeRequest>
{
    public SaveEqasSchemeRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.Provider).NotEmpty().SafeText(200);
        RuleFor(x => x.ParticipantCode).SafeText(64);
        RuleFor(x => x.RoundsPerYear).InclusiveBetween(1, 52);
    }
}

public sealed class ScheduleEqasRoundRequestValidator : AbstractValidator<ScheduleEqasRoundRequest>
{
    public ScheduleEqasRoundRequestValidator()
    {
        RuleFor(x => x.SchemeId).NotEmpty();
        RuleFor(x => x.LabTestId).NotEmpty();
        RuleFor(x => x.RoundCode).NotEmpty().SafeText(64);
    }
}

public sealed class ReportEqasResultRequestValidator : AbstractValidator<ReportEqasResultRequest>
{
    public ReportEqasResultRequestValidator() => RuleFor(x => x.Value).InclusiveBetween(-1_000_000_000m, 1_000_000_000m);
}

public sealed class EvaluateEqasRoundRequestValidator : AbstractValidator<EvaluateEqasRoundRequest>
{
    public EvaluateEqasRoundRequestValidator()
    {
        RuleFor(x => x.AssignedValue).InclusiveBetween(-1_000_000_000m, 1_000_000_000m);
        RuleFor(x => x.AssignedSd).GreaterThan(0).LessThan(1_000_000_000m).WithMessage("The standard deviation must be above zero.");
        RuleFor(x => x.PeerGroup).SafeText(200);
    }
}

public sealed class LinkEqasCapaRequestValidator : AbstractValidator<LinkEqasCapaRequest>
{
    public LinkEqasCapaRequestValidator()
    {
        RuleFor(x => x).Must(x => (x.CapaId is { } id && id != Guid.Empty) || !string.IsNullOrWhiteSpace(x.CapaNumber))
            .WithName("capaNumber").WithMessage("Enter the corrective action's number from the CAPA register.");
        RuleFor(x => x.CapaNumber).SafeText(32);
    }
}

public sealed class AcceptEqasRoundRequestValidator : AbstractValidator<AcceptEqasRoundRequest>
{
    public AcceptEqasRoundRequestValidator() => RuleFor(x => x.Note).SafeText(1000);
}
