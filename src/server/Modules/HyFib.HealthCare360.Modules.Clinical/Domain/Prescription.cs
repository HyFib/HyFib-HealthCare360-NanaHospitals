namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum PrescriptionStatus
{
    Draft = 0,
    Signed,
    Cancelled,
}

/// <summary>Dosing frequency as written on Indian prescriptions. <see cref="Custom"/> accepts patterns such as "1-0-1".</summary>
public enum DoseFrequency
{
    /// <summary>Once daily.</summary>
    OD = 0,

    /// <summary>Twice daily.</summary>
    BD,

    /// <summary>Three times daily.</summary>
    TDS,

    /// <summary>Four times daily.</summary>
    QID,

    /// <summary>At bedtime.</summary>
    HS,

    /// <summary>When required.</summary>
    SOS,

    /// <summary>Immediately, once.</summary>
    STAT,
    Custom,
    Weekly,
}

/// <summary>What the prescriber confirmed when signing (safety override, controlled-drug confirmation).</summary>
public sealed record PrescriptionSignature(
    Guid? UserId,
    string VerificationToken,
    string? OverrideReason,
    string? SafetyAlertsJson,
    bool ControlledDrugsConfirmed);

/// <summary>
/// The doctor's Digital Signature Certificate signature over the prescription's canonical bytes (ERX-04), as checked
/// by the server: the detached CMS/PKCS#7 signature, the exact bytes signed (<see cref="PrescriptionCanonicalForm"/>)
/// and their SHA-256, and the signer's certificate.
/// </summary>
public sealed record PrescriptionDigitalSignature(
    byte[] Signature,
    byte[] Content,
    string ContentHash,
    DateTimeOffset SignedAt,
    Guid? UserId,
    string SignerName,
    string SignerSubject,
    string SignerSerialNumber,
    string SignerIssuer,
    string SignerThumbprint,
    string ChainStatus);

/// <summary>
/// An e-prescription (ERX-01..06). Drafts are freely editable; signing runs the safety checks (in the service),
/// allocates a verification token for the printed QR code and raises <see cref="PrescriptionSigned"/>, which puts
/// it in the OP or IP pharmacy queue — no paper is needed for dispensing.
/// </summary>
public sealed class Prescription : AuditableEntity, IFacilityScoped
{
    /// <summary>What the columns that describe the DSC's certificate hold at most (<see cref="AttachDigitalSignature"/>).</summary>
    public const int SignerNameMaxLength = 200;

    public const int SignerDistinguishedNameMaxLength = 1000;
    public const int SignerSerialNumberMaxLength = 128;
    public const int SignerThumbprintMaxLength = 128;
    public const int SignerChainStatusMaxLength = 1000;

    public Guid FacilityId { get; set; }
    public string PrescriptionNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid? EncounterId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid PractitionerId { get; set; }
    public bool IsInpatient { get; set; }
    public PrescriptionStatus Status { get; private set; }
    public DateTimeOffset PrescribedAt { get; set; }
    public DateTimeOffset? SignedAt { get; private set; }
    public Guid? SignedByUserId { get; private set; }

    /// <summary>Notes for the pharmacist/nurse.</summary>
    public string? Notes { get; set; }

    /// <summary>General advice printed for the patient (diet, rest, warning signs).</summary>
    public string? Advice { get; set; }

    public int RefillsAllowed { get; set; }
    public int RefillsRemaining { get; private set; }

    /// <summary>Set on prescriptions issued as a refill of an earlier signed prescription.</summary>
    public Guid? RefillOfPrescriptionId { get; set; }

    /// <summary>Unguessable token printed as a QR code; the public verification page shows only non-PHI facts.</summary>
    public string? VerificationToken { get; private set; }

    public string? SafetyOverrideReason { get; private set; }

    /// <summary>JSON snapshot of the safety alerts shown at signing (medico-legal record of what the prescriber saw).</summary>
    public string? SafetyAlerts { get; private set; }

    public bool ControlledDrugsConfirmed { get; private set; }

    /// <summary>
    /// The detached CMS/PKCS#7 signature made with the doctor's DSC in HC360 Desktop (ERX-04); null until then. Once
    /// set it never changes: the prescription is signed, so its content cannot change either, and a different
    /// prescription (a repeat) is a new prescription that needs its own signature.
    /// </summary>
    public byte[]? DigitalSignature { get; private set; }

