using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;

namespace HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;

public interface IDischargeContentInput
{
    string? Diagnoses { get; }
    IReadOnlyList<string>? DiagnosisCodes { get; }
    string? PresentingComplaints { get; }
    string? CourseInHospital { get; }
    string? Procedures { get; }
    string? InvestigationsSummary { get; }
    string? ConditionAtDischarge { get; }
    string? Medications { get; }
    string? Advice { get; }
    DateOnly? FollowUpDate { get; }
    string? FollowUpInstructions { get; }
}

/// <summary>Creates or updates the draft summary of an admission. <see cref="Version"/> is required when a draft exists.</summary>
public sealed record SaveDischargeSummaryRequest(
    Guid PatientId,
    Guid? EncounterId,
    Guid? PractitionerId,
    DateTimeOffset? AdmittedAt,
    DateTimeOffset? DischargedAt,
    string? Diagnoses,
    IReadOnlyList<string>? DiagnosisCodes,
    string? PresentingComplaints,
    string? CourseInHospital,
    string? Procedures,
    string? InvestigationsSummary,
    string? ConditionAtDischarge,
    string? Medications,
    string? Advice,
    DateOnly? FollowUpDate,
    string? FollowUpInstructions,
    uint? Version) : IDischargeContentInput;

public sealed record AmendDischargeSummaryRequest(
    string Reason,
    string? Diagnoses,
    IReadOnlyList<string>? DiagnosisCodes,
    string? PresentingComplaints,
    string? CourseInHospital,
    string? Procedures,
    string? InvestigationsSummary,
    string? ConditionAtDischarge,
    string? Medications,
    string? Advice,
    DateOnly? FollowUpDate,
    string? FollowUpInstructions,
    uint Version) : IDischargeContentInput;

public sealed record SignDischargeSummaryRequest(uint Version);

public sealed record DischargeSummaryDto(
    Guid Id,
    Guid AdmissionId,
    Guid PatientId,
    Guid? EncounterId,
    Guid? PractitionerId,
    string? PractitionerName,
    DateTimeOffset? AdmittedAt,
    DateTimeOffset? DischargedAt,
    string? Diagnoses,
    IReadOnlyList<string> DiagnosisCodes,
    string? PresentingComplaints,
    string? CourseInHospital,
    string? Procedures,
    string? InvestigationsSummary,
    string? ConditionAtDischarge,
    string? Medications,
    string? Advice,
    DateOnly? FollowUpDate,
    string? FollowUpInstructions,
    DischargeSummaryStatus Status,
    DateTimeOffset? SignedAt,
    Guid? SignedByPractitionerId,
    string? AmendmentReason,
    DateTimeOffset? AmendedAt,
    int AmendmentCount,
    uint Version);

/// <summary>Suggested content assembled from the structured record of the admission (the doctor edits it before signing).</summary>
public sealed record DischargeSummaryPrefill(
    string? Diagnoses,
    IReadOnlyList<string> DiagnosisCodes,
    string? PresentingComplaints,
    string? Procedures,
    string? InvestigationsSummary,
    string? Medications,
    DateOnly? FollowUpDate,
    string? FollowUpInstructions);

public sealed record DischargeSummaryPrint(DischargeSummaryDto Summary, PatientSummary Patient, FacilityLetterhead? Letterhead, PrescriberInfo? Consultant);

internal static class DischargeContentRules
{
    public static void Apply<T>(AbstractValidator<T> v)
        where T : IDischargeContentInput
    {
        v.RuleFor(x => x.Diagnoses).SafeText(4000);
        v.RuleFor(x => x.PresentingComplaints).SafeText(4000);
        v.RuleFor(x => x.CourseInHospital).SafeText(16000);
        v.RuleFor(x => x.Procedures).SafeText(4000);
        v.RuleFor(x => x.InvestigationsSummary).SafeText(8000);
        v.RuleFor(x => x.ConditionAtDischarge).SafeText(2000);
        v.RuleFor(x => x.Medications).SafeText(4000);
        v.RuleFor(x => x.Advice).SafeText(4000);
        v.RuleFor(x => x.FollowUpInstructions).SafeText(1000);
        v.RuleFor(x => x.DiagnosisCodes).Must(c => c is null || c.Count <= 20).WithMessage("At most 20 diagnosis codes.");
        v.RuleForEach(x => x.DiagnosisCodes).NotEmpty().MaximumLength(16);
    }

    public static DischargeSummaryContent ToContent(this IDischargeContentInput r) => new(
        r.Diagnoses, r.DiagnosisCodes ?? [], r.PresentingComplaints, r.CourseInHospital, r.Procedures, r.InvestigationsSummary, r.ConditionAtDischarge,
        r.Medications, r.Advice, r.FollowUpDate, r.FollowUpInstructions);
}

public sealed class SaveDischargeSummaryRequestValidator : AbstractValidator<SaveDischargeSummaryRequest>
{
    public SaveDischargeSummaryRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.DischargedAt).GreaterThanOrEqualTo(x => x.AdmittedAt).When(x => x.AdmittedAt is not null && x.DischargedAt is not null)
            .WithMessage("Discharge must be after admission.");
        DischargeContentRules.Apply(this);
    }
}

public sealed class AmendDischargeSummaryRequestValidator : AbstractValidator<AmendDischargeSummaryRequest>
{
    public AmendDischargeSummaryRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).MaximumLength(500)
            .WithMessage("Explain what is being corrected and why (at least 10 characters).");
        DischargeContentRules.Apply(this);
    }
}
