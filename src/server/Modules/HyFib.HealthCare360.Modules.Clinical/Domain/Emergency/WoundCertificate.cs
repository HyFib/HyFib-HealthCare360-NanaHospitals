namespace HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;

/// <summary>What an injury is, as the examining doctor finds it.</summary>
public enum InjuryKind
{
    Abrasion = 0,
    Contusion,
    Laceration,
    IncisedWound,
    StabWound,
    ChopWound,
    Fracture,
    Dislocation,
    Burn,
    FirearmWound,
    Bite,
    Other,
}

/// <summary>What caused the injury, in the doctor's opinion.</summary>
public enum InjuryWeapon
{
    Blunt = 0,
    Sharp,
    SharpPointed,
    HeavySharp,
    Firearm,
    Thermal,
    Chemical,
    Bite,
    Unknown,
}

/// <summary>The nature of the hurt under the Bharatiya Nyaya Sanhita 2023 (s.114 hurt, s.116 grievous hurt).</summary>
public enum InjuryNature
{
    /// <summary>Hurt that is not grievous (BNS s.114; punished under s.115).</summary>
    Simple = 0,

    /// <summary>One of the eight kinds of BNS s.116; the clause is named (<see cref="GrievousClause"/>).</summary>
    Grievous,

    /// <summary>
    /// Opinion kept back until an X-ray, a scan or a specialist's opinion is seen; given later in a supplementary
    /// certificate. A certificate with a reserved opinion says what it waits for.
    /// </summary>
    Reserved,
}

/// <summary>The kinds of grievous hurt of the Bharatiya Nyaya Sanhita 2023, s.116.</summary>
public enum GrievousClause
{
    /// <summary>(a) Emasculation.</summary>
    Emasculation = 0,

    /// <summary>(b) Permanent privation of the sight of either eye.</summary>
    LossOfSight,

    /// <summary>(c) Permanent privation of the hearing of either ear.</summary>
    LossOfHearing,

    /// <summary>(d) Privation of any member or joint.</summary>
    LossOfMemberOrJoint,

    /// <summary>(e) Destruction or permanent impairing of the powers of any member or joint.</summary>
    ImpairmentOfMemberOrJoint,

    /// <summary>(f) Permanent disfiguration of the head or face.</summary>
    DisfigurementOfHeadOrFace,

    /// <summary>(g) Fracture or dislocation of a bone or tooth.</summary>
    FractureOrDislocation,

    /// <summary>
    /// (h) Hurt which endangers life, or causes severe bodily pain, or leaves the sufferer unable to follow their ordinary
    /// pursuits, for fifteen days.
    /// </summary>
    EndangersLifeOrFifteenDays,
}

/// <summary>One injury on a wound certificate, as examined: what, where, how large, by what, how old, and its nature in law.</summary>
/// <param name="Number">The injury's number on the certificate, from 1, as the court refers to it.</param>
/// <param name="Site">Where on the body, with its distance from a fixed landmark ("left forearm, 5 cm above the wrist").</param>
/// <param name="Size">Length × breadth × depth, in centimetres ("4 × 1 × 0.5 cm").</param>
/// <param name="Age">How old the injury is, by its colour, scab or healing ("fresh, within 6 hours").</param>
/// <param name="ReservedFor">For a reserved opinion: what it waits for ("X-ray of the left forearm").</param>
public sealed record WoundInjury(
    int Number,
    InjuryKind Kind,
    string Site,
    string? Size,
    string Description,
    InjuryWeapon Weapon,
    string Age,
    InjuryNature Nature,
    GrievousClause? Clause,
    string? ReservedFor);