    /// <summary>The exact bytes the DSC signed (<see cref="PrescriptionCanonicalForm"/>).</summary>
    public byte[]? SignedContent { get; private set; }

    /// <summary>Lower-case hex SHA-256 of <see cref="SignedContent"/>, printed and shown on the verification page.</summary>
    public string? SignedContentHash { get; private set; }

    /// <summary>The signing time in the signature (checked against the server's clock when it was attached).</summary>
    public DateTimeOffset? DigitallySignedAt { get; private set; }

    public Guid? DigitallySignedByUserId { get; private set; }

    /// <summary>The certificate's common name: the doctor's name on an Indian DSC.</summary>
    public string? SignerName { get; private set; }

    public string? SignerSubject { get; private set; }
    public string? SignerSerialNumber { get; private set; }
    public string? SignerIssuer { get; private set; }

    /// <summary>SHA-1 thumbprint, as the Windows certificate store shows it.</summary>
    public string? SignerThumbprint { get; private set; }

    /// <summary>
    /// The certificate's chain when the signature was attached: always "Trusted", because a signature is accepted only from
    /// a certificate that chains to a CCA India root in the server's DSC trust folder (custom root trust). Every later
    /// check builds the chain again against the folder as it is then.
    /// </summary>
    public string? SignerChainStatus { get; private set; }

    public bool IsDigitallySigned => DigitalSignature is not null;

    /// <summary>
    /// When the patient was told the prescription is ready (the "prescription shared" message): at signing, or, where the
    /// hospital requires the doctor's DSC, once signed with it or once the hospital no longer requires it
    /// (<see cref="PatientNoticeHeldAt"/>). Null until then, so the message goes once whatever the setting was at each step.
    /// </summary>
    public DateTimeOffset? PatientNotifiedAt { get; private set; }

    /// <summary>
    /// When the "prescription shared" message was held back because the hospital required the doctor's DSC when the
    /// prescription was signed; null when it was not held, or no longer is. A held message goes when the doctor signs it
    /// with their DSC, or, if the hospital stops requiring the DSC first, when <c>PrescriptionNoticeRelease</c> finds the
    /// prescription can be issued without it. Either way it is let go once (<see cref="ReleasePatientNotice"/>).
    /// </summary>
    public DateTimeOffset? PatientNoticeHeldAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancelReason { get; private set; }
    public List<PrescriptionItem> Items { get; set; } = [];

    public bool HasControlledDrugs => Items.Any(i => i.IsControlledDrug);

    public Result ReplaceItems(IEnumerable<PrescriptionItem> items)
    {
        if (Status != PrescriptionStatus.Draft)
        {
            return Error.Rule("prescription.not_draft", "Only draft prescriptions can be edited. Cancel and re-prescribe instead.");
        }

        Items.Clear();
        var sequence = 0;
        foreach (var item in items)
        {
            item.PrescriptionId = Id;
            item.Sequence = ++sequence;
            Items.Add(item);
        }

        return Result.Success();
    }

    public Result Sign(PrescriptionSignature signature, DateTimeOffset now)
    {
        if (Status != PrescriptionStatus.Draft)
        {
            return Error.Rule("prescription.not_draft", Status == PrescriptionStatus.Signed ? "This prescription is already signed." : "This prescription is cancelled.");
        }

        if (Items.Count == 0)
        {
            return Error.Rule("prescription.empty", "Add at least one medicine before signing.");
        }

        if (Items.FirstOrDefault(i => i.Quantity <= 0) is { } missing)
        {
            return Error.Rule("prescription.quantity_missing", $"Enter the quantity to dispense for {missing.DrugName}.");
        }

        if (HasControlledDrugs && !signature.ControlledDrugsConfirmed)
        {
            return Error.Rule("prescription.controlled_confirmation_required",
                "This prescription contains Schedule H1/X (narcotic or psychotropic) medicines. Confirm them explicitly to sign.");
        }

        Status = PrescriptionStatus.Signed;
        SignedAt = now;
        SignedByUserId = signature.UserId;
        VerificationToken = signature.VerificationToken;
        SafetyOverrideReason = string.IsNullOrWhiteSpace(signature.OverrideReason) ? null : signature.OverrideReason.Trim();
        SafetyAlerts = signature.SafetyAlertsJson;
        ControlledDrugsConfirmed = HasControlledDrugs;
        RefillsRemaining = RefillsAllowed;

        Raise(new PrescriptionSigned(Id, PrescriptionNumber, PatientId, VisitId, AdmissionId, FacilityId, PractitionerId, IsInpatient,
            Items.OrderBy(i => i.Sequence).Select(ToLine).ToList(), now));
        return Result.Success();
    }

