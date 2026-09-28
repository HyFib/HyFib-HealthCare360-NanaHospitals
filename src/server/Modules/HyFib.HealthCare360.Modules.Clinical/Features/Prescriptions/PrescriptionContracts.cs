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
    uint Version);

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
    string? VerificationPath);

/// <summary>Anonymous verification answer: deliberately free of patient data.</summary>
public sealed record PrescriptionVerification(
    bool Found,
    string? Status,
    string? Doctor,
    string? Qualification,
    string? RegistrationNumber,
    string? Facility,
    DateOnly? IssuedOn,
    int ItemCount);

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

public sealed class CancelPrescriptionRequestValidator : AbstractValidator<CancelPrescriptionRequest>
{
    public CancelPrescriptionRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
}
