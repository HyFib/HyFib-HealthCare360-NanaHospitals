namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>
/// A released diagnostic report waiting for the ordering doctor to review it ("results to review" on the EMR
/// worklist, EMR-01). Created when Diagnostics raises <see cref="DiagnosticReportReleased"/>; critical findings are
/// sorted to the top and stay there until acknowledged.
/// </summary>
public sealed class ResultReview : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid ReportId { get; set; }
    public DiagnosticKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool HasCriticalFindings { get; set; }
    public DateTimeOffset ReleasedAt { get; set; }
    public Guid? OrderId { get; set; }
    public Guid? OrderItemId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? EncounterId { get; set; }

    /// <summary>Practitioner who should review (the ordering doctor).</summary>
    public Guid? PractitionerId { get; set; }

    public DateTimeOffset? ReviewedAt { get; private set; }
    public Guid? ReviewedByUserId { get; private set; }
    public string? ReviewNote { get; private set; }

    public bool IsReviewed => ReviewedAt is not null;

    /// <summary>
    /// A corrected/amended report was released again, or more of its tests were: it needs a fresh review even if the
    /// earlier version was acknowledged, otherwise a changed (possibly critical) result could go unseen. A critical
    /// result not yet acknowledged keeps the review critical, whatever is released with it or after it.
    /// </summary>
    public void Reopen(string title, bool hasCriticalFindings, DateTimeOffset releasedAt)
    {
        Title = title;
        HasCriticalFindings = hasCriticalFindings || (HasCriticalFindings && !IsReviewed);
        ReleasedAt = releasedAt;
        ReviewedAt = null;
        ReviewedByUserId = null;
        ReviewNote = null;
    }

    public Result Acknowledge(Guid? userId, string? note, DateTimeOffset now)
    {
        if (IsReviewed)
        {
            return Error.Rule("result.already_reviewed", "This result has already been reviewed.");
        }

        ReviewedAt = now;
        ReviewedByUserId = userId;
        ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return Result.Success();
    }
}
