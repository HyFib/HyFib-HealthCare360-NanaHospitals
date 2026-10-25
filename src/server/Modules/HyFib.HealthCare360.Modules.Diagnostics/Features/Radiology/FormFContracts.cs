using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;

/// <summary>A study's PCPNDT Form F as recorded so far.</summary>
public sealed record FormFDto(
    Guid Id,
    string FormNumber,
    Guid StudyId,
    string AccessionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    FormFStatus Status,
    string RegistrationNumber,
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
    IReadOnlyList<FormFIndication> Indications,
    string? OtherIndication,
    Guid? SignedFormFileId,
    DateTimeOffset DeclaredAt,
    string? DeclarationRecordedBy,
    DateOnly? ProcedureDate,
    string? Result,
    string? ResultConveyedTo,
    DateOnly? ResultConveyedOn,
    bool MtpIndicated,
    string? MtpIndication,
    string? DoctorName,
    DateTimeOffset? CompletedAt,
    uint Version);

/// <summary>The woman's details and her declaration, recorded before the ultrasound. <paramref name="Version"/>: when correcting it.</summary>
public sealed record SaveFormFDeclarationRequest(
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
    IReadOnlyList<FormFIndication> Indications,
    string? OtherIndication,
    Guid? SignedFormFileId,
    bool WomanDeclared,
    uint? Version = null);

/// <summary>The result and the performing doctor's declaration that the sex of the foetus was neither detected nor disclosed.</summary>
public sealed record CompleteFormFRequest(
    string Result, string ConveyedTo, DateOnly ConveyedOn, bool MtpIndicated, string? MtpIndication, bool DoctorDeclared, uint Version);

/// <summary>A line of the month's Form F register, as the Appropriate Authority receives it.</summary>
public sealed record FormFRegisterRow(
    Guid Id,
    string FormNumber,
    Guid StudyId,
    string AccessionNumber,
    string Uhid,
    string PatientName,
    int AgeYears,
    string HusbandOrFatherName,
    int LivingSons,
    int LivingDaughters,
    int? WeeksOfPregnancy,
    DateOnly? LastMenstrualPeriod,
    string ReferredBy,
    IReadOnlyList<FormFIndication> Indications,
    DateOnly? ProcedureDate,
    string? Result,
    bool MtpIndicated,
    string? DoctorName,
    FormFStatus Status);

/// <summary>
/// The month's forms: completed ones by the date of the ultrasound, and any still open from the month, which must be
/// completed before the return is sent (by the 5th of the next month).
/// </summary>
public sealed record FormFRegister(int Year, int Month, string RegistrationNumber, IReadOnlyList<FormFRegisterRow> Rows, int Incomplete);

public sealed class SaveFormFDeclarationRequestValidator : AbstractValidator<SaveFormFDeclarationRequest>
{
    public SaveFormFDeclarationRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.AgeYears).InclusiveBetween(10, 60).WithMessage("Enter the woman's age in years.");
        RuleFor(x => x.HusbandOrFatherName).NotEmpty().WithMessage("Enter the name of her husband or father.").MaximumLength(200);
        RuleFor(x => x.Address).NotEmpty().WithMessage("Enter her address.").MaximumLength(500);
        RuleFor(x => x.Phone).MaximumLength(20);
        RuleFor(x => x.LivingSons).InclusiveBetween(0, 20);
        RuleFor(x => x.LivingDaughters).InclusiveBetween(0, 20);
        RuleFor(x => x.LastMenstrualPeriod)
            .Must(d => d is null || d <= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddDays(1)))
            .WithMessage("The last menstrual period cannot be in the future.");
        RuleFor(x => x.LastMenstrualPeriod).NotNull().When(x => x.WeeksOfPregnancy is null)
            .WithMessage("Enter the last menstrual period or the weeks of pregnancy.");
        RuleFor(x => x.WeeksOfPregnancy).InclusiveBetween(1, FormF.MaxWeeksOfPregnancy).When(x => x.WeeksOfPregnancy is not null)
            .WithMessage($"Enter the weeks of pregnancy, 1 to {FormF.MaxWeeksOfPregnancy}.");
        RuleFor(x => x.Referral).IsInEnum();
        RuleFor(x => x.ReferringDoctorName).NotEmpty().When(x => x.Referral == FormFReferral.Doctor)
            .WithMessage("Enter the name of the referring doctor.").MaximumLength(200);
        RuleFor(x => x.ReferringDoctorAddress).NotEmpty().When(x => x.Referral == FormFReferral.Doctor)
            .WithMessage("Enter the address of the referring doctor.").MaximumLength(500);
        RuleFor(x => x.Indications).NotEmpty().WithMessage("Choose at least one indication for the ultrasound.");
        RuleForEach(x => x.Indications).IsInEnum();
        RuleFor(x => x.OtherIndication).MaximumLength(500);
        RuleFor(x => x.WomanDeclared).Equal(true)
            .WithMessage("The woman must declare that she does not want to know the sex of the foetus before the ultrasound.");
    }
}

public sealed class CompleteFormFRequestValidator : AbstractValidator<CompleteFormFRequest>
{
    public CompleteFormFRequestValidator()
    {
        RuleFor(x => x.Result).NotEmpty().WithMessage("Enter the result of the ultrasound.").MaximumLength(2000);
        RuleFor(x => x.ConveyedTo).NotEmpty().WithMessage("Enter whom the result was given to.").MaximumLength(200);
        RuleFor(x => x.MtpIndication).NotEmpty().When(x => x.MtpIndicated)
            .WithMessage("Describe the abnormality that indicates a termination of pregnancy.").MaximumLength(1000);
        RuleFor(x => x.DoctorDeclared).Equal(true)
            .WithMessage("Declare that you neither detected nor disclosed the sex of the foetus to anyone.");
    }
}
