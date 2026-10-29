namespace HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;

public enum MlcCause
{
    RoadTrafficAccident = 0,
    Assault,
    SexualAssault,
    Burns,
    Poisoning,
    Fall,
    Hanging,
    Drowning,
    AnimalBite,
    Firearm,
    IndustrialAccident,
    Unknown,
    Other,
}

public enum MlcBroughtBy
{
    Self = 0,
    Relative,
    Police,

    /// <summary>
    /// A member of the public: a Good Samaritan, who is not compelled to give their name, address or other personal details,
    /// for the medico-legal case form or otherwise (Motor Vehicles Act 1988, s.134A; Central Motor Vehicles Rules 1989,
    /// rule 168).
    /// </summary>
    Public,
    Ambulance,
}

public enum MlcIntimationMode
{
    Phone = 0,
    Written,
    InPerson,
}

/// <summary>Who was told of a sexual offence against a child (POCSO Act 2012, s.19(1)).</summary>
public enum PocsoReportedTo
{
    SpecialJuvenilePoliceUnit = 0,
    LocalPolice,
}

/// <summary>Who was present at a child's medical examination (POCSO Act 2012, s.27(3) and (4)).</summary>
public enum ExaminationAttendant
{
    Parent = 0,

    /// <summary>A person in whom the child reposes trust or confidence.</summary>
    TrustedPerson,

    /// <summary>A woman nominated by the head of the hospital, when neither of the others can be present.</summary>
    WomanNominatedByHead,
}

/// <summary>A safeguard of the POCSO Act 2012, s.27, for the medical examination of a child.</summary>
public enum PocsoSafeguard
{
    /// <summary>A girl child is examined by a woman doctor (s.27(2)).</summary>
    WomanDoctor = 0,

    /// <summary>A parent, a person the child trusts or a woman the head of the hospital nominated is present, and named (s.27(3)–(4)).</summary>
    PersonPresent,
}

/// <summary>The medical examination of a victim of sexual assault, as the examining doctor records it.</summary>
/// <param name="ExaminerId">The examining doctor in the practitioner master.</param>
/// <param name="ExaminerRegistration">The examining doctor's council and registration number.</param>
/// <param name="ConsentGivenBy">Who consented: the patient, or a person competent to consent for them, with their relation.</param>
/// <param name="AttendantName">Who was present, for a child: their name (and, for a nominated woman, her post).</param>
public sealed record SexualAssaultExamination(
    Guid ExaminerId, string ExaminerName, string? ExaminerRegistration, Gender ExaminerGender, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    string ConsentGivenBy, ExaminationAttendant? AttendantKind, string? AttendantName);

/// <summary>
/// A medico-legal case: the register entry the hospital keeps for an injury or poisoning the police must know of — who
/// brought the patient, what is alleged to have happened, the injuries as the doctor found them — and the intimation to
/// the police station, with who received it. The number goes on the visit (and the stay, and the chart, which is then
/// kept without a retention limit). An entry is corrected, not deleted; once the police are informed the facts they
/// were told stay as they were.
/// <para>
/// A case of sexual assault keeps its medical examination as the Bharatiya Nagarik Suraksha Sanhita 2023, s.184
/// (formerly CrPC s.164A) asks: by whom, its exact start and end, with whose consent, and when its report went to the
/// investigating officer, within seven days. A child's case (under 18) comes under the Protection of Children from
/// Sexual Offences Act 2012: the report names the Special Juvenile Police Unit or the local police (s.19), a girl is
/// examined by a woman doctor, and a parent, a person the child trusts or a woman the head of the hospital nominates
/// is present (s.27). A case found to be a child's only after its examination was recorded keeps the examination as it
/// was done, and shows which of those safeguards it lacks: the report still goes to the investigating officer.
/// </para>
/// </summary>
public sealed class MlcRecord : AuditableEntity, IFacilityScoped
{
    /// <summary>The hospital's rule for informing the police: within an hour of the case being identified.</summary>
    public static readonly TimeSpan IntimateWithin = TimeSpan.FromHours(1);

    /// <summary>The examination's report goes to the investigating officer within seven days (BNSS 2023, s.184(6)).</summary>
    public static readonly TimeSpan ForwardReportWithin = TimeSpan.FromDays(7);

