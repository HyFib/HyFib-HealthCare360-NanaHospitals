using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Maintenance;

public sealed record SavePmPlanRequest(
    string Title,
    int FrequencyDays,
    DateOnly NextDueDate,
    string? Checklist,
    string? AssignedTo,
    Guid? VendorId,
    bool IsActive,
    uint? Version);

public sealed record PmPlanDto(
    Guid Id,
    Guid AssetId,
    string AssetTag,
    string AssetName,
    string Title,
    int FrequencyDays,
    DateOnly NextDueDate,
    DateOnly? LastDoneOn,
    string? Checklist,
    string? AssignedTo,
    Guid? VendorId,
    bool IsActive,
    int DaysToDue,
    uint Version);

public sealed record RecordPmVisitRequest(
    DateOnly VisitDate,
    string? PerformedBy,
    Guid? VendorId,
    PmVisitOutcome Outcome,
    string? Findings,
    decimal Cost,
    Guid? ServiceReportFileId);

public sealed record PmVisitDto(
    Guid Id,
    Guid PlanId,
    Guid AssetId,
    DateOnly VisitDate,
    string? PerformedBy,
    PmVisitOutcome Outcome,
    string? Findings,
    decimal Cost,
    DateOnly NextDueDate,
    Guid? ServiceReportFileId);

/// <param name="AssetDown">The equipment cannot be used until repaired (sets it Under maintenance).</param>
public sealed record ReportBreakdownRequest(Guid AssetId, string Issue, TicketPriority? Priority, bool AssetDown);

public sealed record AssignTicketRequest(string AssignedTo, Guid? AssignedToUserId, Guid? VendorId);

/// <param name="DowntimeMinutes">Override when the equipment stayed usable for part of the time.</param>
public sealed record ResolveTicketRequest(string Resolution, string? RootCause, decimal Cost, int? DowntimeMinutes);

public sealed record TicketListItem(
    Guid Id,
    string Number,
    Guid AssetId,
    string AssetTag,
    string AssetName,
    AssetCriticality AssetCriticality,
    TicketPriority Priority,
    TicketStatus Status,
    string Issue,
    DateTimeOffset ReportedAt,
    string? ReportedBy,
    string? AssignedTo,
    DateTimeOffset? ResolvedAt,
    int? DowntimeMinutes);

public sealed record TicketDetail(
    Guid Id,
    string Number,
    Guid AssetId,
    string AssetTag,
    string AssetName,
    TicketPriority Priority,
    TicketStatus Status,
    string Issue,
    DateTimeOffset ReportedAt,
    string? ReportedBy,
    string? AssignedTo,
    Guid? AssignedToUserId,
    Guid? VendorId,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    string? Resolution,
    string? RootCause,
    decimal Cost,
    int? DowntimeMinutes,
    uint Version);

public sealed record RecordCalibrationRequest(
    DateOnly CalibratedOn,
    string Agency,
    string? CertificateNumber,
    Guid? CertificateFileId,
    DateOnly NextDueDate,
    CalibrationResult Outcome,
    string? Remarks);

public sealed record CalibrationDto(
    Guid Id,
    Guid AssetId,
    DateOnly CalibratedOn,
    string Agency,
    string? CertificateNumber,
    Guid? CertificateFileId,
    DateOnly NextDueDate,
    CalibrationResult Outcome,
    string? Remarks);

/// <summary>Something that falls due for an asset: PM visit, calibration, warranty or AMC/CMC expiry.</summary>
public sealed record MaintenanceDueItem(
    string Kind,
    Guid FacilityId,
    Guid AssetId,
    string AssetTag,
    string AssetName,
    string Title,
    DateOnly DueDate,
    int DaysToDue);

public sealed class SavePmPlanRequestValidator : AbstractValidator<SavePmPlanRequest>
{
    public SavePmPlanRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().SafeText(200);
        RuleFor(x => x.FrequencyDays).InclusiveBetween(1, 3650);
        RuleFor(x => x.Checklist).SafeText(4000);
        RuleFor(x => x.AssignedTo).SafeText(120);
    }
}

public sealed class RecordPmVisitRequestValidator : AbstractValidator<RecordPmVisitRequest>
{
    public RecordPmVisitRequestValidator(TimeProvider time)
    {
        var today = HospitalClock.Today(time);
        RuleFor(x => x.VisitDate).LessThanOrEqualTo(today).WithMessage("The visit date cannot be in the future.");
        RuleFor(x => x.PerformedBy).SafeText(120);
        RuleFor(x => x.Findings).SafeText(2000);
        RuleFor(x => x.Cost).GreaterThanOrEqualTo(0);
    }
}

public sealed class ReportBreakdownRequestValidator : AbstractValidator<ReportBreakdownRequest>
{
    public ReportBreakdownRequestValidator()
    {
        RuleFor(x => x.AssetId).NotEmpty();
        RuleFor(x => x.Issue).NotEmpty().MinimumLength(5).SafeText(2000).WithMessage("Describe the problem.");
    }
}

public sealed class AssignTicketRequestValidator : AbstractValidator<AssignTicketRequest>
{
    public AssignTicketRequestValidator() => RuleFor(x => x.AssignedTo).NotEmpty().SafeText(120);
}

public sealed class ResolveTicketRequestValidator : AbstractValidator<ResolveTicketRequest>
{
    public ResolveTicketRequestValidator()
    {
        RuleFor(x => x.Resolution).NotEmpty().SafeText(2000);
        RuleFor(x => x.RootCause).SafeText(500);
        RuleFor(x => x.Cost).GreaterThanOrEqualTo(0);
        RuleFor(x => x.DowntimeMinutes).GreaterThanOrEqualTo(0);
    }
}

public sealed class RecordCalibrationRequestValidator : AbstractValidator<RecordCalibrationRequest>
{
    public RecordCalibrationRequestValidator(TimeProvider time)
    {
        var today = HospitalClock.Today(time);
        RuleFor(x => x.CalibratedOn).LessThanOrEqualTo(today).WithMessage("The calibration date cannot be in the future.");
        RuleFor(x => x.NextDueDate).GreaterThan(x => x.CalibratedOn).WithMessage("The next due date must be after the calibration date.");
        RuleFor(x => x.Agency).NotEmpty().SafeText(200);
        RuleFor(x => x.CertificateNumber).MaximumLength(64);
        RuleFor(x => x.Remarks).SafeText(1000);
    }
}
