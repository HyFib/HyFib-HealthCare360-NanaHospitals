namespace HyFib.HealthCare360.Modules.Quality.Domain;

public enum CapaType
{
    Corrective = 0,
    Preventive,
}

public enum CapaStatus
{
    Open = 0,
    InProgress,

    /// <summary>Done by the owner; awaiting an independent effectiveness check.</summary>
    Completed,

    /// <summary>Checked and found effective.</summary>
    Verified,
    Cancelled,
}

public enum CapaSource
{
    Incident = 0,
    Risk,
    Audit,
    Complaint,
    IndicatorBreach,
    Other,
}

/// <summary>
/// Corrective or preventive action (IRMS-04). The owner carries it out; someone else then checks that it actually
/// worked — an ineffective action goes back to the owner rather than being closed on paper.
/// </summary>
public sealed class Capa : AuditableEntity, IFacilityScoped
{
    /// <summary>Default wait before checking effectiveness, so the change has time to show results.</summary>
    public const int DefaultEffectivenessDelayDays = 30;

    public Guid FacilityId { get; set; }

    /// <summary>"{FAC}CAPA{YY}{SEQ:4}".</summary>
    public string CapaNumber { get; set; } = string.Empty;

    public CapaSource Source { get; set; }
    public Guid? IncidentId { get; set; }
    public Guid? RiskId { get; set; }
    public CapaType Type { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid OwnerUserId { get; set; }
    public DateOnly DueDate { get; set; }
    public CapaStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletionNote { get; set; }
    public DateOnly? EffectivenessCheckDue { get; set; }
    public bool? IsEffective { get; set; }
    public string? EffectivenessNote { get; set; }
    public Guid? VerifiedByUserId { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? CancelReason { get; set; }
    public int ReworkCount { get; set; }

    public bool IsOpen => Status is CapaStatus.Open or CapaStatus.InProgress;

    public bool IsOverdue(DateOnly today) => IsOpen && DueDate < today;

    public Result Start(DateTimeOffset now)
    {
        if (Status != CapaStatus.Open)
        {
            return Error.Rule("capa.not_open", "Only an open action can be started.");
        }

        Status = CapaStatus.InProgress;
        StartedAt = now;
        return Result.Success();
    }

    public Result Complete(string note, DateTimeOffset now, DateOnly today)
    {
        if (!IsOpen)
        {
            return Error.Rule("capa.not_open", "This action is not open.");
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            return Error.Field("note", "Describe what was done.");
        }

        Status = CapaStatus.Completed;
        StartedAt ??= now;
        CompletedAt = now;
        CompletionNote = note.Trim();
        EffectivenessCheckDue ??= today.AddDays(DefaultEffectivenessDelayDays);
        return Result.Success();
    }

    /// <summary>Effectiveness check by someone other than the owner; not effective sends the action back for rework.</summary>
    public Result Verify(bool effective, string note, Guid verifierUserId, DateTimeOffset now)
    {
        if (Status != CapaStatus.Completed)
        {
            return Error.Rule("capa.not_completed", "Only a completed action can be checked for effectiveness.");
        }

        if (verifierUserId == OwnerUserId)
        {
            return Error.Forbidden("capa.self_verification", "The effectiveness check must be done by someone other than the action owner.");
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            return Error.Field("note", "Record the evidence of effectiveness.");
        }

        IsEffective = effective;
        EffectivenessNote = note.Trim();
        VerifiedByUserId = verifierUserId;
        VerifiedAt = now;
        if (effective)
        {
            Status = CapaStatus.Verified;
        }
        else
        {
            Status = CapaStatus.InProgress;
            CompletedAt = null;
            EffectivenessCheckDue = null;
            ReworkCount++;
        }

        return Result.Success();
    }

    public Result Cancel(string reason)
    {
        if (!IsOpen)
        {
            return Error.Rule("capa.not_open", "Only open actions can be cancelled.");
        }

        Status = CapaStatus.Cancelled;
        CancelReason = reason.Trim();
        return Result.Success();
    }
}
