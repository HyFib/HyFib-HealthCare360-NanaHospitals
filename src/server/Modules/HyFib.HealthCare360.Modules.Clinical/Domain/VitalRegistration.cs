namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>
/// Where a birth or death recorded here stands with the Registrar of Births and Deaths. An institution reports each one
/// within 21 days (Registration of Births and Deaths Act 1969, s.8 and s.12, as amended in 2023), today through the Civil
/// Registration System (CRS), which gives back the registration number.
/// </summary>
public enum VitalReportStatus
{
    /// <summary>Recorded here; not yet reported to the Registrar.</summary>
    Recorded = 1,

    /// <summary>Reported, with the Registrar's registration number.</summary>
    Reported = 2,

    /// <summary>Recorded in error (the wrong patient, a duplicate): kept, but out of the register.</summary>
    EnteredInError = 3,
}

public enum BirthOutcome
{
    LiveBirth = 1,

    /// <summary>A foetus of at least 28 weeks born dead (RBD Act s.2(1)(i)); reported on Form 3 with the cause.</summary>
    StillBirth = 2,
}

public enum BirthSex
{
    Male = 1,
    Female = 2,
    Indeterminate = 3,
}

/// <summary>Method of delivery, as Form 1's statistical part asks it.</summary>
public enum DeliveryMethod
{
    Natural = 1,
    Caesarean = 2,
    ForcepsOrVacuum = 3,
}

/// <summary>Who attended the delivery (Form 1, statistical part).</summary>
public enum BirthAttendant
{
    Doctor = 1,
    NurseOrMidwife = 2,
    Other = 3,
}

/// <summary>The manner of death the MCCD (Form 4) records.</summary>
public enum DeathManner
{
    Natural = 1,
    Accident = 2,
    Suicide = 3,
    Homicide = 4,

    /// <summary>A medico-legal case whose manner the police or the post-mortem will decide.</summary>
    PendingInvestigation = 5,
}

/// <summary>For a woman of reproductive age, whether the death came in pregnancy or soon after (MCCD Form 4).</summary>
public enum PregnancyAtDeath
{
    NotApplicable = 0,
    NotPregnant = 1,
    Pregnant = 2,

    /// <summary>Within 42 days of the end of a pregnancy: a possible maternal death, which is also reviewed.</summary>
    WithinSixWeeksOfDelivery = 3,
}

/// <summary>One line of Part I of the MCCD: a condition and how long before death it began ("2 days", "10 years").</summary>
public sealed record CauseOfDeath(string Condition, string? Interval);

/// <summary>What Form 1 (a live birth) or Form 3 (a still birth) needs from the hospital.</summary>
public sealed record BirthDetails(
    DateTimeOffset BornAt,
    BirthOutcome Outcome,
    BirthSex Sex,
    int? WeightGrams,
    int? WeeksOfGestation,
    DeliveryMethod Method,
    BirthAttendant Attendant,
    string? ChildName,
    string? FatherName,
    int MotherAgeYears,
    int? LiveChildren,
    string InformantName,
    string? StillBirthCause);

/// <summary>What the medical certificate of cause of death (Form 4) records.</summary>
public sealed record DeathCertificate(
    DateTimeOffset DiedAt,
    IReadOnlyList<CauseOfDeath> Causes,
    string? OtherConditions,
    DeathManner Manner,
    bool MedicoLegal,
    PregnancyAtDeath Pregnancy,
    string CertifiedBy,
    string CertifierRegistrationNumber);

/// <summary>The Registrar's acknowledgement of a reported event.</summary>
public sealed record RegistrarReport(DateOnly ReportedOn, string RegistrationNumber);

/// <summary>
/// A birth in the hospital (Form 1, or Form 3 for a still birth), recorded against the mother, and the baby's own record
/// once registered. Reported to the Registrar within 21 days (<see cref="VitalReportStatus"/>).
/// </summary>
public sealed class BirthRecord : AuditableEntity, IFacilityScoped
{
    /// <summary>Below it, the weight was mis-typed.</summary>
    public const int MinWeightGrams = 300;

    public const int MaxWeightGrams = 7000;

