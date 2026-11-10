using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Features.Mvpi;

/// <summary>A report raised from this one: a follow-up, by its number and state.</summary>
public sealed record MvpiReportRef(Guid Id, string Number, MvpiReportStatus Status);

/// <summary>
/// A device adverse event report as the MDAE form has it. <paramref name="Missing"/>: what MvPI still needs before the report
/// can be recorded as sent, by field (empty when nothing is missing).
/// </summary>
public sealed record MvpiReportDto(
    Guid Id,
    string Number,
    MvpiReportStatus Status,
    DateOnly ReportedOn,
    Guid? IncidentId,
    string? IncidentNumber,
    Guid? FollowUpOfId,
    string? FollowUpOfNumber,
    string? FollowUpOfReference,
    IReadOnlyList<MvpiReportRef> FollowUps,
    Guid? PatientId,
    bool NoPatient,
    string? PatientInitials,
    string? Uhid,
    int? AgeYears,
    string? Sex,
    decimal? WeightKg,
    DateOnly EventOn,
    DateOnly AwareOn,
    string? Location,
    string? Description,
    MvpiSeriousness Seriousness,
    DateOnly? DeathOn,
    MvpiPatientOutcome Outcome,
    MvpiDevice Device,
    string ReporterName,
    string? ReporterDesignation,
    string? ReporterDepartment,
    DateOnly? SubmittedOn,
    string? SubmittedByName,
    string? Reference,
    string? ReferenceCorrectionReason,
    string? ReferenceCorrectedByName,
    DateTimeOffset? ReferenceCorrectedAt,
    IReadOnlyDictionary<string, string> Missing,
    uint Version);

public sealed record MvpiReportListItem(
    Guid Id,
    string Number,
    MvpiReportStatus Status,
    DateOnly ReportedOn,
    DateOnly EventOn,
    string? PatientInitials,
    string? Uhid,
    bool NoPatient,
    string? DeviceName,
    string? Manufacturer,
    bool Serious,
    MvpiPatientOutcome Outcome,
    string? IncidentNumber,
    string? FollowUpOfNumber,
    DateOnly? SubmittedOn,
    string? Reference,
    string ReporterName);

/// <summary>A device charged to the patient (from billing), to fill in the device: an implant's brand, maker, batch and serial.</summary>
public sealed record ChargedDeviceItem(
    string Description,
    string? Brand,
    string? Manufacturer,
    string? Specification,
    string? Batch,
    string? Serial,
    bool IsImplant,
    DateOnly UsedOn);

/// <summary>
/// What a new report starts from: the incident's event (its day, place and account) and patient, or the patient alone,
/// and the devices charged to the patient, the newest implant already filled in as the device.
/// </summary>
public sealed record MvpiPrefill(
    Guid? IncidentId,
    string? IncidentNumber,
    Guid? PatientId,
    string? PatientInitials,
    string? Uhid,
    int? AgeYears,
    string? Sex,
    DateOnly EventOn,
    DateOnly AwareOn,
    string? Location,
    string? Description,
    MvpiSeriousness Seriousness,
    MvpiPatientOutcome Outcome,
    MvpiDevice Device,
    IReadOnlyList<ChargedDeviceItem> Devices);

/// <summary>
/// The draft as written. <paramref name="IncidentId"/> and the patient are set when the report is raised; the patient may be
/// chosen later while it is a draft. <paramref name="Version"/>: when correcting the draft.
/// </summary>
public sealed record SaveMvpiReportRequest(
    Guid? IncidentId,
    Guid? PatientId,
    bool NoPatient,
    decimal? WeightKg,
    DateOnly EventOn,
    DateOnly AwareOn,
    string? Location,
    string? Description,
    MvpiSeriousness Seriousness,
    DateOnly? DeathOn,
    MvpiPatientOutcome Outcome,
    MvpiDevice Device,
    string? ReporterDesignation,
    string? ReporterDepartment,
    uint? Version = null);

/// <summary>Sent to MvPI on <paramref name="SubmittedOn"/>; <paramref name="SubmittedBy"/> defaults to whoever records it.</summary>
public sealed record SubmitMvpiReportRequest(DateOnly SubmittedOn, string? SubmittedBy, string? Reference, uint Version);

public sealed record CorrectMvpiReferenceRequest(string Reference, string Reason, uint Version);

internal sealed class SaveMvpiReportRequestValidator : AbstractValidator<SaveMvpiReportRequest>
{
    public SaveMvpiReportRequestValidator()
    {
        RuleFor(x => x.IncidentId).OptionalId();
        RuleFor(x => x.PatientId).OptionalId();
        RuleFor(x => x.NoPatient).Must((x, none) => !(none && x.PatientId is not null))
            .WithMessage("Choose the patient, or say that no patient was involved: not both.");
        RuleFor(x => x.WeightKg).InclusiveBetween(0.3m, 300m).When(x => x.WeightKg is not null);
        RuleFor(x => x.Location).SafeText(150);
        RuleFor(x => x.Description).SafeText(4000);
        RuleFor(x => x.Seriousness).Must(s => (s & ~(MvpiSeriousness)127) == 0).WithMessage("Choose from the listed reasons.");
        RuleFor(x => x.Outcome).IsInEnum();
        RuleFor(x => x.ReporterDesignation).SafeText(120);
        RuleFor(x => x.ReporterDepartment).SafeText(120);
        RuleFor(x => x.Device).NotNull().ChildRules(device =>
        {
            device.RuleFor(d => d.Name).SafeText(200);
            device.RuleFor(d => d.GenericName).SafeText(200);
            device.RuleFor(d => d.RiskClass).IsInEnum();
            device.RuleFor(d => d.Manufacturer).SafeText(200);
            device.RuleFor(d => d.ManufacturerAddress).SafeText(500);
            device.RuleFor(d => d.Importer).SafeText(500);
            device.RuleFor(d => d.ModelNumber).SafeText(80);
            device.RuleFor(d => d.CatalogueNumber).SafeText(80);
            device.RuleFor(d => d.SerialNumber).SafeText(80);
            device.RuleFor(d => d.LotNumber).SafeText(80);
            device.RuleFor(d => d.Udi).SafeText(120);
            device.RuleFor(d => d.SoftwareVersion).SafeText(60);
            device.RuleFor(d => d.Use).IsInEnum();
            device.RuleFor(d => d.OperatedBy).IsInEnum();
            device.RuleFor(d => d.Availability).IsInEnum();
            device.RuleFor(d => d.KeptAt).SafeText(200);
        });
    }
}

internal sealed class SubmitMvpiReportRequestValidator : AbstractValidator<SubmitMvpiReportRequest>
{
    public SubmitMvpiReportRequestValidator()
    {
        RuleFor(x => x.SubmittedBy).SafeText(200);
        RuleFor(x => x.Reference).SafeText(64);
    }
}

internal sealed class CorrectMvpiReferenceRequestValidator : AbstractValidator<CorrectMvpiReferenceRequest>
{
    public CorrectMvpiReferenceRequestValidator()
    {
        RuleFor(x => x.Reference).NotEmpty().SafeText(64);
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Say why the reference is changed.").SafeText(500);
    }
}
