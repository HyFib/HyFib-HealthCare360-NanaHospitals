namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

/// <summary>
/// The indications for an ultrasound in pregnancy, as Form F Section B lists them (PCPNDT Rules 1996, as amended in
/// 2014). One or more is recorded for every obstetric ultrasound.
/// </summary>
public enum FormFIndication
{
    IntraUterineOrEctopicPregnancy = 1,
    GestationalAge,
    NumberOfFoetusesAndChorionicity,
    PregnancyWithIucdOrContraceptiveFailure,
    VaginalBleedingOrLeaking,
    FollowUpOfAbortion,
    CervicalCanalAndInternalOs,
    UterineSizeDiscrepancy,
    AdnexalOrUterinePathology,
    ChromosomalOrStructuralAbnormalities,
    FoetalPresentationAndPosition,
    LiquorAmnii,
    PretermLabourOrRupturedMembranes,
    PlacentalEvaluation,
    UmbilicalCordEvaluation,
    PreviousCaesareanScars,
    FoetalGrowthAndWellBeing,
    ColourFlowAndDoppler,
    GuidedProcedures,
    AdjunctToInvasiveInterventions,
    IntraPartumEvents,
    ConditionsComplicatingPregnancy,
    Research,
}

public enum FormFReferral
{
    /// <summary>Referred by another doctor, whose name and address are recorded.</summary>
    Doctor = 1,

    /// <summary>Self-referral by the gynaecologist, radiologist or practitioner who performs the ultrasound.</summary>
    Self,
}

public enum FormFStatus
{
    /// <summary>The woman's details and her declaration are recorded: the ultrasound may be performed.</summary>
    Declared = 1,

    /// <summary>The result and the doctor's declaration are recorded. The form can no longer change.</summary>
    Completed,
}

/// <summary>What the clinic records about the woman before the ultrasound (Form F Sections A and D, her declaration).</summary>
public sealed record FormFDeclaration(
    int AgeYears,
    string HusbandOrFatherName,
    string Address,
    string? Phone,
    int LivingSons,
    int LivingDaughters,
    DateOnly? LastMenstrualPeriod,
    int? WeeksOfPregnancy,
    FormFReferral Referral,
    string? ReferringDoctorName,
    string? ReferringDoctorAddress,
    IReadOnlyCollection<FormFIndication> Indications,
    string? OtherIndication,
    Guid? SignedFormFileId);

/// <summary>What the doctor records after the ultrasound (Form F Section B and the doctor's declaration in Section D).</summary>
public sealed record FormFResult(string Result, string ConveyedTo, DateOnly ConveyedOn, bool MtpIndicated, string? MtpIndication);

/// <summary>
/// Form F of the PCPNDT Act (s.29; Rules 9 and 10): the record every ultrasound of a pregnant woman needs. The woman's
/// details, the referral, the indications and her declaration that she does not want to know the sex of the foetus are
/// recorded before the ultrasound; the result, whom it was given to, and the doctor's declaration that the sex was neither
/// detected nor disclosed, after it. Once completed it cannot change, and it is kept for at least two years (Rule 9(4)).
/// The month's forms go to the Appropriate Authority by the 5th of the next month (Rule 9(8)).
/// </summary>
public sealed class FormF : AuditableEntity, IFacilityScoped
{
    /// <summary>The longest gestation a form may give: beyond it, the period was mis-typed.</summary>
    public const int MaxWeeksOfPregnancy = 45;

    public Guid FacilityId { get; set; }
    public Guid ImagingStudyId { get; set; }
    public Guid PatientId { get; set; }
    public string FormNumber { get; set; } = string.Empty;

    /// <summary>The clinic's PCPNDT registration number when the form was made, as the form prints it.</summary>
    public string RegistrationNumber { get; set; } = string.Empty;

    public FormFStatus Status { get; private set; }

    public int AgeYears { get; private set; }
    public string HusbandOrFatherName { get; private set; } = string.Empty;
    public string Address { get; private set; } = string.Empty;
    public string? Phone { get; private set; }
    public int LivingSons { get; private set; }
    public int LivingDaughters { get; private set; }
    public DateOnly? LastMenstrualPeriod { get; private set; }
    public int? WeeksOfPregnancy { get; private set; }
    public FormFReferral Referral { get; private set; }
    public string? ReferringDoctorName { get; private set; }
    public string? ReferringDoctorAddress { get; private set; }
    public List<FormFIndication> Indications { get; private set; } = [];
    public string? OtherIndication { get; private set; }

    /// <summary>The scanned form the woman signed (her declaration, Section D).</summary>
    public Guid? SignedFormFileId { get; private set; }

    public DateTimeOffset DeclaredAt { get; private set; }
    public Guid? DeclarationRecordedBy { get; private set; }

    public DateOnly? ProcedureDate { get; private set; }
    public string? ResultText { get; private set; }
    public string? ResultConveyedTo { get; private set; }
    public DateOnly? ResultConveyedOn { get; private set; }
    public bool MtpIndicated { get; private set; }
    public string? MtpIndication { get; private set; }

