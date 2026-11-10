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

    /// <summary>
    /// Why a doctor other than the ordering doctor acknowledged a critical result as covering the patient (on call, the
    /// unit's doctor on duty); null when the ordering doctor did, or none was asked.
    /// </summary>
    public string? CoveringReason { get; private set; }

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
        CoveringReason = null;
    }

    public Result Acknowledge(Guid? userId, string? note, DateTimeOffset now, string? coveringReason = null)
    {
        if (IsReviewed)
        {
            return Error.Rule("result.already_reviewed", "This result has already been reviewed.");
        }

        ReviewedAt = now;
        ReviewedByUserId = userId;
        ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        CoveringReason = string.IsNullOrWhiteSpace(coveringReason) ? null : coveringReason.Trim();
        return Result.Success();
    }

    /// <summary>
    /// Who may acknowledge a critical lab result where the lab asks for a second pathologist (pure; unit-tested): the doctor
    /// who ordered it, or another doctor who records that they cover the patient, with a reason. Anyone else (a nurse, a
    /// clerk, an account linked to no doctor) is refused: a critical value acknowledged by someone who cannot act on it is
    /// a call that never reached a doctor.
    /// </summary>
    /// <param name="actingPractitionerId">The signed-in user's practitioner record, when it is a doctor's; null otherwise.</param>
    public Result CanAcknowledgeCritical(Guid? actingPractitionerId, string? coveringReason)
    {
        if (actingPractitionerId is not { } doctor)
        {
            return Error.Forbidden("result.critical_doctor_only",
                "Only the ordering doctor, or a doctor covering the patient, acknowledges a critical result. Sign in with a doctor's account linked to their practitioner record.");
        }

        if (doctor == PractitionerId)
        {
            return Result.Success();
        }

        return string.IsNullOrWhiteSpace(coveringReason) || coveringReason.Trim().Length < 10
            ? Error.Field("coveringReason",
                "You did not order this test. To acknowledge the critical result, record that you cover the patient and why (at least 10 characters: on call, the unit's doctor on duty).")
            : Result.Success();
    }
}
