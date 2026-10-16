using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;

/// <summary>
/// One medicine line. <see cref="DrugItemId"/> is the inventory item chosen through the drug-master search; the
/// name/generic/strength/form/route are stored as a snapshot. Quantity is calculated when possible (solid forms).
/// </summary>
public sealed record PrescriptionItemRequest(
    Guid? DrugItemId,
    string DrugName,
    string? GenericName,
    string? Strength,
    string? DosageForm,
    string? Route,
    string Dose,
    DoseFrequency Frequency,
    string? CustomFrequency,
    decimal? DosesPerDay,
    int DurationDays,
    decimal? Quantity,
    string? Instructions,
    bool IsControlledDrug,
    string? ControlledSchedule);

public sealed record CreatePrescriptionRequest(
    Guid PatientId,
    Guid? EncounterId,
    Guid? VisitId,
    Guid? AdmissionId,
    bool IsInpatient,
    Guid? PractitionerId,
    string? Notes,
    string? Advice,
    int RefillsAllowed,
    IReadOnlyList<PrescriptionItemRequest> Items);

public sealed record UpdatePrescriptionRequest(string? Notes, string? Advice, int RefillsAllowed, IReadOnlyList<PrescriptionItemRequest> Items, uint Version);

/// <summary>
/// Signing: blocking alerts (major interaction, allergy, maximum dose, high-alert pattern) need an
/// <see cref="OverrideReason"/> (audited); other warnings need <see cref="AcknowledgeWarnings"/>; Schedule H1/X
/// medicines need <see cref="ConfirmControlledDrugs"/>.
/// </summary>
public sealed record SignPrescriptionRequest(string? OverrideReason, bool AcknowledgeWarnings, bool ConfirmControlledDrugs, uint Version);

public sealed record RefillPrescriptionRequest(string? OverrideReason, bool AcknowledgeWarnings, bool ConfirmControlledDrugs);

public sealed record CancelPrescriptionRequest(string Reason);

public sealed record PrescriptionItemDto(
    Guid Id,
    int Sequence,
    Guid? DrugItemId,
    string DrugName,
    string? GenericName,
    string? Strength,
    string? DosageForm,
    string? Route,
    string Dose,
    DoseFrequency Frequency,
    string? CustomFrequency,
    string FrequencyText,
    decimal? DosesPerDay,
    int DurationDays,
    decimal Quantity,
    string? Instructions,
    bool IsControlledDrug,
    string? ControlledSchedule);

public sealed record PrescriptionDto(
    Guid Id,
    string PrescriptionNumber,
    Guid PatientId,
    Guid? EncounterId,
    Guid? VisitId,
    Guid? AdmissionId,
    Guid PractitionerId,
    string? PractitionerName,
    bool IsInpatient,
    PrescriptionStatus Status,
    DateTimeOffset PrescribedAt,
    DateTimeOffset? SignedAt,
    string? Notes,
    string? Advice,
    int RefillsAllowed,
    int RefillsRemaining,
    Guid? RefillOfPrescriptionId,
    string? SafetyOverrideReason,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    IReadOnlyList<PrescriptionItemDto> Items,
    uint Version,
    bool IsDigitallySigned = false,
    DateTimeOffset? DigitallySignedAt = null,
    string? DigitalSignerName = null);

public sealed record PrescriptionListItem(
    Guid Id,
    string PrescriptionNumber,
    Guid PatientId,
    PrescriptionStatus Status,
    bool IsInpatient,
    DateTimeOffset PrescribedAt,
    DateTimeOffset? SignedAt,
    Guid PractitionerId,
    string? PractitionerName,
    IReadOnlyList<string> Drugs);

public sealed record SafetyCheckResponse(bool IsBlocked, bool RequiresAcknowledgement, bool HasControlledDrugs, IReadOnlyList<SafetyAlert> Alerts);

public sealed record SignPrescriptionResult(PrescriptionDto Prescription, IReadOnlyList<SafetyAlert> Alerts);

public sealed record PrescriptionPrintPatient(Guid Id, string Uhid, string Name, string? Age, Gender Gender, string? PreferredLanguage);

public sealed record PrescriptionPrintLine(
    int Sequence,
    string DrugName,
    string? GenericName,
    string? Strength,
    string? DosageForm,
    string? Route,
    string Dose,
    string Frequency,
    string FrequencyEnglish,
    string FrequencyHindi,
    int DurationDays,
    decimal Quantity,
    string? Instructions,
    bool IsControlledDrug);

