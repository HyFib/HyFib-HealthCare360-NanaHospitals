namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Mtp;

public enum MtpCaseStatus
{
    /// <summary>The woman's consent (Form C) and the opinion of the doctors (Form I) are recorded; the termination is not yet done.</summary>
    Certified = 1,

    /// <summary>The termination is done and entered in the admission register (Form III).</summary>
    Terminated = 2,

    /// <summary>Not done: the woman withdrew her consent, or she went elsewhere.</summary>
    Cancelled = 3,
}

/// <summary>The grounds of s.3(2) of the MTP Act on which the doctors formed their opinion in good faith.</summary>
public enum MtpGround
{
    /// <summary>Continuing the pregnancy would risk the woman's life.</summary>
    RiskToLife = 1,

    /// <summary>It would gravely injure her physical health.</summary>
    InjuryToPhysicalHealth = 2,

    /// <summary>It would gravely injure her mental health.</summary>
    InjuryToMentalHealth = 3,

    /// <summary>The pregnancy was caused by rape (Explanation 2: the anguish is presumed a grave injury to her mental health).</summary>
    Rape = 4,

    /// <summary>
    /// The failure of a device or method used by the woman or her partner to limit the number of children or prevent
    /// pregnancy (Explanation 1; up to 20 weeks).
    /// </summary>
    ContraceptiveFailure = 5,

    /// <summary>Substantial risk that the child, if born, would suffer from a serious physical or mental abnormality.</summary>
    FoetalAbnormality = 6,
}

/// <summary>
/// The women for whom a termination between 20 and 24 weeks may be done on the opinion of two doctors (MTP Rules 2003,
/// rule 3B, as amended in 2021).
/// </summary>
public enum MtpRule3BCategory
{
    SexualAssaultRapeOrIncest = 1,
    Minor = 2,
    ChangeOfMaritalStatus = 3,
    PhysicalDisability = 4,
    MentalIllness = 5,
    FoetalMalformation = 6,
    HumanitarianSettingOrEmergency = 7,
}

/// <summary>How the pregnancy was terminated (Form II, Form III).</summary>
public enum MtpMethod
{
    /// <summary>Medical method of abortion (mifepristone and misoprostol).</summary>
    Medical = 1,

    /// <summary>Manual or electric vacuum aspiration.</summary>
    VacuumAspiration = 2,

    /// <summary>Dilatation and evacuation.</summary>
    DilatationAndEvacuation = 3,

    /// <summary>Another method, named in the remarks.</summary>
    Other = 4,
}

/// <summary>Whose written consent was taken (MTP Act s.3(4); MTP Rules, Form C).</summary>
public enum MtpConsentBy
{
    /// <summary>The woman herself: an adult who is not mentally ill needs no one else's consent.</summary>
    Woman = 1,

    /// <summary>Her guardian, because she is under 18 or mentally ill (s.3(4)(a)).</summary>
    Guardian = 2,
}

/// <summary>One registered medical practitioner who formed the opinion of Form I.</summary>
public sealed record MtpOpinion(string DoctorName, string RegistrationNumber, DateOnly OpinionOn);

/// <summary>What Form C and Form I record before the termination.</summary>
public sealed record MtpCertification(
    int AgeYears,
    bool MentallyIll,
    MtpConsentBy ConsentBy,
    string? GuardianName,
    string? GuardianRelation,
    DateOnly ConsentOn,
    Guid? ConsentFileId,
    int WeeksOfGestation,
    IReadOnlyList<MtpGround> Grounds,
    MtpRule3BCategory? Category,
    string? MedicalBoardReference,
    IReadOnlyList<MtpOpinion> Opinions);

/// <summary>What the admission register (Form III) records once the termination is done.</summary>
public sealed record MtpTermination(DateOnly TerminatedOn, MtpMethod Method, string PerformedBy, DateOnly? DischargedOn, string? Outcome, string? Remarks);

/// <summary>
/// A termination of pregnancy under the MTP Act 1971 (as amended in 2021) and the MTP Regulations 2003. Before it is done:
/// the woman's written consent (Form C), or her guardian's when she is under 18 or mentally ill, and the opinion, formed
/// in good faith, of the doctors the length of the pregnancy calls for (Form I): one up to 20 weeks; two from 20 to 24
/// weeks, for a woman in a rule 3B category; and beyond 24 weeks only for a substantial foetal abnormality the State
/// Medical Board has diagnosed. Once done it is entered in the admission register (Form III), whose serial number is the
/// case's, and counted in the month's report to the Chief Medical Officer (Form II). Under s.5A the woman's name and
/// particulars are revealed to no one but a person authorised by law, so the case opens only to staff given the MTP
/// permissions, and certifying it puts the patient's record in the MTP category (R-4).
/// </summary>
public sealed class MtpCase : AuditableEntity, IFacilityScoped
{
    /// <summary>Up to this, one doctor's opinion is enough (s.3(2)(a)).</summary>
    public const int OneOpinionWeeks = 20;

