namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

/// <summary>Report text as edited by the radiologist.</summary>
public sealed record RadiologyReportContent(
    Guid? TemplateId,
    string? Technique,
    string? ClinicalHistory,
    string? Comparison,
    string Findings,
    string Impression,
    bool IsCriticalFinding,
    string? CriticalFindingNote);

/// <summary>
/// Radiologist's report for one study (IMG-03). Workflow: Draft → Reported (submitted) → Verified (signed off) →
/// Released (EHR + portal). A critical finding cannot be verified without a documented call. After sign-off the text is
/// frozen, and corrections are added as addenda so that the original wording stays auditable.
/// </summary>
public sealed class RadiologyReport : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid ImagingStudyId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? TemplateId { get; set; }
    public string? Technique { get; set; }
    public string? ClinicalHistory { get; set; }
    public string? Comparison { get; set; }
    public string Findings { get; set; } = string.Empty;
    public string Impression { get; set; } = string.Empty;
    public bool IsCriticalFinding { get; set; }
    public string? CriticalFindingNote { get; set; }
    public DiagnosticReportStatus Status { get; set; }
    public Guid? ReportedBy { get; set; }
    public DateTimeOffset? ReportedAt { get; set; }
    public Guid? VerifiedBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? ReleasedBy { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? VerificationCode { get; set; }

    public List<RadiologyAddendum> Addenda { get; set; } = [];

    public Result Save(RadiologyReportContent content)
    {
        if (Status is DiagnosticReportStatus.Verified or DiagnosticReportStatus.Released)
        {
            return Error.Rule("radiology.report_signed", "The report is signed; add an addendum instead of editing it.");
        }

        if (content.IsCriticalFinding && string.IsNullOrWhiteSpace(content.CriticalFindingNote))
        {
            return Error.Field("criticalFindingNote", "Describe the critical finding.");
        }

        TemplateId = content.TemplateId ?? TemplateId;
        Technique = Clean(content.Technique);
        ClinicalHistory = Clean(content.ClinicalHistory);
        Comparison = Clean(content.Comparison);
        Findings = content.Findings.Trim();
        Impression = content.Impression.Trim();
        IsCriticalFinding = content.IsCriticalFinding;
        CriticalFindingNote = content.IsCriticalFinding ? Clean(content.CriticalFindingNote) : null;
        return Result.Success();
    }

    public Result Submit(Guid? by, DateTimeOffset now)
    {
        if (Status != DiagnosticReportStatus.Draft)
        {
            return Error.Rule("radiology.report_submitted", "The report was already submitted.");
        }

        var complete = EnsureComplete();
        if (complete.IsFailure)
        {
            return complete;
        }

        Status = DiagnosticReportStatus.Reported;
        ReportedBy = by;
        ReportedAt = now;
        return Result.Success();
    }

    /// <summary>Sign-off. A radiologist may sign a draft directly (report and verify in one step).</summary>
    public Result Verify(Guid? by, DateTimeOffset now)
    {
        if (Status is DiagnosticReportStatus.Verified or DiagnosticReportStatus.Released)
        {
            return Error.Rule("radiology.report_signed", "The report is already signed.");
        }

        var complete = EnsureComplete();
        if (complete.IsFailure)
        {
            return complete;
        }

        ReportedBy ??= by;
        ReportedAt ??= now;
        Status = DiagnosticReportStatus.Verified;
        VerifiedBy = by;
        VerifiedAt = now;
        return Result.Success();
    }

    /// <summary>Makes the report visible in the EHR and portal and announces it (DiagnosticReportReleased).</summary>
    public Result Release(ImagingStudy study, Guid? by, DateTimeOffset now)
    {
        if (Status != DiagnosticReportStatus.Verified)
        {
            return Error.Rule("radiology.report_not_verified", "Sign off the report before releasing it.");
        }

        Status = DiagnosticReportStatus.Released;
        ReleasedBy = by;
        ReleasedAt = now;
        VerificationCode ??= Domain.VerificationCode.New();
        study.MarkReleased(now);
        Raise(new DiagnosticReportReleased(
            Id, DiagnosticKind.Radiology, PatientId, study.VisitId, study.SourceOrderItemId, study.StudyName, IsCriticalFinding, now));
        return Result.Success();
    }

    /// <summary>Adds a signed addendum; a released report is re-announced so the EHR shows the addition.</summary>
    public Result AddAddendum(ImagingStudy study, string text, string? reason, bool isCriticalFinding, Guid? by, DateTimeOffset now)
    {
        if (Status is not (DiagnosticReportStatus.Verified or DiagnosticReportStatus.Released))
        {
            return Error.Rule("radiology.report_not_signed", "Edit the report directly until it is signed; addenda are for signed reports.");
        }

        Addenda.Add(new RadiologyAddendum
        {
            RadiologyReportId = Id,
            Text = text.Trim(),
            Reason = Clean(reason),
            IsCriticalFinding = isCriticalFinding,
            AddedAt = now,
            AddedBy = by,
        });

        if (Status == DiagnosticReportStatus.Released)
        {
            Raise(new DiagnosticReportReleased(
                Id, DiagnosticKind.Radiology, PatientId, study.VisitId, study.SourceOrderItemId, $"{study.StudyName} (addendum)",
                IsCriticalFinding || isCriticalFinding, now));
        }

        return Result.Success();
    }

    private Result EnsureComplete() =>
        string.IsNullOrWhiteSpace(Findings) || string.IsNullOrWhiteSpace(Impression)
            ? Error.Rule("radiology.report_incomplete", "Enter the findings and the impression.")
            : Result.Success();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Signed addition to a signed radiology report (the original text is never edited).</summary>
public sealed class RadiologyAddendum : AuditableEntity
{
    public Guid RadiologyReportId { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public bool IsCriticalFinding { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public Guid? AddedBy { get; set; }
}