    /// <summary>The doctor who performed the ultrasound and declared that the sex of the foetus was neither detected nor disclosed.</summary>
    public Guid? DoctorUserId { get; private set; }

    public string? DoctorName { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsCompleted => Status == FormFStatus.Completed;

    /// <summary>Records the woman's details and declaration, or corrects them before the form is completed.</summary>
    public Result Declare(FormFDeclaration declaration, Guid? by, DateTimeOffset now)
    {
        if (IsCompleted)
        {
            return Completed();
        }

        var invalid = Check(declaration);
        if (invalid is not null)
        {
            return invalid;
        }

        AgeYears = declaration.AgeYears;
        HusbandOrFatherName = declaration.HusbandOrFatherName.Trim();
        Address = declaration.Address.Trim();
        Phone = Clean(declaration.Phone);
        LivingSons = declaration.LivingSons;
        LivingDaughters = declaration.LivingDaughters;
        LastMenstrualPeriod = declaration.LastMenstrualPeriod;
        WeeksOfPregnancy = declaration.WeeksOfPregnancy;
        Referral = declaration.Referral;
        ReferringDoctorName = declaration.Referral == FormFReferral.Doctor ? Clean(declaration.ReferringDoctorName) : null;
        ReferringDoctorAddress = declaration.Referral == FormFReferral.Doctor ? Clean(declaration.ReferringDoctorAddress) : null;
        Indications = [.. declaration.Indications.Distinct().Order()];
        OtherIndication = Clean(declaration.OtherIndication);
        SignedFormFileId = declaration.SignedFormFileId ?? SignedFormFileId;
        if (Status == 0)
        {
            Status = FormFStatus.Declared;
            DeclaredAt = now;
            DeclarationRecordedBy = by;
        }

        return Result.Success();
    }

    /// <summary>
    /// Records the result and the doctor's declaration. Only the doctor who performed the ultrasound completes the form, and
    /// only once the images are acquired.
    /// </summary>
    public Result Complete(FormFResult result, Guid doctorUserId, string doctorName, DateOnly procedureDate, DateTimeOffset now)
    {
        if (IsCompleted)
        {
            return Completed();
        }

        if (string.IsNullOrWhiteSpace(result.Result))
        {
            return Error.Field("result", "Enter the result of the ultrasound.");
        }

        if (string.IsNullOrWhiteSpace(result.ConveyedTo))
        {
            return Error.Field("conveyedTo", "Enter whom the result was given to.");
        }

        if (result.ConveyedOn < procedureDate)
        {
            return Error.Field("conveyedOn", "The result cannot have been given before the ultrasound was done.");
        }

        if (result.MtpIndicated && string.IsNullOrWhiteSpace(result.MtpIndication))
        {
            return Error.Field("mtpIndication", "Describe the abnormality that indicates a termination of pregnancy.");
        }

        ResultText = result.Result.Trim();
        ResultConveyedTo = result.ConveyedTo.Trim();
        ResultConveyedOn = result.ConveyedOn;
        MtpIndicated = result.MtpIndicated;
        MtpIndication = result.MtpIndicated ? Clean(result.MtpIndication) : null;
        ProcedureDate = procedureDate;
        DoctorUserId = doctorUserId;
        DoctorName = doctorName.Trim();
        CompletedAt = now;
        Status = FormFStatus.Completed;
        return Result.Success();
    }

    private static Error? Check(FormFDeclaration d)
    {
        if (d.AgeYears is < 10 or > 60)
        {
            return Error.Field("ageYears", "Enter the woman's age in years.");
        }

        if (string.IsNullOrWhiteSpace(d.HusbandOrFatherName))
        {
            return Error.Field("husbandOrFatherName", "Enter the name of her husband or father.");
        }

        if (string.IsNullOrWhiteSpace(d.Address))
        {
            return Error.Field("address", "Enter her address.");
        }

        if (d.LivingSons < 0 || d.LivingDaughters < 0)
        {
            return Error.Field("livingSons", "Enter the number of living children.");
        }

        if (d.LastMenstrualPeriod is null && d.WeeksOfPregnancy is null)
        {
            return Error.Field("lastMenstrualPeriod", "Enter the last menstrual period or the weeks of pregnancy.");
        }

        if (d.WeeksOfPregnancy is < 1 or > MaxWeeksOfPregnancy)
        {
            return Error.Field("weeksOfPregnancy", $"Enter the weeks of pregnancy, 1 to {MaxWeeksOfPregnancy}.");
        }

        if (d.Referral == FormFReferral.Doctor && (string.IsNullOrWhiteSpace(d.ReferringDoctorName) || string.IsNullOrWhiteSpace(d.ReferringDoctorAddress)))
        {
            return Error.Field("referringDoctorName", "Enter the name and address of the referring doctor.");
        }

        if (d.Indications.Count == 0)
        {
            return Error.Field("indications", "Choose at least one indication for the ultrasound.");
        }

        return null;
    }

    private static Error Completed() =>
        Error.Rule("form_f.completed", "This Form F is completed and signed, so it cannot be changed.");

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