    /// <summary>A foetus born dead from this gestation is a still birth (RBD Act s.2(1)(i)).</summary>
    public const int StillBirthWeeks = 28;

    public Guid FacilityId { get; set; }
    public Guid MotherPatientId { get; set; }
    public Guid? BabyPatientId { get; set; }
    public Guid? AdmissionId { get; set; }

    /// <summary>The hospital's birth register number, from 1 each calendar year.</summary>
    public string SerialNumber { get; set; } = string.Empty;

    public VitalReportStatus Status { get; private set; } = VitalReportStatus.Recorded;
    public DateTimeOffset BornAt { get; private set; }
    public BirthOutcome Outcome { get; private set; }
    public BirthSex Sex { get; private set; }
    public int? WeightGrams { get; private set; }
    public int? WeeksOfGestation { get; private set; }
    public DeliveryMethod Method { get; private set; }
    public BirthAttendant Attendant { get; private set; }
    public string? ChildName { get; private set; }
    public string? FatherName { get; private set; }
    public int MotherAgeYears { get; private set; }
    public int? LiveChildren { get; private set; }
    public string InformantName { get; private set; } = string.Empty;
    public string? StillBirthCause { get; private set; }
    public Guid? RecordedBy { get; private set; }

    public DateOnly? ReportedOn { get; private set; }
    public string? RegistrationNumber { get; private set; }
    public string? ErrorReason { get; private set; }

    /// <summary>Records the birth, or corrects it until it is reported.</summary>
    public Result Record(BirthDetails d, DateTimeOffset now, Guid? by)
    {
        if (Status != VitalReportStatus.Recorded)
        {
            return VitalRegistration.Closed(Status);
        }

        if (d.BornAt > now)
        {
            return Error.Field("bornAt", "The birth cannot be after now.");
        }

        if (d.WeightGrams is < MinWeightGrams or > MaxWeightGrams)
        {
            return Error.Field("weightGrams", $"Enter the birth weight in grams, from {MinWeightGrams} to {MaxWeightGrams}.");
        }

        if (d.WeeksOfGestation is < 20 or > 45)
        {
            return Error.Field("weeksOfGestation", "Enter the length of the pregnancy in weeks, from 20 to 45.");
        }

        if (d.MotherAgeYears is < 10 or > 60)
        {
            return Error.Field("motherAgeYears", "Enter the mother's age in years.");
        }

        if (string.IsNullOrWhiteSpace(d.InformantName))
        {
            return Error.Field("informantName", "Enter who gives the information (the hospital's officer in charge, or a relative).");
        }

        if (d.Outcome == BirthOutcome.StillBirth)
        {
            if (d.WeeksOfGestation is null or < StillBirthWeeks)
            {
                return Error.Field("weeksOfGestation", $"A still birth is a foetus of at least {StillBirthWeeks} weeks; an earlier loss is an abortion, not reported as a birth.");
            }

            if (string.IsNullOrWhiteSpace(d.StillBirthCause))
            {
                return Error.Field("stillBirthCause", "Enter the cause of the foetal death (Form 3).");
            }
        }

        BornAt = d.BornAt;
        Outcome = d.Outcome;
        Sex = d.Sex;
        WeightGrams = d.WeightGrams;
        WeeksOfGestation = d.WeeksOfGestation;
        Method = d.Method;
        Attendant = d.Attendant;
        ChildName = d.Outcome == BirthOutcome.LiveBirth ? Clean(d.ChildName) : null;
        FatherName = Clean(d.FatherName);
        MotherAgeYears = d.MotherAgeYears;
        LiveChildren = d.LiveChildren;
        InformantName = d.InformantName.Trim();
        StillBirthCause = d.Outcome == BirthOutcome.StillBirth ? d.StillBirthCause!.Trim() : null;
        RecordedBy = by;
        return Result.Success();
    }

    public Result Report(RegistrarReport report, DateOnly today) => VitalRegistration.Report(Status, report, BirthDate, today, (on, number) =>
    {
        (ReportedOn, RegistrationNumber, Status) = (on, number, VitalReportStatus.Reported);
    });

