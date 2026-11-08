namespace HyFib.HealthCare360.Modules.Quality.Domain.Audits;

/// <summary>The ten chapters of the NABH hospital accreditation standards (6th edition).</summary>
public enum NabhChapter
{
    /// <summary>Access, Assessment and Continuity of Care.</summary>
    Aac = 0,

    /// <summary>Care of Patients.</summary>
    Cop,

    /// <summary>Management of Medication.</summary>
    Mom,

    /// <summary>Patient Rights and Education.</summary>
    Pre,

    /// <summary>Hospital Infection Control.</summary>
    Hic,

    /// <summary>Patient Safety and Quality Improvement.</summary>
    Psq,

    /// <summary>Responsibilities of Management.</summary>
    Rom,

    /// <summary>Facility Management and Safety.</summary>
    Fms,

    /// <summary>Human Resource Management.</summary>
    Hrm,

    /// <summary>Information Management System.</summary>
    Ims,
}

/// <summary>How often an audit repeats: completing one schedules the next.</summary>
public enum AuditRecurrence
{
    Once = 0,
    Monthly,
    Quarterly,
    HalfYearly,
    Yearly,
}

public enum AuditStatus
{
    Scheduled = 0,
    InProgress,

    /// <summary>Every item answered and every non-compliance given a CAPA; waiting for the CAPAs to be verified.</summary>
    Completed,

    /// <summary>Every CAPA verified effective (by someone other than its owner) or cancelled.</summary>
    Closed,
    Cancelled,
}

public enum FindingResult
{
    Compliant = 0,
    NonCompliant,
    NotApplicable,
}

/// <summary>One question of an audit checklist.</summary>
/// <param name="Reference">Where in the standards it comes from, as the hospital's self-assessment cites it.</param>
public sealed record AuditItem(string Key, string Text, string? Reference);

