using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;

public sealed record SaveDonorRequest(
    string FirstName, string? LastName, Gender Gender, DateOnly DateOfBirth, BloodGroup? BloodGroup, string Phone, string? Email, Address? Address,
    string? Occupation, bool ConsentToNotify, string? Notes, bool IsActive = true, uint? Version = null);

public sealed record DonorListItem(
    Guid Id, string DonorNumber, string FullName, Gender Gender, string Age, BloodGroup BloodGroup, bool BloodGroupConfirmed, string PhoneMasked,
    int DonationCount, DateTimeOffset? LastDonationAt, bool IsPermanentlyDeferred, DateOnly? DeferredUntil, DateOnly? NextEligibleOn);

public sealed record DonorScreeningDto(
    Guid Id, DateTimeOffset ScreenedAt, string? ScreenedByName, decimal HaemoglobinGdl, decimal WeightKg, int Systolic, int Diastolic, int Pulse,
    decimal TemperatureC, IReadOnlyList<ScreeningAnswer> Answers, DonorEligibilityOutcome Outcome, IReadOnlyList<string> Reasons, DateOnly? DeferredUntil,
    int? MaxBagVolumeMl, Guid? DonationId);

public sealed record DonationListItem(
    Guid Id, string BagNumber, Guid DonorId, string DonorNumber, string DonorName, BloodBagType BagType, int VolumeMl, DonationType Type,
    DateTimeOffset CollectedAt, DonationStatus Status, BloodGroup BloodGroup, bool TtiComplete, bool TtiReactive);

public sealed record DonorDetail(
    Guid Id, string DonorNumber, string FirstName, string? LastName, string FullName, Gender Gender, DateOnly DateOfBirth, string Age, BloodGroup BloodGroup,
    bool BloodGroupConfirmed, string Phone, string? Email, Address Address, string? Occupation, bool ConsentToNotify, int DonationCount,
    DateTimeOffset? LastDonationAt, DateOnly? NextEligibleOn, DateOnly? DeferredUntil, bool IsPermanentlyDeferred, string? DeferralReason, string? Notes,
    bool IsActive, IReadOnlyList<DonorScreeningDto> Screenings, IReadOnlyList<DonationListItem> Donations, uint Version);

public sealed record DeferDonorRequest(DateOnly? Until, bool Permanent, string Reason);

public sealed record ScreenDonorRequest(
    decimal HaemoglobinGdl, decimal WeightKg, int Systolic, int Diastolic, int Pulse, decimal TemperatureC, IReadOnlyList<ScreeningAnswer> Answers);

/// <summary>Questionnaire shown at the screening desk (from <see cref="DonorEligibility.Questions"/>).</summary>
public sealed record DonorQuestionDto(string Key, string Text, Gender? OnlyFor, int? DeferralDays, bool PermanentDeferral);

public sealed record CollectDonationRequest(
    Guid ScreeningId, string? BagNumber, BloodBagType BagType, int VolumeMl, DonationType Type, Guid? ReplacementForPatientId,
    DateTimeOffset? CollectedAt, string? DonorReaction, string? Notes);

public sealed record ComponentPlan(BloodComponentType Type, int? VolumeMl, string? StorageLocation);

public sealed record SeparateComponentsRequest(IReadOnlyList<ComponentPlan> Components);

public sealed record RecordLabResultsRequest(BloodGroup BloodGroup, TtiResult Hiv, TtiResult HbsAg, TtiResult Hcv, TtiResult Syphilis, TtiResult Malaria)
{
    public TtiPanel ToPanel() => new() { Hiv = Hiv, HbsAg = HbsAg, Hcv = Hcv, Syphilis = Syphilis, Malaria = Malaria };
}