    /// <summary>Up to this, two doctors' opinions for a woman in a rule 3B category (s.3(2)(b)).</summary>
    public const int TwoOpinionsWeeks = 24;

    /// <summary>The longest gestation a case may give: beyond it, the period was mis-typed.</summary>
    public const int MaxWeeksOfGestation = 42;

    public const int AgeOfMajority = 18;

    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? AdmissionId { get; set; }

    /// <summary>The serial number of the admission register (Form III), from 1 each calendar year.</summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>The place's certificate of approval (MTP Rules, Form B) when the case was certified, as the forms print it.</summary>
    public string ApprovalNumber { get; set; } = string.Empty;

    public MtpCaseStatus Status { get; private set; }

    public int AgeYears { get; private set; }
    public bool MentallyIll { get; private set; }
    public MtpConsentBy ConsentBy { get; private set; }
    public string? GuardianName { get; private set; }
    public string? GuardianRelation { get; private set; }
    public DateOnly ConsentOn { get; private set; }

    /// <summary>The signed consent (Form C), scanned.</summary>
    public Guid? ConsentFileId { get; private set; }

    public int WeeksOfGestation { get; private set; }
    public List<MtpGround> Grounds { get; private set; } = [];
    public MtpRule3BCategory? Category { get; private set; }

    /// <summary>The State Medical Board's opinion, for a termination beyond 24 weeks (s.3(2B), s.3(2C)).</summary>
    public string? MedicalBoardReference { get; private set; }

    public List<MtpOpinion> Opinions { get; private set; } = [];
    public DateTimeOffset CertifiedAt { get; private set; }
    public Guid? CertifiedBy { get; private set; }

    public DateOnly? TerminatedOn { get; private set; }
    public MtpMethod? Method { get; private set; }
    public string? PerformedBy { get; private set; }
    public DateOnly? DischargedOn { get; private set; }
    /// <summary>The result and condition at discharge (Form III).</summary>
    public string? Outcome { get; private set; }
    public string? Remarks { get; private set; }
    public DateTimeOffset? TerminationRecordedAt { get; private set; }

    public string? CancelReason { get; private set; }

    public bool IsClosed => Status is MtpCaseStatus.Terminated or MtpCaseStatus.Cancelled;

    /// <summary>
    /// Records the consent and the doctors' opinion, or corrects them before the termination is done. Refused when the
    /// law does not allow the termination on what was recorded.
    /// </summary>
    public Result Certify(MtpCertification c, DateOnly today, Guid? by, DateTimeOffset now)
    {
        if (IsClosed)
        {
            return Closed();
        }

        if (Check(c, today) is { } refused)
        {
            return refused;
        }

        AgeYears = c.AgeYears;
        MentallyIll = c.MentallyIll;
        ConsentBy = c.ConsentBy;
        (GuardianName, GuardianRelation) = c.ConsentBy == MtpConsentBy.Guardian ? (c.GuardianName!.Trim(), c.GuardianRelation!.Trim()) : (null, null);
        ConsentOn = c.ConsentOn;
        ConsentFileId = c.ConsentFileId;
        WeeksOfGestation = c.WeeksOfGestation;
        Grounds = [.. Enum.GetValues<MtpGround>().Where(c.Grounds.Contains)];
        Category = c.WeeksOfGestation > OneOpinionWeeks ? c.Category : null;
        MedicalBoardReference = c.WeeksOfGestation > TwoOpinionsWeeks ? c.MedicalBoardReference!.Trim() : null;
        Opinions = [.. c.Opinions.Select(o => o with { DoctorName = o.DoctorName.Trim(), RegistrationNumber = o.RegistrationNumber.Trim() })];
        CertifiedAt = now;
        CertifiedBy = by;
        Status = MtpCaseStatus.Certified;
        return Result.Success();
    }

    /// <summary>Records the termination in the admission register. Once recorded the case cannot change.</summary>
    public Result Terminate(MtpTermination t, DateOnly today, DateTimeOffset now)
    {
        if (IsClosed)
        {
            return Closed();
        }

        if (string.IsNullOrWhiteSpace(t.PerformedBy))
        {
            return Error.Field("performedBy", "Enter the doctor who performed the termination.");
        }

        if (t.Method == MtpMethod.Other && string.IsNullOrWhiteSpace(t.Remarks))
        {
            return Error.Field("remarks", "Name the method in the remarks.");
        }

        if (t.TerminatedOn < LatestOf(ConsentOn, Opinions.Select(o => o.OpinionOn)))
        {
            return Error.Field("terminatedOn", "The termination cannot come before the consent and the doctors' opinion.");
        }

        if (t.TerminatedOn > today)
        {
            return Error.Field("terminatedOn", "The termination cannot be dated after today.");
        }

        if (t.DischargedOn is { } discharged && (discharged < t.TerminatedOn || discharged > today))
        {
            return Error.Field("dischargedOn", "The discharge comes on or after the termination, and not after today.");
        }

        TerminatedOn = t.TerminatedOn;
        Method = t.Method;
        PerformedBy = t.PerformedBy.Trim();
        DischargedOn = t.DischargedOn;
        Outcome = Clean(t.Outcome);
        Remarks = Clean(t.Remarks);
        TerminationRecordedAt = now;
        Status = MtpCaseStatus.Terminated;
        return Result.Success();
    }