/// <summary>
/// Everything needed to print or share the prescription (ERX-04): letterhead, prescriber registration, bilingual
/// directions, known allergies and the QR verification token. The UI renders it with the PrintDocument layout.
/// Print <see cref="VerificationUrl"/> as the QR code when present: the absolute portal verification page
/// (<c>{portal.baseUrl}/portal/verify/prescription/{token}</c>), which resolves outside the hospital LAN.
/// <see cref="VerificationPath"/> is the server-relative verification API, kept for compatibility; the URL is null
/// until <c>portal.baseUrl</c> is configured.
/// </summary>
public sealed record PrescriptionPrintModel(
    Guid Id,
    string PrescriptionNumber,
    PrescriptionStatus Status,
    DateTimeOffset? SignedAt,
    FacilityLetterhead? Letterhead,
    PrescriberInfo? Prescriber,
    PrescriptionPrintPatient Patient,
    IReadOnlyList<string> Diagnoses,
    IReadOnlyList<string> KnownAllergies,
    IReadOnlyList<PrescriptionPrintLine> Lines,
    string? Advice,
    DateOnly? FollowUpDate,
    string? FollowUpAdvice,
    int RefillsAllowed,
    string? VerificationToken,
    string? VerificationPath,
    string? VerificationUrl,
    PrescriptionPrintSignature? DigitalSignature = null);

/// <summary>
/// The doctor's DSC signature as the printed copy states it: "Digitally signed by {SignerName} ({RegistrationNumber}) —
/// certificate {SerialNumber}, {SignedAt}", with the SHA-256 of the signed bytes.
/// </summary>
public sealed record PrescriptionPrintSignature(string SignerName, string? RegistrationNumber, string SerialNumber, DateTimeOffset SignedAt, string ContentHash);

/// <summary>
/// Anonymous verification answer: deliberately free of patient data. <see cref="DigitalSignature"/> is the DSC
/// signature as the server re-checked it for this request (null when the prescription has none);
/// <see cref="SignaturePath"/> downloads it as a .p7s for an independent check.
/// </summary>
public sealed record PrescriptionVerification(
    bool Found,
    string? Status,
    string? Doctor,
    string? Qualification,
    string? RegistrationNumber,
    string? Facility,
    DateOnly? IssuedOn,
    int ItemCount,
    string? PrescriptionNumber = null,
    PublicDigitalSignature? DigitalSignature = null,
    string? SignaturePath = null);

/// <summary>What anyone may see of a DSC signature: no patient data, only the signer, the certificate and the hash.</summary>
public sealed record PublicDigitalSignature(
    DigitalSignatureState State,
    string SignerName,
    string CertificateSerialNumber,
    string CertificateIssuer,
    DateTimeOffset SignedAt,
    string ContentHash,
    bool ChainTrusted,
    string Message);

public enum DigitalSignatureState
{
    /// <summary>Not signed with a DSC.</summary>
    NotSigned = 0,

    /// <summary>The signature checks out against the signed bytes, and the bytes against the prescription as stored.</summary>
    Valid,

    /// <summary>The signature, the signed bytes or the stored prescription were changed.</summary>
    Invalid,
}

/// <summary>Whether a facility's doctors sign prescriptions with their DSC (<c>clinical.prescriptions.signature</c>).</summary>
public enum PrescriptionSignaturePolicy
{
    /// <summary>The default: no DSC signing is offered.</summary>
    Off = 0,

    /// <summary>Doctors may sign with their DSC; prescriptions without one print as before.</summary>
    Optional,

    /// <summary>A prescription is printed, downloaded or shared only once signed with the doctor's DSC.</summary>
    Required,
}

/// <summary>
/// The bytes HC360 Desktop signs (<see cref="PrescriptionCanonicalForm"/>), base64, with what the doctor is shown when
/// choosing the certificate.
/// </summary>
public sealed record PrescriptionSigningContent(
    Guid PrescriptionId,
    string PrescriptionNumber,
    string Format,
    string Content,
    string ContentHash,
    string PatientName,
    string PatientUhid,
    int ItemCount);

/// <summary>The DSC signature from HC360 Desktop: DER-encoded detached CMS/PKCS#7 SignedData, base64.</summary>
public sealed record AttachDigitalSignatureRequest(string Signature);

