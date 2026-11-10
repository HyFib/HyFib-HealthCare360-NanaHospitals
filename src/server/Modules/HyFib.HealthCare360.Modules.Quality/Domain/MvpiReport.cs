using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Domain;

/// <summary>
/// Why an adverse event with a medical device is serious (MvPI medical device adverse event reporting form, seriousness of
/// the event); none of them is a non-serious event.
/// </summary>
[Flags]
public enum MvpiSeriousness
{
    None = 0,
    Death = 1,
    LifeThreatening = 2,

    /// <summary>Hospitalisation, initial or prolonged.</summary>
    Hospitalisation = 4,
    Disability = 8,
    CongenitalAnomaly = 16,

    /// <summary>Required intervention to prevent permanent impairment or damage.</summary>
    RequiredIntervention = 32,
    Other = 64,
}

/// <summary>How the patient was at the time of the report.</summary>
public enum MvpiPatientOutcome
{
    Unknown = 0,
    Recovered,
    Recovering,
    NotRecovered,
    Fatal,
}

/// <summary>A device's risk class under the Medical Devices Rules 2017 (First Schedule): A low, B low to moderate, C moderate to high, D high.</summary>
public enum DeviceRiskClass
{
    A = 0,
    B,
    C,
    D,
}

public enum DeviceUse
{
    Unknown = 0,
    SingleUse,
    Reusable,
}

/// <summary>Who was operating the device when the event happened.</summary>
public enum DeviceOperator
{
    HealthProfessional = 0,
    Patient,
    Other,
}

/// <summary>Whether the device can be evaluated: the manufacturer or the monitoring centre may ask for it.</summary>
public enum DeviceAvailability
{
    /// <summary>Kept for evaluation, usually in quarantine (<see cref="MvpiDevice.KeptAt"/>).</summary>
    Available = 0,
    NotAvailable,
    ReturnedToManufacturer,
}

public enum MvpiReportStatus
{
    /// <summary>Being written by the quality or biomedical officer.</summary>
    Draft = 0,

    /// <summary>Sent to MvPI (the national coordination centre at IPC, or a medical device adverse event monitoring centre).</summary>
    Submitted,
}