    /// <summary>Only the person who recorded the birth may mark it entered in error; anyone else asks them.</summary>
    public Result MarkEnteredInError(string reason, Guid? by) =>
        VitalRegistration.MarkError(Status, IsRecordedBy(by), "Only the person who recorded this birth can mark it as entered in error.", () =>
        {
            (Status, ErrorReason) = (VitalReportStatus.EnteredInError, reason.Trim());
        });

    public bool IsRecordedBy(Guid? user) => user is { } u && u == RecordedBy;

    /// <summary>The local calendar day of the birth, from which the 21 days run.</summary>
    public DateOnly BirthDate => VitalRegistration.LocalDate(BornAt);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A death in the hospital with its medical certificate of cause of death (MCCD, Form 4; RBD Act s.10(3)), which the
/// doctor who attended the patient in the last illness signs. Recording it marks the patient deceased. Reported to the
/// Registrar within 21 days.
/// </summary>
public sealed class DeathRecord : AuditableEntity, IFacilityScoped
{
    /// <summary>Part I has lines (a) to (d): the immediate cause, then the conditions that led to it, the underlying cause last.</summary>
    public const int MaxCauses = 4;

    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? AdmissionId { get; set; }

    /// <summary>The hospital's death register number, from 1 each calendar year.</summary>
    public string SerialNumber { get; set; } = string.Empty;

    public VitalReportStatus Status { get; private set; } = VitalReportStatus.Recorded;
    public DateTimeOffset DiedAt { get; private set; }
    public List<CauseOfDeath> Causes { get; private set; } = [];
    public string? OtherConditions { get; private set; }
    public DeathManner Manner { get; private set; }
    public bool MedicoLegal { get; private set; }
    public PregnancyAtDeath Pregnancy { get; private set; }
    public string CertifiedBy { get; private set; } = string.Empty;
    public string CertifierRegistrationNumber { get; private set; } = string.Empty;
    public Guid? CertifiedByUserId { get; private set; }
    public DateTimeOffset CertifiedAt { get; private set; }

    public DateOnly? ReportedOn { get; private set; }
    public string? RegistrationNumber { get; private set; }
    public string? ErrorReason { get; private set; }

    /// <summary>The underlying cause: the last line of Part I, the one mortality statistics count.</summary>
    public string UnderlyingCause => Causes.Count == 0 ? string.Empty : Causes[^1].Condition;

    /// <summary>
    /// Records the certificate, or corrects it until it is reported. <paramref name="womanOfReproductiveAge"/>: a woman
    /// aged 15 to 49, for whom the certificate must say whether she was pregnant.
    /// </summary>
    public Result Certify(DeathCertificate c, bool womanOfReproductiveAge, DateTimeOffset? bornAt, DateTimeOffset now, Guid? by)
    {
        if (Status != VitalReportStatus.Recorded)
        {
            return VitalRegistration.Closed(Status);
        }

        if (c.DiedAt > now)
        {
            return Error.Field("diedAt", "The death cannot be after now.");
        }

        if (bornAt is { } born && c.DiedAt < born)
        {
            return Error.Field("diedAt", "The death cannot be before the patient's birth.");
        }

        var causes = c.Causes.Where(x => !string.IsNullOrWhiteSpace(x.Condition)).ToList();
        if (causes.Count == 0 || causes.Count != c.Causes.Count)
        {
            return Error.Field("causes", "Enter the immediate cause (Part I, line a), and the conditions that led to it in order, without blank lines.");
        }

        if (causes.Count > MaxCauses)
        {
            return Error.Field("causes", "Part I has four lines, (a) to (d).");
        }

        if (womanOfReproductiveAge && c.Pregnancy == PregnancyAtDeath.NotApplicable)
        {
            return Error.Field("pregnancy", "For a woman aged 15 to 49, say whether she was pregnant or within six weeks of the end of a pregnancy.");
        }

        if (!womanOfReproductiveAge && c.Pregnancy != PregnancyAtDeath.NotApplicable)
        {
            return Error.Field("pregnancy", "Pregnancy is asked only for a woman aged 15 to 49.");
        }

        if (c.Manner == DeathManner.PendingInvestigation && !c.MedicoLegal)
        {
            return Error.Field("manner", "Only a medico-legal case waits for the investigation to decide the manner of death.");
        }

        if (string.IsNullOrWhiteSpace(c.CertifiedBy) || string.IsNullOrWhiteSpace(c.CertifierRegistrationNumber))
        {
            return Error.Field("certifiedBy", "Enter the certifying doctor's name and registration number.");
        }

        DiedAt = c.DiedAt;
        Causes = [.. causes.Select(x => new CauseOfDeath(x.Condition.Trim(), string.IsNullOrWhiteSpace(x.Interval) ? null : x.Interval.Trim()))];
        OtherConditions = string.IsNullOrWhiteSpace(c.OtherConditions) ? null : c.OtherConditions.Trim();
        Manner = c.Manner;
        MedicoLegal = c.MedicoLegal;
        Pregnancy = c.Pregnancy;
        CertifiedBy = c.CertifiedBy.Trim();
        CertifierRegistrationNumber = c.CertifierRegistrationNumber.Trim();
        CertifiedByUserId = by;
        CertifiedAt = now;
        return Result.Success();
    }

