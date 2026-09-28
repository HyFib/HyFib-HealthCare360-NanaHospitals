namespace HyFib.HealthCare360.Modules.AI.Domain;

/// <summary>
/// One call to an AI provider (SEC-12, ADR-011): who used which model for what, token counts and latency — never the
/// transcript, the prompt or the answer. Append-only and high volume, so it is excluded from data-change auditing; the
/// clinical audit entry written in the same transaction is the tamper-evident record. Also the basis of per-user quotas.
/// </summary>
[NotAudited]
public sealed class AiUsage : Entity
{
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? UserId { get; set; }
    public Guid? FacilityId { get; set; }

    /// <summary>Patient in context, when there was one (links the usage to the patient's audit history).</summary>
    public Guid? PatientId { get; set; }

    /// <summary><c>assistant.command</c>, <c>clinical.structure-note</c> or <c>voice.transcribe</c>.</summary>
    public string Purpose { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;
    public string? Model { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int LatencyMs { get; set; }
    public bool Success { get; set; }

    /// <summary>Provider/status error text only; never request content.</summary>
    public string? Error { get; set; }
}
