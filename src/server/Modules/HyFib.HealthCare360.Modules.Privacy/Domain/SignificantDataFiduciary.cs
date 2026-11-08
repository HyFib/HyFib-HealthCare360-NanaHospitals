namespace HyFib.HealthCare360.Modules.Privacy.Domain;

public enum DpiaStatus
{
    /// <summary>Being written, or revised; can be changed. Sent for approval, it waits for another person (the approvals inbox).</summary>
    Draft = 0,

    /// <summary>Approved; reviewed by <see cref="Dpia.ReviewDue"/>, when it is revised as a new draft.</summary>
    Approved,

    /// <summary>The processing has stopped.</summary>
    Retired,
}

public enum RiskLevel
{
    Low = 0,
    Medium,
    High,
}

/// <summary>One risk to the people whose data it is, and how likely and how harmful it is.</summary>
public sealed record DpiaRisk(string Description, RiskLevel Likelihood, RiskLevel Impact);

/// <summary>A measure that reduces a risk, and who is responsible for it.</summary>
public sealed record DpiaMitigation(string Description, string? Owner);

/// <summary>
/// A Data Protection Impact Assessment of one processing activity (DPDP Act 2023 s.10(2)(c)(i); DPDP Rules 2025, rule 13:
/// once every twelve months). What is processed, about whom, for what, the risks to them and the measures against those
/// risks, approved by someone other than whoever wrote it, and reviewed by a date.
/// </summary>
public sealed class Dpia : AuditableEntity
{
    public string Number { get; set; } = string.Empty;

    /// <summary>The processing activity, for example "Laboratory reports sent by WhatsApp".</summary>
    public string Activity { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>The personal data processed, for example "Name", "Mobile number", "Lab results".</summary>
    public List<string> DataCategories { get; set; } = [];

    /// <summary>Whose data: patients, children, staff, visitors.</summary>
    public string DataPrincipals { get; set; } = string.Empty;

    public string Purpose { get; set; } = string.Empty;

    public List<DpiaRisk> Risks { get; set; } = [];
    public List<DpiaMitigation> Mitigations { get; set; } = [];

    /// <summary>The date by which it is reviewed again (at most twelve months after approval, rule 13).</summary>
    public DateOnly ReviewDue { get; set; }

    public DpiaStatus Status { get; set; }
    public Guid? ApprovedBy { get; set; }
    public string? ApprovedByName { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>The last reminder sent for the review date: 30 or 7 days before, or 0 once overdue; null for none.</summary>
    public int? RemindedDaysBefore { get; set; }

    /// <summary>The highest risk left, the worse of likelihood and impact of each risk.</summary>
    public RiskLevel? HighestRisk => Risks.Count == 0 ? null : Risks.Max(r => (RiskLevel)Math.Max((int)r.Likelihood, (int)r.Impact));
}

/// <summary>
/// The yearly audit of the hospital's processing by an independent data auditor (DPDP Act 2023 s.10(2)(b); DPDP Rules
/// 2025, rule 13: once every twelve months, its significant observations reported to the Board).
/// </summary>
public sealed class DataAudit : AuditableEntity
{
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }

    /// <summary>The independent data auditor and their firm.</summary>
    public string Auditor { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;
    public string? Findings { get; set; }

    /// <summary>The significant observations, as reported to the Board.</summary>
    public string? SignificantObservations { get; set; }

    public DateOnly? CompletedOn { get; set; }
    public DateOnly? ReportedToBoardOn { get; set; }
    public string? ReportReference { get; set; }

    /// <summary>When the next audit is due: twelve months after this one's period ends, unless set earlier.</summary>
    public DateOnly NextDue { get; set; }

    public int? RemindedDaysBefore { get; set; }
}
