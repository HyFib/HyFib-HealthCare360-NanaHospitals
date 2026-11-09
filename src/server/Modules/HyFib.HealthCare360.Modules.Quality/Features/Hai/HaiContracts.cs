using HyFib.HealthCare360.Modules.Quality.Domain.Hai;

namespace HyFib.HealthCare360.Modules.Quality.Features.Hai;

public sealed record DeviceEpisodeDto(
    Guid Id,
    Guid PatientId,
    string? Uhid,
    Guid AdmissionId,
    string AdmissionNumber,
    string? WardName,
    InvasiveDevice Device,
    string? Detail,
    DateTimeOffset InsertedAt,
    string? InsertedByName,
    DateTimeOffset? RemovedAt,
    DeviceRemovalReason? RemovalReason,
    string? RemovedByName,
    int DeviceDays,
    string? Notes,
    uint Version);

/// <summary>A device put in (or ventilation started) for an in-patient, named by the admission's id or its number.</summary>
public sealed record InsertDeviceRequest(Guid? AdmissionId, string? AdmissionNumber, InvasiveDevice Device, string? Detail, DateTimeOffset InsertedAt, string? Notes);

public sealed record RemoveDeviceRequest(DateTimeOffset RemovedAt, DeviceRemovalReason Reason, uint Version);

public sealed record HaiCaseListItem(
    Guid Id,
    string CaseNumber,
    HaiType Type,
    HaiCaseOrigin Origin,
    HaiCaseStatus Status,
    string? Uhid,
    DateOnly EventOn,
    string? Organism,
    string? Specimen,
    bool MeetsDefinition,
    DateTimeOffset CreatedAt);

public sealed record HaiCriterionDto(string Key, string Group, string Text, bool Met);

/// <summary>A case under review: the checklist with what is ticked, what HC360 worked out, and whether it meets the definition.</summary>
public sealed record HaiCaseDetail(
    Guid Id,
    string CaseNumber,
    HaiType Type,
    HaiCaseOrigin Origin,
    HaiCaseStatus Status,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid? AdmissionId,
    string? AdmissionNumber,
    DeviceEpisodeDto? Device,
    IReadOnlyList<DeviceEpisodeDto> PatientDevices,
    Guid? CultureResultId,
    string? Organism,
    string? Specimen,
    DateTimeOffset? SpecimenCollectedAt,
    Guid? SurgeryBookingId,
    string? SurgeryReference,
    DateOnly? OperatedOn,
    int? DaysAfterOperation,
    DateOnly EventOn,
    bool? DeviceEligible,
    int? DayOfDevice,
    IReadOnlyList<HaiCriterionDto> Criteria,
    bool MeetsDefinition,
    IReadOnlyList<string> Missing,
    string? ReviewedByName,
    DateTimeOffset? ReviewedAt,
    string? DecisionNote,
    string? Notes,
    uint Version);

/// <summary>A case the infection control team raises themselves (a clinical suspicion, a culture from outside).</summary>
public sealed record RaiseHaiCaseRequest(HaiType Type, Guid PatientId, Guid? DeviceEpisodeId, DateOnly EventOn, string? Organism, string? Notes);

/// <summary>The checklist as reviewed: the lines ticked, the date of event, the device episode or the operation it is associated with.</summary>
public sealed record ReviewHaiCaseRequest(
    IReadOnlyList<string> CriteriaMet,
    DateOnly EventOn,
    Guid? DeviceEpisodeId,
    Guid? SurgeryBookingId,
    string? SurgeryReference,
    DateOnly? OperatedOn,
    string? Notes,
    uint Version);

public sealed record DecideHaiCaseRequest(string? Note, uint Version);

public sealed record SurgeryOption(Guid BookingId, string Reference, DateOnly OperatedOn, bool Elective);

/// <summary>A month's device-associated infection figures.</summary>
public sealed record HaiRateRow(
    DateOnly Month,
    string Label,
    int PatientDays,
    int CentralLineDays,
    int UrinaryCatheterDays,
    int VentilatorDays,
    int Clabsi,
    int Cauti,
    int Vap,
    int Ssi,
    int Operations,
    decimal? ClabsiRate,
    decimal? CautiRate,
    decimal? VapRate,
    decimal? SsiRate,
    decimal? CentralLineUtilisation,
    decimal? UrinaryCatheterUtilisation,
    decimal? VentilatorUtilisation,
    int Suspected);

public sealed class InsertDeviceRequestValidator : AbstractValidator<InsertDeviceRequest>
{
    public InsertDeviceRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Device).IsInEnum();
        RuleFor(x => x.AdmissionId).OptionalId();
        RuleFor(x => x).Must(x => x.AdmissionId is not null || !string.IsNullOrWhiteSpace(x.AdmissionNumber))
            .WithName("admissionNumber").WithMessage("Give the patient's admission number.");
        RuleFor(x => x.AdmissionNumber).SafeText(32);
        RuleFor(x => x.Detail).SafeText(200);
        RuleFor(x => x.Notes).SafeText(1000);
        RuleFor(x => x.InsertedAt).Must(at => at <= time.GetUtcNow().AddMinutes(5)).WithMessage("The insertion cannot be in the future.");
    }
}

public sealed class RemoveDeviceRequestValidator : AbstractValidator<RemoveDeviceRequest>
{
    public RemoveDeviceRequestValidator()
    {
        RuleFor(x => x.Reason).IsInEnum();
        RuleFor(x => x.Version).SeenVersion();
    }
}

public sealed class RaiseHaiCaseRequestValidator : AbstractValidator<RaiseHaiCaseRequest>
{
    public RaiseHaiCaseRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.DeviceEpisodeId).OptionalId();
        RuleFor(x => x.EventOn).Must(d => d <= Services.QualityClock.LocalToday(time)).WithMessage("The date of event cannot be in the future.");
        RuleFor(x => x.Organism).SafeText(200);
        RuleFor(x => x.Notes).SafeText(2000);
    }
}

public sealed class ReviewHaiCaseRequestValidator : AbstractValidator<ReviewHaiCaseRequest>
{
    public ReviewHaiCaseRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.CriteriaMet).NotNull();
        RuleForEach(x => x.CriteriaMet).NotEmpty().MaximumLength(32);
        RuleFor(x => x.EventOn).Must(d => d <= Services.QualityClock.LocalToday(time)).WithMessage("The date of event cannot be in the future.");
        RuleFor(x => x.DeviceEpisodeId).OptionalId();
        RuleFor(x => x.SurgeryBookingId).OptionalId();
        RuleFor(x => x.SurgeryReference).SafeText(200);
        RuleFor(x => x.Notes).SafeText(2000);
        RuleFor(x => x.Version).SeenVersion();
    }
}

public sealed class DecideHaiCaseRequestValidator : AbstractValidator<DecideHaiCaseRequest>
{
    public DecideHaiCaseRequestValidator()
    {
        RuleFor(x => x.Note).SafeText(1000);
        RuleFor(x => x.Version).SeenVersion();
    }
}