    public Result Report(RegistrarReport report, DateOnly today) => VitalRegistration.Report(Status, report, DeathDate, today, (on, number) =>
    {
        (ReportedOn, RegistrationNumber, Status) = (on, number, VitalReportStatus.Reported);
    });

    /// <summary>Only the doctor who certified the death may mark it entered in error; anyone else asks them.</summary>
    public Result MarkEnteredInError(string reason, Guid? by) =>
        VitalRegistration.MarkError(Status, IsCertifiedBy(by), "Only the doctor who certified this death can mark it as entered in error.", () =>
        {
            (Status, ErrorReason) = (VitalReportStatus.EnteredInError, reason.Trim());
        });

    public bool IsCertifiedBy(Guid? user) => user is { } u && u == CertifiedByUserId;

    /// <summary>The local calendar day of the death, from which the 21 days run.</summary>
    public DateOnly DeathDate => VitalRegistration.LocalDate(DiedAt);
}

/// <summary>The rules births and deaths share: the 21 days, the report, and an entry made in error.</summary>
public static class VitalRegistration
{
    /// <summary>An institution reports a birth or death to the Registrar within this many days (RBD Rules, rule 5).</summary>
    public const int ReportWithinDays = 21;

    private static readonly Lazy<TimeZoneInfo> India = new(() =>
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    });

    public static DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, India.Value).DateTime);

    /// <summary>The last day to report an event of <paramref name="eventDate"/>.</summary>
    public static DateOnly DueBy(DateOnly eventDate) => eventDate.AddDays(ReportWithinDays);

    internal static Result Report(VitalReportStatus status, RegistrarReport report, DateOnly eventDate, DateOnly today, Action<DateOnly, string> apply)
    {
        if (status != VitalReportStatus.Recorded)
        {
            return Closed(status);
        }

        if (string.IsNullOrWhiteSpace(report.RegistrationNumber))
        {
            return Error.Field("registrationNumber", "Enter the registration number the Registrar gave.");
        }

        if (report.ReportedOn < eventDate || report.ReportedOn > today)
        {
            return Error.Field("reportedOn", "The report comes on or after the event, and not after today.");
        }

        apply(report.ReportedOn, report.RegistrationNumber.Trim());
        return Result.Success();
    }

    internal static Result MarkError(VitalReportStatus status, bool isOwnEntry, string notOwn, Action apply)
    {
        if (status != VitalReportStatus.Recorded)
        {
            return Closed(status);
        }

        if (!isOwnEntry)
        {
            return Error.Forbidden("vital.not_own_entry", notOwn);
        }

        apply();
        return Result.Success();
    }

    internal static Error Closed(VitalReportStatus status) => status == VitalReportStatus.Reported
        ? Error.Rule("vital.reported", "This entry is reported to the Registrar; a correction goes through the Registrar (RBD Act s.15).")
        : Error.Rule("vital.entered_in_error", "This entry was marked as entered in error.");
}
