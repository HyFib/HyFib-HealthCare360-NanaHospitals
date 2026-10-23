using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Encounters;

public sealed record StartEncounterRequest(
    Guid PatientId,
    Guid? VisitId,
    Guid? AdmissionId,
    EncounterType? Type,
    Guid? DepartmentId,
    Guid? PractitionerId,
    Guid? TemplateId,
    string? ChiefComplaint);

/// <summary>SOAP, follow-up and referral fields shared by update and amendment requests.</summary>
public interface IEncounterContentInput
{
    string? ChiefComplaint { get; }
    string? HistoryOfPresentIllness { get; }
    string? Examination { get; }
    string? Assessment { get; }
    string? Plan { get; }
    DateOnly? FollowUpDate { get; }
    string? FollowUpAdvice { get; }
    Guid? ReferralDepartmentId { get; }
    Guid? ReferralPractitionerId { get; }
    string? ReferralExternal { get; }
    string? ReferralReason { get; }
}

public sealed record UpdateEncounterRequest(
    string? ChiefComplaint,
    string? HistoryOfPresentIllness,
    string? Examination,
    string? Assessment,
    string? Plan,
    DateOnly? FollowUpDate,
    string? FollowUpAdvice,
    Guid? ReferralDepartmentId,
    Guid? ReferralPractitionerId,
    string? ReferralExternal,
    string? ReferralReason,
    uint Version) : IEncounterContentInput;

public sealed record AmendEncounterRequest(
    string Reason,
    string? ChiefComplaint,
    string? HistoryOfPresentIllness,
    string? Examination,
    string? Assessment,
    string? Plan,
    DateOnly? FollowUpDate,
    string? FollowUpAdvice,
    Guid? ReferralDepartmentId,
    Guid? ReferralPractitionerId,
    string? ReferralExternal,
    string? ReferralReason,
    uint Version) : IEncounterContentInput;

public sealed record SignEncounterRequest(uint Version);

/// <summary>A doctor signs the encounter as they read it: its version says which.</summary>
public sealed class SignEncounterRequestValidator : AbstractValidator<SignEncounterRequest>
{
    public SignEncounterRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

public sealed record EncounterListItem(
    Guid Id,
    Guid PatientId,
    EncounterType Type,
    EncounterStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? SignedAt,
    Guid PractitionerId,
    string? PractitionerName,
    Guid? DepartmentId,
    string? DepartmentName,
    string? ChiefComplaint,
    IReadOnlyList<string> Diagnoses);

public sealed record EncounterAmendmentDto(Guid Id, DateTimeOffset AmendedAt, Guid? AmendedByUserId, string Reason, string PreviousContent);

public sealed record EncounterDetail(
    Guid Id,
    Guid PatientId,
    Guid? VisitId,
    Guid? AdmissionId,
    Guid PractitionerId,
    string? PractitionerName,
    Guid? DepartmentId,
    string? DepartmentName,
    EncounterType Type,
    EncounterStatus Status,
    DateTimeOffset StartedAt,
    string? ChiefComplaint,
    string? HistoryOfPresentIllness,
    string? Examination,
    string? Assessment,
    string? Plan,
    DateOnly? FollowUpDate,
    string? FollowUpAdvice,
    Guid? ReferralDepartmentId,
    Guid? ReferralPractitionerId,
    string? ReferralExternal,
    string? ReferralReason,
    Guid? TemplateId,
    DateTimeOffset? SignedAt,
    Guid? SignedByPractitionerId,
    string? AmendmentReason,
    DateTimeOffset? AmendedAt,
    IReadOnlyList<EncounterAmendmentDto> Amendments,
    uint Version);

public static class EncounterMapping
{
    public static EncounterContent ToContent(this IEncounterContentInput r) => new(
        r.ChiefComplaint, r.HistoryOfPresentIllness, r.Examination, r.Assessment, r.Plan, r.FollowUpDate, r.FollowUpAdvice,
        r.ReferralDepartmentId, r.ReferralPractitionerId, r.ReferralExternal, r.ReferralReason);

    public static EncounterDetail ToDetail(Encounter e, string? practitionerName, string? departmentName) => new(
        e.Id, e.PatientId, e.VisitId, e.AdmissionId, e.PractitionerId, practitionerName, e.DepartmentId, departmentName, e.Type, e.Status,
        e.StartedAt, e.ChiefComplaint, e.HistoryOfPresentIllness, e.Examination, e.Assessment, e.Plan, e.FollowUpDate, e.FollowUpAdvice,
        e.ReferralDepartmentId, e.ReferralPractitionerId, e.ReferralExternal, e.ReferralReason, e.TemplateId, e.SignedAt, e.SignedByPractitionerId,
        e.AmendmentReason, e.AmendedAt,
        e.Amendments.OrderByDescending(a => a.AmendedAt).Select(a => new EncounterAmendmentDto(a.Id, a.AmendedAt, a.AmendedByUserId, a.Reason, a.PreviousContent)).ToList(),
        e.Version);
}

internal static class EncounterContentRules
{
    public static void Apply<T>(AbstractValidator<T> v, TimeProvider time)
        where T : IEncounterContentInput
    {
        var today = time.Today();
        v.RuleFor(x => x.ChiefComplaint).SafeText(1000);
        v.RuleFor(x => x.HistoryOfPresentIllness).SafeText(8000);
        v.RuleFor(x => x.Examination).SafeText(8000);
        v.RuleFor(x => x.Assessment).SafeText(4000);
        v.RuleFor(x => x.Plan).SafeText(8000);
        v.RuleFor(x => x.FollowUpAdvice).SafeText(1000);
        v.RuleFor(x => x.ReferralExternal).SafeText(300);
        v.RuleFor(x => x.ReferralReason).SafeText(1000);
        v.RuleFor(x => x.FollowUpDate).Must(d => d is null || (d >= today && d <= today.AddYears(2)))
            .WithMessage("Choose a follow-up date between today and two years ahead.");
        v.RuleFor(x => x.ReferralReason).NotEmpty()
            .When(x => x.ReferralDepartmentId is not null || x.ReferralPractitionerId is not null || !string.IsNullOrWhiteSpace(x.ReferralExternal))
            .WithMessage("Give the reason for the referral.");
    }
}

public sealed class StartEncounterRequestValidator : AbstractValidator<StartEncounterRequest>
{
    public StartEncounterRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.ChiefComplaint).SafeText(1000);
        RuleFor(x => x.AdmissionId).NotNull().When(x => x.Type == EncounterType.InPatient && x.VisitId is null)
            .WithMessage("An in-patient encounter needs the admission.");
    }
}

public sealed class UpdateEncounterRequestValidator : AbstractValidator<UpdateEncounterRequest>
{
    public UpdateEncounterRequestValidator(TimeProvider time) => EncounterContentRules.Apply(this, time);
}

public sealed class AmendEncounterRequestValidator : AbstractValidator<AmendEncounterRequest>
{
    public AmendEncounterRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).MaximumLength(500)
            .WithMessage("Explain what is being corrected and why (at least 10 characters).");
        EncounterContentRules.Apply(this, time);
    }
}