/// <summary>An audit checklist for a NABH chapter: the hospital's own, or one of the starting points HC360 seeds.</summary>
public sealed class AuditTemplate : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public NabhChapter Chapter { get; set; }
    public string? Description { get; set; }
    public List<AuditItem> Items { get; set; } = [];
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// An internal audit against a checklist (NABH PSQ: internal audits and their corrective actions): scheduled for a date and a
/// lead auditor, its findings recorded item by item, each non-compliance given a CAPA with an owner and a due date, and
/// closed only when every CAPA is verified effective by someone other than its owner (or cancelled).
/// </summary>
public sealed class QualityAudit : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>"{FAC}AUD{YY}{SEQ:4}".</summary>
    public string AuditNumber { get; set; } = string.Empty;

    public Guid TemplateId { get; set; }
    public string Title { get; set; } = string.Empty;
    public NabhChapter Chapter { get; set; }
    public Guid? DepartmentId { get; set; }

    /// <summary>The ward, unit or area audited.</summary>
    public string? Area { get; set; }

    public DateOnly ScheduledOn { get; set; }
    public Guid LeadAuditorUserId { get; set; }
    public AuditRecurrence Recurrence { get; set; }
    public AuditStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public string? Summary { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>The next audit scheduled when this one was completed.</summary>
    public Guid? NextAuditId { get; set; }

    public List<AuditFinding> Findings { get; set; } = [];

    public Result Start(DateTimeOffset now)
    {
        if (Status != AuditStatus.Scheduled)
        {
            return Error.Rule("audit.not_scheduled", "Only a scheduled audit can be started.");
        }

        Status = AuditStatus.InProgress;
        StartedAt = now;
        return Result.Success();
    }

    public Result Record(string itemKey, FindingResult result, string? observation)
    {
        if (Status is not (AuditStatus.Scheduled or AuditStatus.InProgress))
        {
            return Error.Rule("audit.not_open", "The findings of a completed audit cannot change.");
        }

        var finding = Findings.FirstOrDefault(f => f.ItemKey == itemKey);
        if (finding is null)
        {
            return Error.Field("itemKey", "Not an item of this audit's checklist.");
        }

        if (result == FindingResult.NonCompliant && string.IsNullOrWhiteSpace(observation))
        {
            return Error.Field("observation", "Say what was found for a non-compliance.");
        }

        if (finding.CapaId is not null && result != FindingResult.NonCompliant)
        {
            return Error.Rule("audit.finding_has_capa", "This finding has a CAPA: cancel the CAPA first to change it.");
        }

        finding.Result = result;
        finding.Observation = string.IsNullOrWhiteSpace(observation) ? null : observation.Trim();
        return Result.Success();
    }

    /// <summary>What still stops completion: items unanswered, non-compliances with no CAPA.</summary>
    public IReadOnlyList<string> CompletionGaps()
    {
        var gaps = new List<string>();
        var unanswered = Findings.Count(f => f.Result is null);
        if (unanswered > 0)
        {
            gaps.Add($"{unanswered} item{(unanswered == 1 ? " is" : "s are")} not answered");
        }

        var noCapa = Findings.Count(f => f.Result == FindingResult.NonCompliant && f.CapaId is null);
        if (noCapa > 0)
        {
            gaps.Add($"{noCapa} non-compliance{(noCapa == 1 ? " has" : "s have")} no CAPA");
        }

        return gaps;
    }

    public Result Complete(string? summary, DateTimeOffset now)
    {
        if (Status is not (AuditStatus.Scheduled or AuditStatus.InProgress))
        {
            return Error.Rule("audit.not_open", "Only an audit in progress can be completed.");
        }

        var gaps = CompletionGaps();
        if (gaps.Count > 0)
        {
            return Error.Rule("audit.incomplete", "The audit cannot be completed yet: " + string.Join("; ", gaps) + ".");
        }

        Status = AuditStatus.Completed;
        StartedAt ??= now;
        CompletedAt = now;
        Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
        return Result.Success();
    }

    /// <summary>Closes the audit once every CAPA it raised is verified (by someone other than its owner) or cancelled.</summary>
    public Result Close(IReadOnlyCollection<CapaStatus> capaStatuses, Guid userId, DateTimeOffset now)
    {
        if (Status != AuditStatus.Completed)
        {
            return Error.Rule("audit.not_completed", "Only a completed audit can be closed.");
        }

        var open = capaStatuses.Count(s => s is not (CapaStatus.Verified or CapaStatus.Cancelled));
        if (open > 0)
        {
            return Error.Rule("audit.capas_open",
                $"{open} CAPA{(open == 1 ? " is" : "s are")} not yet verified effective. Each is verified by someone other than its owner before the audit closes.");
        }

        Status = AuditStatus.Closed;
        ClosedAt = now;
        ClosedByUserId = userId;
        return Result.Success();
    }

    public Result Cancel(string reason)
    {
        if (Status is not (AuditStatus.Scheduled or AuditStatus.InProgress))
        {
            return Error.Rule("audit.not_open", "Only an audit not yet completed can be cancelled.");
        }

        if (Findings.Any(f => f.CapaId is not null))
        {
            return Error.Rule("audit.has_capas", "This audit has raised CAPAs: complete it instead.");
        }

        Status = AuditStatus.Cancelled;
        CancelReason = reason.Trim();
        return Result.Success();
    }

    /// <summary>Compliance: compliant items ÷ items that apply, as a percentage to one decimal; null while none is answered.</summary>
    public decimal? CompliancePercent()
    {
        var applicable = Findings.Count(f => f.Result is FindingResult.Compliant or FindingResult.NonCompliant);
        return applicable == 0 ? null : Math.Round(Findings.Count(f => f.Result == FindingResult.Compliant) * 100m / applicable, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>The date the next audit of a recurring schedule falls on; none for a one-off.</summary>
    public static DateOnly? Next(DateOnly scheduledOn, AuditRecurrence recurrence) => recurrence switch
    {
        AuditRecurrence.Monthly => scheduledOn.AddMonths(1),
        AuditRecurrence.Quarterly => scheduledOn.AddMonths(3),
        AuditRecurrence.HalfYearly => scheduledOn.AddMonths(6),
        AuditRecurrence.Yearly => scheduledOn.AddYears(1),
        _ => null,
    };
}

/// <summary>An audit's answer to one checklist item, copied from the template when the audit was scheduled.</summary>
public sealed class AuditFinding : Entity
{
    public Guid AuditId { get; set; }
    public int Order { get; set; }
    public string ItemKey { get; set; } = string.Empty;
    public string ItemText { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public FindingResult? Result { get; set; }
    public string? Observation { get; set; }

    /// <summary>The CAPA raised for a non-compliance (in the CAPA register, source Audit).</summary>
    public Guid? CapaId { get; set; }
}