/// <summary>
/// The wound certificate of a medico-legal case: the injuries the examining doctor found, each with its site, size, the
/// weapon in their opinion, its age and its nature in law (simple or grievous under the Bharatiya Nyaya Sanhita 2023,
/// s.114–116), signed by that doctor. The police ask for it to frame the charge, and the court calls the doctor on it, so
/// once signed it does not change: a reserved opinion is given later, in a supplementary certificate, and the signed one
/// stays as it was.
/// </summary>
public sealed class WoundCertificate : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid VisitId { get; set; }
    public Guid MlcRecordId { get; set; }

    /// <summary>When the patient was examined.</summary>
    public DateTimeOffset ExaminedAt { get; private set; }

    /// <summary>Two marks of identification (moles, scars), as the certificate identifies the person examined.</summary>
    public string IdentificationMarks { get; private set; } = string.Empty;

    /// <summary>What the patient or the person who brought them said happened, in their words.</summary>
    public string? History { get; private set; }

    public List<WoundInjury> Injuries { get; private set; } = [];

    /// <summary>The doctor's remarks: investigations asked for, the patient's condition, referral.</summary>
    public string? Remarks { get; private set; }

    public DateTimeOffset? SignedAt { get; private set; }
    public Guid? SignedByUserId { get; private set; }

    /// <summary>The examining doctor in the practitioner master: their name, qualification and registration are printed.</summary>
    public Guid? SignedByPractitionerId { get; private set; }

    public string? DoctorName { get; private set; }
    public string? DoctorRegistration { get; private set; }

    public bool IsSigned => SignedAt is not null;

    /// <summary>Writes the certificate's content while it is a draft. A signed certificate does not change.</summary>
    public Result Write(DateTimeOffset examinedAt, string identificationMarks, string? history, IReadOnlyList<WoundInjury> injuries, string? remarks, DateTimeOffset now)
    {
        if (IsSigned)
        {
            return Error.Rule("wound_certificate.signed", "This wound certificate is signed and cannot change. Give a reserved opinion in a supplementary certificate.");
        }

        var checkedInjuries = Check(injuries);
        if (checkedInjuries.IsFailure)
        {
            return checkedInjuries;
        }

        if (examinedAt > now.AddMinutes(5))
        {
            return Error.Field("examinedAt", "The examination cannot be in the future.");
        }

        if (string.IsNullOrWhiteSpace(identificationMarks))
        {
            return Error.Field("identificationMarks", "Record the marks of identification (two, where the patient has them).");
        }

        ExaminedAt = examinedAt;
        IdentificationMarks = identificationMarks.Trim();
        History = Clean(history);
        Injuries = [.. injuries.OrderBy(i => i.Number).Select(i => i with
        {
            Site = i.Site.Trim(),
            Size = Clean(i.Size),
            Description = i.Description.Trim(),
            Age = i.Age.Trim(),
            Clause = i.Nature == InjuryNature.Grievous ? i.Clause : null,
            ReservedFor = i.Nature == InjuryNature.Reserved ? Clean(i.ReservedFor) : null,
        })];
        Remarks = Clean(remarks);
        return Result.Success();
    }

    /// <summary>
    /// The examining doctor signs: only a doctor in the practitioner master, from their own sign-in, whose name and
    /// registration go on the certificate.
    /// </summary>
    public Result Sign(Guid userId, Guid practitionerId, string doctorName, string? registration, DateTimeOffset now)
    {
        if (IsSigned)
        {
            return Error.Rule("wound_certificate.already_signed", "This wound certificate is already signed.");
        }

        if (Injuries.Count == 0)
        {
            return Error.Rule("wound_certificate.no_injuries", "Record the injuries before signing.");
        }

        SignedAt = now;
        SignedByUserId = userId;
        SignedByPractitionerId = practitionerId;
        DoctorName = doctorName;
        DoctorRegistration = registration;
        return Result.Success();
    }

    /// <summary>
    /// The rules of an injury list: numbered 1, 2, 3… without gaps; a grievous hurt names its clause of BNS s.116; a reserved
    /// opinion says what it waits for; and a fracture or dislocation, being grievous by s.116(g), is never simple.
    /// </summary>
    public static Result Check(IReadOnlyList<WoundInjury> injuries)
    {
        if (injuries.Count == 0)
        {
            return Error.Field("injuries", "Record at least one injury.");
        }

        var numbers = injuries.Select(i => i.Number).Order().ToList();
        if (!numbers.SequenceEqual(Enumerable.Range(1, injuries.Count)))
        {
            return Error.Field("injuries", "Number the injuries 1, 2, 3… without gaps or repeats.");
        }

        foreach (var i in injuries.OrderBy(i => i.Number))
        {
            if (string.IsNullOrWhiteSpace(i.Site) || string.IsNullOrWhiteSpace(i.Description) || string.IsNullOrWhiteSpace(i.Age))
            {
                return Error.Field("injuries", $"Injury {i.Number}: give its site, a description and its age.");
            }

            if (i.Nature == InjuryNature.Grievous && i.Clause is null)
            {
                return Error.Field("injuries", $"Injury {i.Number}: say which kind of grievous hurt it is (BNS 2023, s.116).");
            }

            if (i.Nature == InjuryNature.Reserved && string.IsNullOrWhiteSpace(i.ReservedFor))
            {
                return Error.Field("injuries", $"Injury {i.Number}: say what the reserved opinion waits for (an X-ray, a CT scan, a specialist's opinion).");
            }

            if (i.Kind is InjuryKind.Fracture or InjuryKind.Dislocation && i.Nature == InjuryNature.Simple)
            {
                return Error.Field("injuries", $"Injury {i.Number}: a fracture or dislocation is grievous hurt (BNS 2023, s.116(g)), never simple.");
            }
        }

        return Result.Success();
    }

    /// <summary>"Simple", "Grievous (s.116(g): fracture or dislocation)", "Reserved: X-ray of the left forearm".</summary>
    public static string Describe(WoundInjury injury) => injury.Nature switch
    {
        InjuryNature.Grievous when injury.Clause is { } clause => $"Grievous (BNS s.116{ClauseLetter(clause)}: {ClauseWords(clause)})",
        InjuryNature.Grievous => "Grievous",
        InjuryNature.Reserved => $"Opinion reserved pending {injury.ReservedFor}",
        _ => "Simple",
    };

    public static string ClauseLetter(GrievousClause clause) => $"({(char)('a' + (int)clause)})";

    public static string ClauseWords(GrievousClause clause) => clause switch
    {
        GrievousClause.Emasculation => "emasculation",
        GrievousClause.LossOfSight => "permanent privation of the sight of either eye",
        GrievousClause.LossOfHearing => "permanent privation of the hearing of either ear",
        GrievousClause.LossOfMemberOrJoint => "privation of a member or joint",
        GrievousClause.ImpairmentOfMemberOrJoint => "destruction or permanent impairing of the powers of a member or joint",
        GrievousClause.DisfigurementOfHeadOrFace => "permanent disfiguration of the head or face",
        GrievousClause.FractureOrDislocation => "fracture or dislocation of a bone or tooth",
        _ => "endangers life, or severe bodily pain or unable to follow ordinary pursuits for fifteen days",
    };

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
