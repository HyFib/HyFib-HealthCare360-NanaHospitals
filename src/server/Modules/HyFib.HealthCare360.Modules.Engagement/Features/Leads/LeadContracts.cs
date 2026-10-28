using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Leads;

public sealed record LeadListItem(
    Guid Id,
    string LeadNumber,
    string Name,
    string? PhoneMasked,
    LeadSource Source,
    LeadStatus Status,
    string? InterestPackage,
    Guid? InterestDepartmentId,
    Guid? OwnerUserId,
    DateTimeOffset? NextFollowUpAt,
    DateTimeOffset? LastContactedAt,
    DateTimeOffset CreatedAt,
    bool IsFollowUpDue);

public sealed record LeadActivityDto(Guid Id, LeadActivityType Type, DateTimeOffset OccurredAt, string? Outcome, string? Notes, Guid? PerformedByUserId);

public sealed record LeadDetail(
    Guid Id,
    string LeadNumber,
    string Name,
    string? Phone,
    string? Email,
    Gender? Gender,
    int? AgeYears,
    string? City,
    LeadSource Source,
    string? SourceDetail,
    Guid? CampaignId,
    Guid? ReferringDoctorId,
    Guid? InterestDepartmentId,
    string? InterestPackage,
    string? Notes,
    LeadStatus Status,
    string? LostReason,
    Guid? OwnerUserId,
    DateTimeOffset? NextFollowUpAt,
    DateTimeOffset? LastContactedAt,
    Guid? ConvertedPatientId,
    Guid? ConvertedAppointmentId,
    DateTimeOffset? ConvertedAt,
    IReadOnlyList<LeadActivityDto> Activities,
    uint Version);

public sealed record SaveLeadRequest(
    string Name,
    string? Phone,
    string? Email,
    Gender? Gender,
    int? AgeYears,
    string? City,
    LeadSource Source,
    string? SourceDetail,
    Guid? CampaignId,
    Guid? ReferringDoctorId,
    Guid? InterestDepartmentId,
    string? InterestPackage,
    string? Notes,
    Guid? OwnerUserId,
    DateTimeOffset? NextFollowUpAt,
    bool ConfirmedNotDuplicate,
    uint? Version);

/// <summary>Created lead; <see cref="ExistingPatientId"/> hints that the caller is already a registered patient.</summary>
public sealed record LeadCreated(Guid Id, string LeadNumber, Guid? ExistingPatientId);

public sealed record AddLeadActivityRequest(LeadActivityType Type, DateTimeOffset? OccurredAt, string? Outcome, string? Notes, DateTimeOffset? NextFollowUpAt);

public sealed record ChangeLeadStatusRequest(LeadStatus Status, string? LostReason);

/// <summary>Details needed to register the lead as a new patient during conversion.</summary>
public sealed record NewPatientDetails(Gender Gender, DateOnly? DateOfBirth, int? AgeYears, bool ConfirmedNotDuplicate);

/// <summary>Convert to an existing patient, register a new one, and/or link the booked appointment.</summary>
public sealed record ConvertLeadRequest(Guid? PatientId, NewPatientDetails? RegisterPatient, Guid? AppointmentId);

public sealed record LeadConversion(Guid LeadId, Guid? PatientId, string? Uhid, Guid? AppointmentId);

internal static class LeadRules
{
    public static bool ValidName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(c => char.IsLetter(c) || c is ' ' or '.' or '\'' or '-');
}

public sealed class SaveLeadRequestValidator : AbstractValidator<SaveLeadRequest>
{
    public SaveLeadRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).Must(LeadRules.ValidName).WithMessage("Use letters, spaces, dots, apostrophes or hyphens.");
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.Phone) || !string.IsNullOrWhiteSpace(x.Email)).WithName("phone")
            .WithMessage("Enter a phone number or an e-mail address to follow up.");
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.AgeYears).InclusiveBetween(0, 120);
        RuleFor(x => x.City).SafeText(100);
        RuleFor(x => x.SourceDetail).SafeText(200);
        RuleFor(x => x.InterestPackage).SafeText(200);
        RuleFor(x => x.Notes).SafeText(2000);
    }
}

public sealed class AddLeadActivityRequestValidator : AbstractValidator<AddLeadActivityRequest>
{
    public AddLeadActivityRequestValidator()
    {
        RuleFor(x => x.Outcome).SafeText(200);
        RuleFor(x => x.Notes).SafeText(2000);
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.Outcome) || !string.IsNullOrWhiteSpace(x.Notes)).WithName("notes")
            .WithMessage("Record the outcome or a note.");
    }
}

public sealed class ChangeLeadStatusRequestValidator : AbstractValidator<ChangeLeadStatusRequest>
{
    public ChangeLeadStatusRequestValidator() => RuleFor(x => x.LostReason).SafeText(300);
}

public sealed class ConvertLeadRequestValidator : AbstractValidator<ConvertLeadRequest>
{
    public ConvertLeadRequestValidator(TimeProvider time)
    {
        RuleFor(x => x).Must(x => x.PatientId is not null || x.RegisterPatient is not null || x.AppointmentId is not null).WithName("patientId")
            .WithMessage("Choose an existing patient, register a new one, or link an appointment.");
        RuleFor(x => x).Must(x => x.PatientId is null || x.RegisterPatient is null).WithName("registerPatient")
            .WithMessage("Choose either an existing patient or a new registration, not both.");
        When(x => x.RegisterPatient is not null, () =>
        {
            RuleFor(x => x.RegisterPatient!).Must(r => r.DateOfBirth is not null || r.AgeYears is not null).WithName("registerPatient.dateOfBirth")
                .WithMessage("Enter the date of birth or the age.");
            RuleFor(x => x.RegisterPatient!.DateOfBirth).Must(d => d is null || d <= time.Today())
                .WithMessage("Enter a valid date of birth.");
            RuleFor(x => x.RegisterPatient!.AgeYears).InclusiveBetween(0, 130);
        });
    }
}