    /// <summary>How far a time a desk sends as "now" may run ahead of the server's clock and still be taken as now.</summary>
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid VisitId { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateTimeOffset IdentifiedAt { get; set; }
    public MlcBroughtBy BroughtBy { get; private set; }
    public string? BroughtByName { get; private set; }
    public MlcCause Cause { get; private set; }
    public DateTimeOffset? IncidentAt { get; private set; }
    public string? IncidentPlace { get; private set; }

    /// <summary>The injuries as examined: kind, site, size, and whether simple or grievous.</summary>
    public string Injuries { get; private set; } = string.Empty;

    public bool? ConsciousOnArrival { get; private set; }
    public bool? SmellOfAlcohol { get; private set; }
    public string DoctorName { get; set; } = string.Empty;
    public string? DoctorRegistrationNumber { get; set; }
    public string PoliceStation { get; private set; } = string.Empty;
    public DateTimeOffset? IntimatedAt { get; private set; }
    public MlcIntimationMode? IntimationMode { get; private set; }

    /// <summary>The officer who received the intimation, with their number.</summary>
    public string? ReceivedBy { get; private set; }

    /// <summary>The police station's general diary (GD/DD) entry number for the intimation.</summary>
    public string? PoliceEntryNumber { get; private set; }

    /// <summary>The patient was under 18 on the day of the incident, or of the case when that is not known.</summary>
    public bool IsChild { get; private set; }

    /// <summary>For a sexual offence against a child: told to the Special Juvenile Police Unit or the local police.</summary>
    public PocsoReportedTo? ReportedTo { get; private set; }

    public Guid? ExaminerId { get; private set; }
    public string? ExaminerName { get; private set; }
    public string? ExaminerRegistration { get; private set; }

    /// <summary>The examining doctor's gender when the examination was recorded, which s.27(2) asks of a girl child's.</summary>
    public Gender? ExaminerGender { get; private set; }

    public DateTimeOffset? ExaminationStartedAt { get; private set; }
    public DateTimeOffset? ExaminationEndedAt { get; private set; }
    public string? ConsentGivenBy { get; private set; }
    public ExaminationAttendant? AttendantKind { get; private set; }
    public string? AttendantName { get; private set; }

    /// <summary>The investigating officer the examination's report went to, with their rank and police station.</summary>
    public string? InvestigatingOfficer { get; private set; }

    public DateTimeOffset? ReportForwardedAt { get; private set; }

    public bool IsIntimated => IntimatedAt is not null;

    /// <summary>A sexual offence against a child: the POCSO Act 2012 applies.</summary>
    public bool IsPocso => Cause == MlcCause.SexualAssault && IsChild;

    /// <summary>When the examination's report is due at the investigating officer; null until the examination is recorded.</summary>
    public DateTimeOffset? ReportDueAt => ExaminationEndedAt + ForwardReportWithin;

    public bool IntimationOverdue(DateTimeOffset now) => !IsIntimated && now - IdentifiedAt > IntimateWithin;

    public bool ReportOverdue(DateTimeOffset now) => ReportDueAt is { } due && ReportForwardedAt is null && now > due;

    /// <summary>
    /// The safeguards of s.27 a child's recorded examination lacks. The examination itself is refused for a child without
    /// them, so this is a case found to be a child's only after its examination was recorded: empty for any other.
    /// </summary>
    public IReadOnlyList<PocsoSafeguard> MissingSafeguards(Gender patientGender)
    {
        if (!IsPocso || ExaminationEndedAt is null)
        {
            return [];
        }

        var missing = new List<PocsoSafeguard>();
        if (patientGender == Gender.Female && ExaminerGender != Gender.Female)
        {
            missing.Add(PocsoSafeguard.WomanDoctor);
        }

        if (AttendantName is null)
        {
            missing.Add(PocsoSafeguard.PersonPresent);
        }

        return missing;
    }

    /// <param name="isChild">The patient was under 18 on the day of the incident (<see cref="IsChild"/>).</param>
    public Result Describe(
        MlcBroughtBy broughtBy, string? broughtByName, MlcCause cause, DateTimeOffset? incidentAt, string? incidentPlace, string injuries,
        bool? consciousOnArrival, bool? smellOfAlcohol, string policeStation, bool isChild, DateTimeOffset now)
    {
        if (IsIntimated)
        {
            return Error.Rule("mlc.intimated", "The police have been informed of this case; its facts stay as they were told.");
        }

        if (string.IsNullOrWhiteSpace(injuries))
        {
            return Error.Field("injuries", "Describe the injuries as examined.");
        }

        if (string.IsNullOrWhiteSpace(policeStation))
        {
            return Error.Field("policeStation", "Name the police station to inform.");
        }

        if (incidentAt > now)
        {
            return Error.Field("incidentAt", "The incident cannot be in the future.");
        }

        // A Good Samaritan gives their name only if they wish to (Central Motor Vehicles Rules 1989, rule 168).
        if (broughtBy is not (MlcBroughtBy.Self or MlcBroughtBy.Public) && string.IsNullOrWhiteSpace(broughtByName))
        {
            return Error.Field("broughtByName", "Name who brought the patient (and the police officer's number, if police).");
        }

        BroughtBy = broughtBy;
        BroughtByName = string.IsNullOrWhiteSpace(broughtByName) ? null : broughtByName.Trim();
        Cause = cause;
        IncidentAt = incidentAt;
        IncidentPlace = string.IsNullOrWhiteSpace(incidentPlace) ? null : incidentPlace.Trim();
        Injuries = injuries.Trim();
        ConsciousOnArrival = consciousOnArrival;
        SmellOfAlcohol = smellOfAlcohol;
        PoliceStation = policeStation.Trim();
        IsChild = isChild;
        return Result.Success();
    }

    /// <param name="reportedTo">For a sexual offence against a child: the Special Juvenile Police Unit or the local police (POCSO s.19(1)).</param>
    public Result Intimate(
        DateTimeOffset at, MlcIntimationMode mode, string receivedBy, string? policeEntryNumber, PocsoReportedTo? reportedTo, DateTimeOffset now)
    {
        if (IsIntimated)
        {
            return Error.Rule("mlc.intimated", "The police intimation is already recorded.");
        }

        if (at > now + ClockTolerance || at < IdentifiedAt.AddMinutes(-5))
        {
            return Error.Field("intimatedAt", "The police are informed after the case is identified, and not in the future.");
        }

        if (string.IsNullOrWhiteSpace(receivedBy))
        {
            return Error.Field("receivedBy", "Name the officer who received the intimation, with their number.");
        }

        if (IsPocso && reportedTo is null)
        {
            return Error.Field("reportedTo",
                "A sexual offence against a child is reported to the Special Juvenile Police Unit or the local police (POCSO Act s.19(1)): say which.");
        }

        IntimatedAt = at;
        IntimationMode = mode;
        ReceivedBy = receivedBy.Trim();
        PoliceEntryNumber = string.IsNullOrWhiteSpace(policeEntryNumber) ? null : policeEntryNumber.Trim();
        ReportedTo = IsPocso ? reportedTo : null;
        return Result.Success();
    }

    /// <summary>
    /// Records the medical examination of a victim of sexual assault: by whom, its exact start and end, and with whose
    /// consent (BNSS 2023, s.184(4), (5) and (7)); for a child, a girl by a woman doctor, with a parent, a person the child
    /// trusts or a woman the head of the hospital nominated present (POCSO Act 2012, s.27(2)–(4)). It may be corrected
    /// until its report goes to the investigating officer.
    /// </summary>
    public Result RecordExamination(SexualAssaultExamination examination, Gender patientGender, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(examination);
        if (Cause != MlcCause.SexualAssault)
        {
            return Error.Rule("mlc.not_sexual_assault", "This examination is recorded for a case of sexual assault.");
        }

        if (ReportForwardedAt is not null)
        {
            return Error.Rule("mlc.report_forwarded", "The examination's report has gone to the investigating officer: the examination stays as it was.");
        }

        if (examination.EndedAt < examination.StartedAt || examination.EndedAt > now + ClockTolerance)
        {
            return Error.Field("endedAt", "The examination ends after it starts, and not in the future.");
        }

        if (IncidentAt is { } incident && examination.StartedAt < incident)
        {
            return Error.Field("startedAt", "The examination starts after the incident.");
        }

        if (string.IsNullOrWhiteSpace(examination.ConsentGivenBy))
        {
            return Error.Field("consentGivenBy",
                "Say who consented to the examination: the patient, or a person competent to consent for them (BNSS s.184(7)).");
        }

        if (IsChild && patientGender == Gender.Female && examination.ExaminerGender != Gender.Female)
        {
            return Error.Rule("pocso.woman_doctor", "A girl child is examined by a woman doctor (POCSO Act s.27(2)).");
        }

        if (IsChild && (examination.AttendantKind is null || string.IsNullOrWhiteSpace(examination.AttendantName)))
        {
            return Error.Field("attendantName",
                "Name who was present: the child's parent, a person the child trusts, or a woman the head of the hospital nominated (POCSO Act s.27(3)–(4)).");
        }

        ExaminerId = examination.ExaminerId;
        ExaminerName = examination.ExaminerName.Trim();
        ExaminerRegistration = string.IsNullOrWhiteSpace(examination.ExaminerRegistration) ? null : examination.ExaminerRegistration.Trim();
        ExaminerGender = examination.ExaminerGender;
        ExaminationStartedAt = examination.StartedAt;
        ExaminationEndedAt = examination.EndedAt;
        ConsentGivenBy = examination.ConsentGivenBy.Trim();
        AttendantKind = IsChild ? examination.AttendantKind : null;
        AttendantName = IsChild ? examination.AttendantName!.Trim() : null;
        return Result.Success();
    }

    /// <summary>Records that the examination's report went to the investigating officer, due within seven days (BNSS s.184(6)).</summary>
    public Result ForwardReport(DateTimeOffset at, string investigatingOfficer, DateTimeOffset now)
    {
        if (ExaminationEndedAt is not { } ended)
        {
            return Error.Rule("mlc.no_examination", "Record the examination first: its report is what goes to the investigating officer.");
        }

        if (ReportForwardedAt is not null)
        {
            return Error.Rule("mlc.report_forwarded", "The report's forwarding is already recorded.");
        }

        if (at < ended || at > now + ClockTolerance)
        {
            return Error.Field("forwardedAt", "The report goes after the examination, and not in the future.");
        }

        if (string.IsNullOrWhiteSpace(investigatingOfficer))
        {
            return Error.Field("investigatingOfficer", "Name the investigating officer, with their rank and police station.");
        }

        ReportForwardedAt = at;
        InvestigatingOfficer = investigatingOfficer.Trim();
        return Result.Success();
    }
}
