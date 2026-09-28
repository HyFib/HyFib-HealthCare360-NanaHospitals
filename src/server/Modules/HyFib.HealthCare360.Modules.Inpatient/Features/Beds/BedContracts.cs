using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Beds;

public sealed record BedDto(
    Guid Id,
    Guid WardId,
    string? WardName,
    Guid? RoomId,
    string? RoomNumber,
    string Label,
    BedStatus Status,
    string? StatusReason,
    DateTimeOffset? StatusChangedAt,
    Guid? CurrentAdmissionId,
    Guid? ReservedForPatientId,
    DateTimeOffset? ReservedUntil,
    bool HasOxygen,
    bool HasVentilator,
    bool HasMonitor,
    int DisplayOrder,
    bool IsActive,
    IReadOnlyList<BedStatus> NextStatuses,
    uint Version);

public sealed record SaveBedRequest(Guid WardId, Guid? RoomId, string Label, bool HasOxygen, bool HasVentilator, bool HasMonitor, int DisplayOrder, bool IsActive, uint? Version);

/// <summary>Creates <see cref="Count"/> beds labelled "{prefix}-{nn}" starting at <see cref="StartNumber"/> (ward set-up).</summary>
public sealed record CreateBedsRequest(Guid WardId, Guid? RoomId, string LabelPrefix, int StartNumber, int Count, bool HasOxygen, bool HasVentilator, bool HasMonitor);

public sealed record ReserveBedRequest(Guid PatientId, DateTimeOffset ReservedUntil, string? Note);

public sealed record BedReasonRequest(string Reason);

public sealed record BedNoteRequest(string? Notes);

public sealed record AvailableBedDto(
    Guid BedId,
    string Label,
    Guid WardId,
    string WardName,
    WardType WardType,
    string? RoomNumber,
    BedStatus Status,
    bool HasOxygen,
    bool HasVentilator,
    bool HasMonitor,
    bool IsEligible,
    string? IneligibleReason);

/// <summary>Flags shown on a bed tile so the ward team sees risks at a glance.</summary>
public enum BedAlert
{
    Mlc = 0,
    Restricted,
    HighFallRisk,
    PressureInjuryRisk,
    SeverePain,
    MedicationOverdue,
    DischargeInitiated,
    DischargeDueToday,
    ReservationExpired,
    CleaningDelayed,
}

public sealed record BedOccupant(
    Guid AdmissionId,
    string AdmissionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    string? ConsultantName,
    DateTimeOffset AdmittedAt,
    int LengthOfStayDays,
    DateOnly? ExpectedDischargeDate,
    AdmissionStatus Status,
    PayerType PayerType);

public sealed record BedBoardItem(
    Guid BedId,
    string Label,
    string? RoomNumber,
    BedStatus Status,
    string? StatusReason,
    DateTimeOffset? StatusChangedAt,
    bool HasOxygen,
    bool HasVentilator,
    bool HasMonitor,
    DateTimeOffset? ReservedUntil,
    BedOccupant? Occupant,
    IReadOnlyList<BedAlert> Alerts);

public sealed record BedBoardWard(
    Guid WardId,
    string Code,
    string Name,
    WardType Type,
    string? Floor,
    string? NurseStation,
    BedCounts Counts,
    IReadOnlyList<BedBoardItem> Beds);

public sealed record WardOccupancy(Guid WardId, string Code, string Name, WardType Type, BedCounts Counts);

public sealed record WardTypeOccupancy(WardType Type, BedCounts Counts);

public sealed record OccupancyReport(DateTimeOffset AsOf, BedCounts Total, IReadOnlyList<WardOccupancy> Wards, IReadOnlyList<WardTypeOccupancy> ByType);

public sealed record HousekeepingTaskDto(
    Guid Id,
    Guid BedId,
    string BedLabel,
    Guid WardId,
    string WardName,
    Guid? AdmissionId,
    HousekeepingTrigger Trigger,
    HousekeepingStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? TurnaroundMinutes,
    int WaitingMinutes,
    string? Notes);

public sealed record BedHistoryItem(
    Guid AdmissionId,
    string AdmissionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    BedAssignmentReason Reason,
    DateTimeOffset FromAt,
    DateTimeOffset? ToAt);

public sealed class SaveBedRequestValidator : AbstractValidator<SaveBedRequest>
{
    public SaveBedRequestValidator()
    {
        RuleFor(x => x.WardId).NotEmpty();
        RuleFor(x => x.Label).NotEmpty().MaximumLength(24).Matches("^[A-Za-z0-9-/ ]+$").WithMessage("Use letters, digits, spaces, '/' or '-'.");
        RuleFor(x => x.DisplayOrder).InclusiveBetween(0, 10_000);
    }
}

public sealed class CreateBedsRequestValidator : AbstractValidator<CreateBedsRequest>
{
    public CreateBedsRequestValidator()
    {
        RuleFor(x => x.WardId).NotEmpty();
        RuleFor(x => x.LabelPrefix).NotEmpty().MaximumLength(16).Matches("^[A-Za-z0-9-]+$").WithMessage("Use letters, digits or dashes.");
        RuleFor(x => x.StartNumber).InclusiveBetween(1, 999);
        RuleFor(x => x.Count).InclusiveBetween(1, 100);
    }
}

public sealed class ReserveBedRequestValidator : AbstractValidator<ReserveBedRequest>
{
    public ReserveBedRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.ReservedUntil)
            .Must(until => until > time.GetUtcNow() && until <= time.GetUtcNow().AddDays(7))
            .WithMessage("A reservation must end in the future and within 7 days.");
        RuleFor(x => x.Note).SafeText(300);
    }
}

public sealed class BedReasonRequestValidator : AbstractValidator<BedReasonRequest>
{
    public BedReasonRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

public sealed class BedNoteRequestValidator : AbstractValidator<BedNoteRequest>
{
    public BedNoteRequestValidator() => RuleFor(x => x.Notes).SafeText(500);
}