    /// <summary>
    /// Keeps the doctor's DSC signature, which the caller has verified against <see cref="PrescriptionCanonicalForm"/>.
    /// Only a signed prescription takes one, and only once: a signed prescription never changes, so a changed
    /// prescription is a new one (repeat) with its own signature.
    /// </summary>
    public Result AttachDigitalSignature(PrescriptionDigitalSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (Status != PrescriptionStatus.Signed)
        {
            return Error.Rule("prescription.not_signed", Status == PrescriptionStatus.Draft
                ? "Sign the prescription before signing it with your DSC."
                : "This prescription is cancelled.");
        }

        if (IsDigitallySigned)
        {
            return Error.Conflict("prescription.already_digitally_signed",
                "This prescription is already signed with a DSC and cannot change. Repeat it to issue a new prescription.");
        }

        DigitalSignature = signature.Signature;
        SignedContent = signature.Content;
        SignedContentHash = signature.ContentHash;
        DigitallySignedAt = signature.SignedAt;
        DigitallySignedByUserId = signature.UserId;
        SignerName = signature.SignerName;
        SignerSubject = signature.SignerSubject;
        SignerSerialNumber = signature.SignerSerialNumber;
        SignerIssuer = signature.SignerIssuer;
        SignerThumbprint = signature.SignerThumbprint;
        SignerChainStatus = signature.ChainStatus;
        return Result.Success();
    }

    /// <summary>
    /// Holds back the "prescription shared" message until the prescription can be issued (the hospital requires the
    /// doctor's DSC). Only an out-patient prescription just signed, whose patient has not been told, is held.
    /// </summary>
    public void HoldPatientNotice(DateTimeOffset now)
    {
        if (Status == PrescriptionStatus.Signed && !IsInpatient && PatientNotifiedAt is null)
        {
            PatientNoticeHeldAt ??= now;
        }
    }

    /// <summary>The held message was sent, or could not be (the patient has no mobile number): it is not tried again.</summary>
    public void ReleasePatientNotice() => PatientNoticeHeldAt = null;

    /// <summary>
    /// Records that the patient was told the prescription is ready, and raises <see cref="PrescriptionSharedWithPatient"/>
    /// (Virtual Care adds an SMS after a video consultation). False when they were told already.
    /// </summary>
    public bool MarkPatientNotified(DateTimeOffset now)
    {
        if (PatientNotifiedAt is not null || Status != PrescriptionStatus.Signed)
        {
            return false;
        }

        PatientNotifiedAt = now;
        PatientNoticeHeldAt = null;
        Raise(new PrescriptionSharedWithPatient(Id, PatientId, VisitId, FacilityId, PractitionerId, IsInpatient, SignedAt!.Value));
        return true;
    }

    /// <summary>Cancelling a signed prescription withdraws it from the pharmacy queue.</summary>
    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status == PrescriptionStatus.Cancelled)
        {
            return Error.Rule("prescription.cancelled", "This prescription is already cancelled.");
        }

        var wasSigned = Status == PrescriptionStatus.Signed;
        var trimmed = reason.Trim();
        Status = PrescriptionStatus.Cancelled;
        CancelledAt = now;
        CancelReason = trimmed;
        RefillsRemaining = 0;
        PatientNoticeHeldAt = null; // a cancelled prescription is never announced
        if (wasSigned)
        {
            Raise(new PrescriptionCancelled(Id, PatientId, trimmed));
        }

        return Result.Success();
    }

    /// <summary>Consumes one authorised refill (ERX-05). The refill itself is issued as a new signed prescription.</summary>
    public Result UseRefill()
    {
        if (Status != PrescriptionStatus.Signed)
        {
            return Error.Rule("prescription.not_signed", "Only signed prescriptions can be refilled.");
        }

        if (RefillsRemaining <= 0)
        {
            return Error.Rule("prescription.no_refills", "No refills remain on this prescription. A new prescription is needed.");
        }

        RefillsRemaining--;
        return Result.Success();
    }

    /// <summary>Last day of the longest course, counting the signing day as day 1.</summary>
    public DateOnly? CourseEndsOn(DateOnly startDate) =>
        Items.Count == 0 ? null : startDate.AddDays(Math.Max(Items.Max(i => i.DurationDays), 1) - 1);

    public static PrescriptionLine ToLine(PrescriptionItem i) => new(
        i.Id, i.DrugItemId, i.DrugName, i.GenericName, i.Strength, i.DosageForm, i.Route, i.Dose, i.FrequencyText, i.DurationDays,
        i.Quantity, i.Instructions, i.IsControlledDrug);
}