    /// <summary>The termination will not be done here: the case is closed with the reason and stays in no register.</summary>
    public Result Cancel(string reason)
    {
        if (IsClosed)
        {
            return Closed();
        }

        Status = MtpCaseStatus.Cancelled;
        CancelReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>The number of doctors' opinions the length of a pregnancy calls for, beyond 24 weeks the Medical Board's.</summary>
    public static int OpinionsNeeded(int weeks) => weeks <= OneOpinionWeeks ? 1 : 2;

    private static Error? Check(MtpCertification c, DateOnly today)
    {
        if (c.AgeYears is < 10 or > 60)
        {
            return Error.Field("ageYears", "Enter the woman's age in years.");
        }

        if (c.WeeksOfGestation is < 1 or > MaxWeeksOfGestation)
        {
            return Error.Field("weeksOfGestation", $"Enter the length of the pregnancy in weeks, from 1 to {MaxWeeksOfGestation}.");
        }

        if (c.ConsentOn > today)
        {
            return Error.Field("consentOn", "The consent cannot be dated after today.");
        }

        // s.3(4): a minor's or a mentally ill woman's consent is her guardian's; anyone else's is her own.
        var needsGuardian = c.AgeYears < AgeOfMajority || c.MentallyIll;
        if (needsGuardian && c.ConsentBy != MtpConsentBy.Guardian)
        {
            return Error.Field("consentBy", "A woman under 18 or mentally ill needs her guardian's written consent.");
        }

        if (!needsGuardian && c.ConsentBy != MtpConsentBy.Woman)
        {
            return Error.Field("consentBy", "An adult woman who is not mentally ill gives her own consent; no one else's is asked.");
        }

        if (c.ConsentBy == MtpConsentBy.Guardian && (string.IsNullOrWhiteSpace(c.GuardianName) || string.IsNullOrWhiteSpace(c.GuardianRelation)))
        {
            return Error.Field("guardianName", "Enter the guardian's name and relation to the woman.");
        }

        if (c.Grounds.Count == 0)
        {
            return Error.Field("grounds", "Choose the grounds on which the opinion was formed.");
        }

        if (c.Grounds.Contains(MtpGround.ContraceptiveFailure) && c.WeeksOfGestation > OneOpinionWeeks)
        {
            return Error.Field("grounds", "The failure of a contraceptive is a ground up to 20 weeks only.");
        }

        if (c.WeeksOfGestation > OneOpinionWeeks && c.WeeksOfGestation <= TwoOpinionsWeeks && c.Category is null)
        {
            return Error.Field("category", "From 20 to 24 weeks the woman must be in a category of rule 3B. Choose it.");
        }

        if (c.WeeksOfGestation > TwoOpinionsWeeks)
        {
            if (!c.Grounds.SequenceEqual([MtpGround.FoetalAbnormality]))
            {
                return Error.Field("grounds", "Beyond 24 weeks a pregnancy is terminated only for a substantial foetal abnormality.");
            }

            if (string.IsNullOrWhiteSpace(c.MedicalBoardReference))
            {
                return Error.Field("medicalBoardReference", "Beyond 24 weeks enter the reference of the Medical Board's opinion.");
            }
        }

        var opinions = c.Opinions.Where(o => !string.IsNullOrWhiteSpace(o.DoctorName)).ToList();
        if (opinions.Count != c.Opinions.Count || opinions.Any(o => string.IsNullOrWhiteSpace(o.RegistrationNumber)))
        {
            return Error.Field("opinions", "Enter each doctor's name and registration number.");
        }

        var needed = OpinionsNeeded(c.WeeksOfGestation);
        if (opinions.Count < needed)
        {
            return Error.Field("opinions", needed == 1
                ? "Enter the doctor who formed the opinion."
                : "Beyond 20 weeks the opinion of two doctors is needed.");
        }

        if (opinions.Select(o => o.RegistrationNumber.Trim().ToUpperInvariant()).Distinct().Count() != opinions.Count)
        {
            return Error.Field("opinions", "The two opinions must be of two different doctors.");
        }

        if (opinions.Any(o => o.OpinionOn > today))
        {
            return Error.Field("opinions", "An opinion cannot be dated after today.");
        }

        return null;
    }

    private static DateOnly LatestOf(DateOnly first, IEnumerable<DateOnly> others) => others.Aggregate(first, (a, b) => a > b ? a : b);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Error Closed() => Status == MtpCaseStatus.Cancelled
        ? Error.Rule("mtp.cancelled", "This case was cancelled.")
        : Error.Rule("mtp.terminated", "The termination is recorded in the register; the case cannot change.");
}