/// <summary>
/// The DSC signature of a prescription as the server re-checked it now. <see cref="ContentMatchesRecord"/> is false when
/// the stored prescription no longer gives the signed bytes (the record was changed outside HC360).
/// <see cref="ChainStatus"/> is "Trusted" or why the certificate does not chain to a root this server trusts; it is
/// reported, not required.
/// </summary>
public sealed record PrescriptionDigitalSignatureDto(
    DigitalSignatureState State,
    PrescriptionSignaturePolicy Policy,
    DateTimeOffset? SignedAt,
    string? SignerName,
    string? SignerSubject,
    string? CertificateSerialNumber,
    string? CertificateIssuer,
    string? CertificateThumbprint,
    string? ContentHash,
    bool ContentMatchesRecord,
    string? ChainStatus,
    string? Message);

public sealed class PrescriptionItemRequestValidator : AbstractValidator<PrescriptionItemRequest>
{
    public PrescriptionItemRequestValidator()
    {
        RuleFor(x => x.DrugName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.GenericName).MaximumLength(300);
        RuleFor(x => x.Strength).MaximumLength(64);
        RuleFor(x => x.DosageForm).MaximumLength(64);
        RuleFor(x => x.Route).MaximumLength(32);
        RuleFor(x => x.Dose).NotEmpty().MaximumLength(64);
        RuleFor(x => x.CustomFrequency).NotEmpty().When(x => x.Frequency == DoseFrequency.Custom)
            .WithMessage("Write the custom frequency (for example 1-0-1 or 8 hourly).");
        RuleFor(x => x.CustomFrequency).MaximumLength(64);
        RuleFor(x => x.DosesPerDay).GreaterThan(0).LessThanOrEqualTo(24).When(x => x.DosesPerDay is not null);
        RuleFor(x => x.DurationDays).InclusiveBetween(1, 365);
        RuleFor(x => x.Quantity).GreaterThanOrEqualTo(0).LessThanOrEqualTo(10000).When(x => x.Quantity is not null);
        RuleFor(x => x.Instructions).SafeText(500);
        RuleFor(x => x.ControlledSchedule).MaximumLength(8);
    }
}

public sealed class CreatePrescriptionRequestValidator : AbstractValidator<CreatePrescriptionRequest>
{
    public CreatePrescriptionRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.Items).NotEmpty().WithMessage("Add at least one medicine.").Must(i => i is null || i.Count <= 30).WithMessage("At most 30 medicines per prescription.");
        RuleForEach(x => x.Items).SetValidator(new PrescriptionItemRequestValidator());
        RuleFor(x => x.RefillsAllowed).InclusiveBetween(0, 12);
        RuleFor(x => x.Notes).SafeText(1000);
        RuleFor(x => x.Advice).SafeText(2000);
    }
}

public sealed class UpdatePrescriptionRequestValidator : AbstractValidator<UpdatePrescriptionRequest>
{
    public UpdatePrescriptionRequestValidator()
    {
        RuleFor(x => x.Items).NotEmpty().WithMessage("Add at least one medicine.").Must(i => i is null || i.Count <= 30).WithMessage("At most 30 medicines per prescription.");
        RuleForEach(x => x.Items).SetValidator(new PrescriptionItemRequestValidator());
        RuleFor(x => x.RefillsAllowed).InclusiveBetween(0, 12);
        RuleFor(x => x.Notes).SafeText(1000);
        RuleFor(x => x.Advice).SafeText(2000);
    }
}

public sealed class SignPrescriptionRequestValidator : AbstractValidator<SignPrescriptionRequest>
{
    public SignPrescriptionRequestValidator() =>
        RuleFor(x => x.OverrideReason).MinimumLength(10).MaximumLength(1000).When(x => !string.IsNullOrWhiteSpace(x.OverrideReason))
            .WithMessage("Explain the clinical reason for overriding (at least 10 characters).");
}

public sealed class RefillPrescriptionRequestValidator : AbstractValidator<RefillPrescriptionRequest>
{
    public RefillPrescriptionRequestValidator() =>
        RuleFor(x => x.OverrideReason).MinimumLength(10).MaximumLength(1000).When(x => !string.IsNullOrWhiteSpace(x.OverrideReason))
            .WithMessage("Explain the clinical reason for overriding (at least 10 characters).");
}

public sealed class AttachDigitalSignatureRequestValidator : AbstractValidator<AttachDigitalSignatureRequest>
{
    /// <summary>A CMS signature with one certificate is a few kilobytes; 64 KB of base64 is ample.</summary>
    public const int MaxLength = 65_536;

    public AttachDigitalSignatureRequestValidator() =>
        RuleFor(x => x.Signature).NotEmpty().MaximumLength(MaxLength).WithMessage("Send the DSC signature from HC360 Desktop.");
}

public sealed class CancelPrescriptionRequestValidator : AbstractValidator<CancelPrescriptionRequest>
{
    public CancelPrescriptionRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
}