/// <summary>One medicine line with a snapshot of the drug master (name, generic, strength, form, route).</summary>
public sealed class PrescriptionItem : AuditableEntity
{
    public Guid PrescriptionId { get; set; }
    public int Sequence { get; set; }

    /// <summary>Inventory item master id (SupplyChain) chosen by the prescriber; null for free-text medicines.</summary>
    public Guid? DrugItemId { get; set; }

    public string DrugName { get; set; } = string.Empty;
    public string? GenericName { get; set; }
    public string? Strength { get; set; }
    public string? DosageForm { get; set; }
    public string? Route { get; set; }

    /// <summary>Dose per administration as written ("1 tab", "500 mg", "5 ml").</summary>
    public string Dose { get; set; } = string.Empty;

    public DoseFrequency Frequency { get; set; }
    public string? CustomFrequency { get; set; }

    /// <summary>Administrations per day used for quantity and dose checks (fractional for weekly schedules).</summary>
    public decimal? DosesPerDay { get; set; }

    public int DurationDays { get; set; }
    public decimal Quantity { get; set; }
    public string? Instructions { get; set; }

    /// <summary>Schedule H1/X narcotic or psychotropic medicine (ERX-06).</summary>
    public bool IsControlledDrug { get; set; }

    public string? ControlledSchedule { get; set; }

    public string FrequencyText => Frequency == DoseFrequency.Custom ? CustomFrequency ?? "Custom" : Frequency.ToString();
}

/// <summary>
/// The directions printed on prescriptions (ERX-04): in English, with the same in Hindi or Tamil under them for the
/// patient.
/// </summary>
public static class FrequencyLabels
{
    public static (string English, string Hindi) For(DoseFrequency frequency, string? custom) => frequency switch
    {
        DoseFrequency.OD => ("Once a day", "दिन में एक बार"),
        DoseFrequency.BD => ("Twice a day", "दिन में दो बार"),
        DoseFrequency.TDS => ("Three times a day", "दिन में तीन बार"),
        DoseFrequency.QID => ("Four times a day", "दिन में चार बार"),
        DoseFrequency.HS => ("At bedtime", "सोते समय"),
        DoseFrequency.SOS => ("Only when required", "आवश्यकता होने पर"),
        DoseFrequency.STAT => ("Immediately, once", "तुरंत, एक बार"),
        DoseFrequency.Weekly => ("Once a week", "सप्ताह में एक बार"),
        _ => (custom ?? "As directed", custom ?? "निर्देशानुसार"),
    };

    /// <summary>The directions in Tamil; a custom frequency prints as the doctor typed it.</summary>
    public static string Tamil(DoseFrequency frequency, string? custom) => frequency switch
    {
        DoseFrequency.OD => "நாளொன்றுக்கு ஒரு முறை",
        DoseFrequency.BD => "நாளொன்றுக்கு இரண்டு முறை",
        DoseFrequency.TDS => "நாளொன்றுக்கு மூன்று முறை",
        DoseFrequency.QID => "நாளொன்றுக்கு நான்கு முறை",
        DoseFrequency.HS => "இரவு தூங்கும் முன்",
        DoseFrequency.SOS => "தேவைப்படும்போது மட்டும்",
        DoseFrequency.STAT => "உடனடியாக, ஒரு முறை",
        DoseFrequency.Weekly => "வாரத்திற்கு ஒரு முறை",
        _ => custom ?? "அறிவுறுத்தியபடி",
    };
}