public sealed record DonationDetail(
    Guid Id, string BagNumber, Guid DonorId, string DonorNumber, string DonorName, BloodBagType BagType, int VolumeMl, DonationType Type,
    Guid? ReplacementForPatientId, DateTimeOffset CollectedAt, string? CollectedByName, DonationStatus Status, BloodGroup BloodGroup, TtiPanel Tti,
    DateTimeOffset? LabResultsAt, DateTimeOffset? SeparatedAt, string? DonorReaction, string? Notes, IReadOnlyList<BloodUnitDto> Components, uint Version);

public sealed class SaveDonorRequestValidator : AbstractValidator<SaveDonorRequest>
{
    public SaveDonorRequestValidator(TimeProvider time)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).MaximumLength(100);
        RuleFor(x => x.DateOfBirth).Must(d => d < today && d.Year >= 1900).WithMessage("Enter a valid date of birth.");
        RuleFor(x => x.Phone).NotEmpty().WithMessage("A mobile number is needed to recall the donor.").IndianMobile();
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address!.PinCode).PinCode().When(x => !string.IsNullOrWhiteSpace(x.Address?.PinCode));
        RuleFor(x => x.Occupation).MaximumLength(100);
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class DeferDonorRequestValidator : AbstractValidator<DeferDonorRequest>
{
    public DeferDonorRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().SafeText(500);
        RuleFor(x => x.Until).NotNull().When(x => !x.Permanent).WithMessage("Give the date until which the donor is deferred.");
    }
}

public sealed class ScreenDonorRequestValidator : AbstractValidator<ScreenDonorRequest>
{
    public ScreenDonorRequestValidator()
    {
        RuleFor(x => x.HaemoglobinGdl).InclusiveBetween(3m, 25m).WithMessage("Enter haemoglobin between 3 and 25 g/dL.");
        RuleFor(x => x.WeightKg).WeightKg();
        RuleFor(x => x.Systolic).Systolic();
        RuleFor(x => x.Diastolic).Diastolic();
        RuleFor(x => x.Pulse).Pulse();
        RuleFor(x => x.TemperatureC).TemperatureC();
        RuleFor(x => x.Answers).NotNull().Must(a => a.Count <= 50).WithMessage("Too many answers.");
        RuleForEach(x => x.Answers).ChildRules(a => a.RuleFor(v => v.Key).NotEmpty().MaximumLength(64));
    }
}

public sealed class CollectDonationRequestValidator : AbstractValidator<CollectDonationRequest>
{
    public CollectDonationRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.ScreeningId).NotEmpty();
        RuleFor(x => x.BagNumber).MaximumLength(24).Matches("^[A-Za-z0-9-]+$").When(x => !string.IsNullOrWhiteSpace(x.BagNumber))
            .WithMessage("Scan or type the bag barcode (letters, digits and hyphens).");
        RuleFor(x => x.VolumeMl).Must(v => v is 350 or 450).WithMessage("Collect 350 ml or 450 ml.");
        RuleFor(x => x.ReplacementForPatientId).NotEmpty().When(x => x.Type == DonationType.Replacement)
            .WithMessage("Choose the patient this replacement donation is for.");
        RuleFor(x => x.CollectedAt).Must(at => at is null || at <= time.GetUtcNow().AddMinutes(5)).WithMessage("The collection time cannot be in the future.");
        RuleFor(x => x.DonorReaction).SafeText(500);
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class SeparateComponentsRequestValidator : AbstractValidator<SeparateComponentsRequest>
{
    public SeparateComponentsRequestValidator()
    {
        RuleFor(x => x.Components).NotEmpty().Must(c => c.Count <= 4).WithMessage("Choose 1 to 4 components.");
        RuleForEach(x => x.Components).ChildRules(c =>
        {
            c.RuleFor(v => v.VolumeMl).InclusiveBetween(5, 600);
            c.RuleFor(v => v.StorageLocation).MaximumLength(64);
        });
    }
}

public sealed class RecordLabResultsRequestValidator : AbstractValidator<RecordLabResultsRequest>
{
    public RecordLabResultsRequestValidator() =>
        RuleFor(x => x.BloodGroup).NotEqual(BloodGroup.Unknown).WithMessage("Enter the ABO and Rh(D) group.");
}
