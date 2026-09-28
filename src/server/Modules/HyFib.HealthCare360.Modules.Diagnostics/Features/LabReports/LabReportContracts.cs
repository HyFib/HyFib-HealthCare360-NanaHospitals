using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;

public sealed record LabReportListItem(
    Guid Id,
    string ReportNumber,
    Guid LabOrderId,
    Guid PatientId,
    string Uhid,
    string PatientName,
    LabReportStatus Status,
    bool IsPartial,
    int Revision,
    DateTimeOffset OrderedAt,
    DateTimeOffset? ReleasedAt);

public sealed record ReleaseOutcome(Guid ReportId, IReadOnlyList<Guid> ReleasedItemIds, bool IsPartial, string? VerificationCode);

public sealed record AmendmentChange(Guid ResultId, string Value, string? Remarks, string? Organism, string? ColonyCount);

/// <summary>Post-release correction. <see cref="CriticalCall"/> is required when a corrected value is critical.</summary>
public sealed record AmendReportRequest(string Reason, IReadOnlyList<AmendmentChange> Changes, CriticalCallDetails? CriticalCall);

// ---------------------------------------------------------------- printable report (shared by lab, radiology, cardiology)

/// <summary>Facility letterhead printed on every report (UX-09).</summary>
public sealed record ReportLetterhead(
    string FacilityName, string? Address, string? Phone, string? Email, string? NabhNumber, string? LetterheadNote, Guid? LogoFileId);

public sealed record ReportPatient(Guid Id, string Uhid, string Name, Gender Gender, string? Age, DateOnly? DateOfBirth);

/// <summary>Who signed the report (qualification and council registration number are mandatory on Indian reports).</summary>
public sealed record ReportSignatory(Guid UserId, string Name, string? Qualification, string? RegistrationNumber, Guid? SignatureFileId);

public sealed record LabReportResultLine(
    Guid ResultId,
    string Name,
    string? GroupName,
    string? Value,
    string? Unit,
    ResultFlag? Flag,
    string? ReferenceText,
    LabResultStatus Status,
    bool AutoValidated,
    string? Remarks,
    string? Organism,
    string? ColonyCount,
    IReadOnlyList<SusceptibilityDto> Susceptibilities,
    decimal? PreviousValue,
    DateTimeOffset? PreviousResultAt);

public sealed record LabReportTest(
    Guid OrderItemId,
    string Code,
    string Name,
    LabOrderItemStatus Status,
    SpecimenType SpecimenType,
    string? Method,
    string? Interpretation,
    string? ReferralLabName,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? ReleasedAt,
    IReadOnlyList<LabReportResultLine> Results);

public sealed record LabReportSection(LabSection Section, IReadOnlyList<LabReportTest> Tests);

public sealed record LabAmendmentView(int Revision, string Reason, string Changes, DateTimeOffset AmendedAt, string? AmendedBy);

/// <summary>
/// Everything needed to render the A4 report (PrintDocument in the UI) including the QR verification code (LIS-07).
/// Portal copies contain released tests only.
/// </summary>
public sealed record LabReportView(
    Guid ReportId,
    Guid LabOrderId,
    string ReportNumber,
    LabReportStatus Status,
    bool IsPartial,
    int Revision,
    ReportLetterhead Letterhead,
    ReportPatient Patient,
    PatientClass PatientClass,
    string? ReferredBy,
    string? ClinicalNotes,
    DateTimeOffset OrderedAt,
    DateTimeOffset? FirstReleasedAt,
    DateTimeOffset? ReleasedAt,
    IReadOnlyList<LabReportSection> Sections,
    IReadOnlyList<ReportSignatory> Signatories,
    bool HasAutoValidatedResults,
    IReadOnlyList<LabAmendmentView> Amendments,
    string? VerificationCode,
    string? VerificationPath);

/// <summary>Public QR verification answer: proves the report is genuine without disclosing results or identity.</summary>
public sealed record ReportAuthenticity(
    bool Valid,
    DiagnosticKind Kind,
    string ReportNumber,
    string FacilityName,
    string PatientInitials,
    DateTimeOffset? ReleasedAt,
    IReadOnlyList<string> Tests,
    int Revision);

public sealed class AmendReportRequestValidator : AbstractValidator<AmendReportRequest>
{
    public AmendReportRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).WithMessage("Explain the correction (at least 10 characters).").SafeText(500);
        RuleFor(x => x.Changes).NotEmpty().Must(c => c.Count <= 100).WithMessage("At most 100 corrections at a time.")
            .Must(c => c.Select(e => e.ResultId).Distinct().Count() == c.Count).WithMessage("Each result can be corrected once per amendment.");
        RuleForEach(x => x.Changes).ChildRules(c =>
        {
            c.RuleFor(e => e.ResultId).NotEmpty();
            c.RuleFor(e => e.Value).NotEmpty().SafeText(2000);
            c.RuleFor(e => e.Remarks).SafeText(1000);
            c.RuleFor(e => e.Organism).SafeText(200);
            c.RuleFor(e => e.ColonyCount).SafeText(100);
        });
        RuleFor(x => x.CriticalCall!).SetValidator(new CriticalCallDetailsValidator()).When(x => x.CriticalCall is not null);
    }
}
