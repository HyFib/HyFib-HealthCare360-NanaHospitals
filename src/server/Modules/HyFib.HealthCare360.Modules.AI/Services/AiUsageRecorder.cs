using HyFib.HealthCare360.Modules.AI.Domain;

namespace HyFib.HealthCare360.Modules.AI.Services;

/// <summary>
/// Records an AI call: an <see cref="AiUsage"/> row and a clinical audit entry <c>ai.&lt;purpose&gt;</c> (SEC-12,
/// ADR-011 "every call is audited") in one transaction. Neither contains the transcript, prompt or answer. Recording
/// ignores the request's cancellation: a call the client abandoned was still made and must still be accounted for.
/// </summary>
internal sealed class AiUsageRecorder(HospitalDbContext db, IAuditLogger audit, ICurrentUser user, TimeProvider time) : IAiUsageRecorder
{
    public async Task RecordAsync(AiUsageRecord usage, CancellationToken cancellationToken)
    {
        var row = new AiUsage
        {
            CreatedAt = time.GetUtcNow(),
            UserId = user.UserId,
            FacilityId = user.FacilityId,
            PatientId = usage.PatientId,
            Purpose = usage.Purpose,
            Provider = usage.Provider,
            Model = usage.Model,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            LatencyMs = (int)Math.Clamp(usage.Latency.TotalMilliseconds, 0, int.MaxValue),
            Success = usage.Success,
            Error = usage.Error is { Length: > 500 } error ? error[..500] : usage.Error,
        };
        db.Set<AiUsage>().Add(row);
        audit.Add(new AuditEntry(AuditCategories.Clinical, "ai." + usage.Purpose, $"AI {usage.Purpose} via {usage.Provider} ({(usage.Success ? "completed" : "failed")})")
        {
            EntityType = nameof(AiUsage),
            EntityId = row.Id.ToString(),
            PatientId = usage.PatientId,
            Details = new { usage.Provider, usage.Model, usage.InputTokens, usage.OutputTokens, row.LatencyMs, usage.Success },
            Severity = usage.Success ? AuditSeverity.Info : AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