/// <summary>
/// The device as the MDAE form identifies it: brand and generic name, risk class, manufacturer and importer, model,
/// catalogue, serial, lot and UDI, dates, software, implant dates, use, who operated it, and whether it can be evaluated.
/// </summary>
public sealed record MvpiDevice(
    string? Name,
    string? GenericName,
    DeviceRiskClass? RiskClass,
    string? Manufacturer,
    string? ManufacturerAddress,
    string? Importer,
    string? ModelNumber,
    string? CatalogueNumber,
    string? SerialNumber,
    string? LotNumber,
    string? Udi,
    DateOnly? ManufacturedOn,
    DateOnly? ExpiresOn,
    string? SoftwareVersion,
    bool Implantable,
    DateOnly? ImplantedOn,
    DateOnly? ExplantedOn,
    DeviceUse Use,
    bool Reprocessed,
    DeviceOperator OperatedBy,
    DeviceAvailability Availability,
    string? KeptAt,
    DateOnly? ReturnedOn)
{
    public static MvpiDevice Blank { get; } = new(
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, false, null, null, DeviceUse.Unknown, false,
        DeviceOperator.HealthProfessional, DeviceAvailability.Available, null, null);

    /// <summary>The device with its text trimmed (blank is null), and the dates that do not apply to it left out.</summary>
    public MvpiDevice Cleaned() => this with
    {
        Name = Clean(Name),
        GenericName = Clean(GenericName),
        Manufacturer = Clean(Manufacturer),
        ManufacturerAddress = Clean(ManufacturerAddress),
        Importer = Clean(Importer),
        ModelNumber = Clean(ModelNumber),
        CatalogueNumber = Clean(CatalogueNumber),
        SerialNumber = Clean(SerialNumber),
        LotNumber = Clean(LotNumber),
        Udi = Clean(Udi),
        SoftwareVersion = Clean(SoftwareVersion),
        ImplantedOn = Implantable ? ImplantedOn : null,
        ExplantedOn = Implantable ? ExplantedOn : null,
        KeptAt = Availability == DeviceAvailability.Available ? Clean(KeptAt) : null,
        ReturnedOn = Availability == DeviceAvailability.ReturnedToManufacturer ? ReturnedOn : null,
    };

    /// <summary>Whether anything names this one device among others of its kind: model, catalogue, serial, lot or UDI.</summary>
    public bool IsIdentified => new[] { ModelNumber, CatalogueNumber, SerialNumber, LotNumber, Udi }.Any(v => !string.IsNullOrWhiteSpace(v));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The patient as the form names them: by initials, with their age at the event and sex (from the registration).</summary>
public sealed record MvpiPatient(Guid Id, string FullName, DateOnly? DateOfBirth, string Sex);

/// <summary>What the officer records on a draft.</summary>
public sealed record MvpiDetails(
    MvpiPatient? Patient,
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
    string? ReporterDepartment);

/// <summary>
/// An adverse event involving a medical device, reported to the Materiovigilance Programme of India (MvPI, run by the
/// Indian Pharmacopoeia Commission under the Medical Devices Rules 2017) on its medical device adverse event (MDAE)
/// reporting form: the patient by initials (or none), the event and its seriousness, the device, and the reporter. The
/// quality team raises it, usually from an incident of the register, writes it as a draft and records it as sent with
/// MvPI's reference. A sent report does not change: what is learned later goes on a follow-up report that names it, and
/// only its reference can be corrected, with the reason.
/// </summary>
public sealed class MvpiReport : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>"{FAC}MDAE{YY}{SEQ:5}".</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>The incident of the register it was raised from, if any.</summary>
    public Guid? IncidentId { get; set; }

    /// <summary>For a follow-up report, the report it follows up (sent already).</summary>
    public Guid? FollowUpOfId { get; private set; }

    public MvpiReportStatus Status { get; private set; }

    /// <summary>The date of this report, as the form asks: the day it was started.</summary>
    public DateOnly ReportedOn { get; set; }

    public Guid? PatientId { get; private set; }

    /// <summary>The officer said no patient was involved (a device that failed on a check, or harmed a staff member).</summary>
    public bool NoPatientInvolved { get; private set; }

    /// <summary>As the form names the patient: "L. N." for Lakshmi Narayanan, kept as it was sent.</summary>
    public string? PatientInitials { get; private set; }

    /// <summary>Age in completed years on the day of the event, when the date of birth is known.</summary>
    public int? PatientAgeYears { get; private set; }

    public string? PatientSex { get; private set; }
    public decimal? WeightKg { get; private set; }

    public DateOnly EventOn { get; private set; }

    /// <summary>When the hospital became aware of the event.</summary>
    public DateOnly AwareOn { get; private set; }

    public string? Location { get; private set; }
    public string? Description { get; private set; }
    public MvpiSeriousness Seriousness { get; private set; }
    public DateOnly? DeathOn { get; private set; }
    public MvpiPatientOutcome Outcome { get; private set; }
    public MvpiDevice Device { get; private set; } = MvpiDevice.Blank;

    public Guid? ReportedBy { get; set; }
    public string ReporterName { get; set; } = string.Empty;
    public string? ReporterDesignation { get; private set; }
    public string? ReporterDepartment { get; private set; }

    public DateOnly? SubmittedOn { get; private set; }
    public Guid? SubmittedBy { get; private set; }
    public string? SubmittedByName { get; private set; }

    /// <summary>MvPI's (or the monitoring centre's) reference for the report.</summary>
    public string? Reference { get; private set; }

    /// <summary>Why the reference was changed after the report was sent; the data audit keeps the one it replaced.</summary>
    public string? ReferenceCorrectionReason { get; private set; }

    public string? ReferenceCorrectedByName { get; private set; }
    public DateTimeOffset? ReferenceCorrectedAt { get; private set; }

    public bool IsSerious => Seriousness != MvpiSeriousness.None;

    /// <summary>Records the draft. Only how the facts fit together is checked here; what MvPI needs is checked on sending.</summary>
    public Result Record(MvpiDetails details, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (Status == MvpiReportStatus.Submitted)
        {
            return Error.Rule("mvpi.submitted", "The report has been sent to MvPI. Add a follow-up report for what is learned since.");
        }

        var errors = new Dictionary<string, string[]>();
        if (details.Patient is not null && details.NoPatient)
        {
            errors["patientId"] = ["Choose the patient, or say that no patient was involved: not both."];
        }

        if (details.EventOn > today)
        {
            errors["eventOn"] = ["The event cannot be in the future."];
        }

        if (details.AwareOn < details.EventOn || details.AwareOn > today)
        {
            errors["awareOn"] = ["The hospital became aware of it on or after the event, and not in the future."];
        }

        if (details.Outcome == MvpiPatientOutcome.Fatal != details.Seriousness.HasFlag(MvpiSeriousness.Death))
        {
            errors["outcome"] = ["A fatal outcome and death as the seriousness go together."];
        }

        if (details.DeathOn is { } died && (!details.Seriousness.HasFlag(MvpiSeriousness.Death) || died < details.EventOn || died > today))
        {
            errors["deathOn"] = ["The date of death goes with death as the seriousness: on or after the event, and not in the future."];
        }

        foreach (var (field, message) in DeviceProblems(details.Device, details.EventOn, today))
        {
            errors[field] = [message];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("mvpi.invalid", "Some details of the report do not fit together.", errors);
        }

        PatientId = details.Patient?.Id;
        NoPatientInvolved = details.NoPatient;
        PatientInitials = details.Patient is { } p ? PatientNames.Initials(p.FullName) : null;
        PatientAgeYears = details.Patient?.DateOfBirth is { } dob ? AgeOn(dob, details.EventOn) : null;
        PatientSex = details.Patient?.Sex;
        WeightKg = details.Patient is null ? null : details.WeightKg;
        EventOn = details.EventOn;
        AwareOn = details.AwareOn;
        Location = Clean(details.Location);
        Description = Clean(details.Description);
        Seriousness = details.Seriousness;
        DeathOn = details.DeathOn;
        Outcome = details.Outcome;
        Device = details.Device.Cleaned();
        ReporterDesignation = Clean(details.ReporterDesignation);
        ReporterDepartment = Clean(details.ReporterDepartment);
        return Result.Success();
    }

    /// <summary>
    /// What MvPI needs before the report can go, field by field (empty when nothing is missing): the patient or that there
    /// was none, the event, the device named well enough to find it again, and the reporter's designation.
    /// </summary>
    public IReadOnlyDictionary<string, string> Missing()
    {
        var missing = new Dictionary<string, string>();
        if (PatientId is null && !NoPatientInvolved)
        {
            missing["patientId"] = "Choose the patient, or say that no patient was involved.";
        }

        if (string.IsNullOrWhiteSpace(Location))
        {
            missing["location"] = "Say where the event happened.";
        }

        if (string.IsNullOrWhiteSpace(Description))
        {
            missing["description"] = "Describe the event.";
        }

        if (Seriousness.HasFlag(MvpiSeriousness.Death) && DeathOn is null)
        {
            missing["deathOn"] = "Give the date of death.";
        }

        if (Device.Name is null)
        {
            missing["device.name"] = "Name the device (its brand or trade name).";
        }

        if (Device.Manufacturer is null)
        {
            missing["device.manufacturer"] = "Name the device's manufacturer.";
        }

        if (!Device.IsIdentified)
        {
            missing["device.serialNumber"] = "Give the device's model, catalogue, serial or lot number, or its UDI.";
        }

        if (Device.Implantable && Device.ImplantedOn is null)
        {
            missing["device.implantedOn"] = "Give the date the device was implanted.";
        }

        if (Device.Availability == DeviceAvailability.Available && Device.KeptAt is null)
        {
            missing["device.keptAt"] = "Say where the device is kept for evaluation.";
        }

        if (Device.Availability == DeviceAvailability.ReturnedToManufacturer && Device.ReturnedOn is null)
        {
            missing["device.returnedOn"] = "Give the date the device went back to the manufacturer.";
        }

        if (ReporterDesignation is null)
        {
            missing["reporterDesignation"] = "Give the reporter's designation.";
        }

        return missing;
    }

    /// <summary>Records that the report was sent to MvPI, on what day and by whom; it cannot change after.</summary>
    public Result Submit(DateOnly submittedOn, Guid? by, string byName, string? reference, DateOnly today)
    {
        if (Status == MvpiReportStatus.Submitted)
        {
            return Error.Rule("mvpi.submitted", "The report has already been sent to MvPI.");
        }

        var missing = Missing();
        if (missing.Count > 0)
        {
            return Error.Validation("mvpi.incomplete", "Fill in what MvPI asks for before recording the report as sent.",
                missing.ToDictionary(m => m.Key, m => new[] { m.Value }));
        }

        if (submittedOn > today || submittedOn < AwareOn)
        {
            return Error.Field("submittedOn", "Give the date it was sent: on or after the hospital became aware of the event, and not in the future.");
        }

        SubmittedOn = submittedOn;
        SubmittedBy = by;
        SubmittedByName = byName.Trim();
        Reference = Clean(reference);
        Status = MvpiReportStatus.Submitted;
        return Result.Success();
    }

    /// <summary>
    /// Corrects (or adds) MvPI's reference of a report already sent, which is all that may change of it: a reference typed
    /// wrong, or one MvPI gave after the report was e-mailed. The reason is kept with it.
    /// </summary>
    public Result CorrectReference(string reference, string reason, string byName, DateTimeOffset at)
    {
        if (Status != MvpiReportStatus.Submitted)
        {
            return Error.Rule("mvpi.not_submitted", "A draft's reference is recorded when it is sent.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("reason", "Say why the reference is changed.");
        }

        var cleaned = Clean(reference);
        if (cleaned is null)
        {
            return Error.Field("reference", "Give MvPI's reference.");
        }

        if (string.Equals(cleaned, Reference, StringComparison.Ordinal))
        {
            return Error.Field("reference", "That is the reference already recorded.");
        }

        Reference = cleaned;
        ReferenceCorrectionReason = reason.Trim();
        ReferenceCorrectedByName = byName.Trim();
        ReferenceCorrectedAt = at;
        return Result.Success();
    }

    /// <summary>
    /// A follow-up report to this one, which has been sent: a new draft with the same patient, event and device to bring
    /// up to date, naming this report (and through it MvPI's reference).
    /// </summary>
    public Result<MvpiReport> FollowUp(DateOnly today)
    {
        if (Status != MvpiReportStatus.Submitted)
        {
            return Error.Rule("mvpi.not_submitted", "Correct the draft itself; a follow-up is for a report already sent.");
        }

        return new MvpiReport
        {
            FacilityId = FacilityId,
            IncidentId = IncidentId,
            FollowUpOfId = Id,
            ReportedOn = today,
            PatientId = PatientId,
            NoPatientInvolved = NoPatientInvolved,
            PatientInitials = PatientInitials,
            PatientAgeYears = PatientAgeYears,
            PatientSex = PatientSex,
            WeightKg = WeightKg,
            EventOn = EventOn,
            AwareOn = AwareOn,
            Location = Location,
            Description = Description,
            Seriousness = Seriousness,
            DeathOn = DeathOn,
            Outcome = Outcome,
            Device = Device,
            ReporterDesignation = ReporterDesignation,
            ReporterDepartment = ReporterDepartment,
        };
    }

    /// <summary>Completed years from <paramref name="born"/> to <paramref name="on"/>.</summary>
    public static int AgeOn(DateOnly born, DateOnly on) =>
        Math.Max(0, on.Year - born.Year - (on < born.AddYears(on.Year - born.Year) ? 1 : 0));

    private static IEnumerable<(string Field, string Message)> DeviceProblems(MvpiDevice device, DateOnly eventOn, DateOnly today)
    {
        if (device.ManufacturedOn is { } made && (made > today || (device.ExpiresOn is { } expires && expires < made)))
        {
            yield return ("device.manufacturedOn", "A device is made before it expires, and not in the future.");
        }

        if (device.Implantable && device.ImplantedOn is { } implanted && implanted > eventOn)
        {
            yield return ("device.implantedOn", "The device was implanted on or before the event.");
        }

        if (device.Implantable && device.ExplantedOn is { } explanted
            && (explanted > today || (device.ImplantedOn is { } i && explanted < i)))
        {
            yield return ("device.explantedOn", "A device is explanted after it is implanted, and not in the future.");
        }

        if (device.Availability == DeviceAvailability.ReturnedToManufacturer && device.ReturnedOn is { } returned
            && (returned < eventOn || returned > today))
        {
            yield return ("device.returnedOn", "The device went back to the manufacturer after the event, and not in the future.");
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
