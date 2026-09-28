namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

/// <summary>
/// Preliminary finding from an external AI triage engine (IMG-05). It is stored beside the radiologist's report, never
/// inside it, and is never verified automatically. An urgent finding only moves the study up the reporting worklist.
/// A radiologist acknowledges or dismisses it, and that review is audited.
/// </summary>
public sealed class ImagingAiFinding : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid ImagingStudyId { get; set; }
    public string Engine { get; set; } = string.Empty;
    public string? EngineVersion { get; set; }
    public string Finding { get; set; } = string.Empty;

    /// <summary>Engine confidence between 0 and 1, when provided.</summary>
    public decimal? Confidence { get; set; }

    public bool IsUrgent { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public AiFindingStatus Status { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }

    public Result Review(bool acknowledge, string? note, Guid? by, DateTimeOffset now)
    {
        if (Status != AiFindingStatus.Preliminary)
        {
            return Error.Rule("ai.reviewed", "This AI finding was already reviewed.");
        }

        Status = acknowledge ? AiFindingStatus.Acknowledged : AiFindingStatus.Dismissed;
        ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        ReviewedBy = by;
        ReviewedAt = now;
        return Result.Success();
    }
}
