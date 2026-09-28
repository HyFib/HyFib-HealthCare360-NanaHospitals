using HyFib.HealthCare360.Modules.AI.Services.Privacy;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Services;

/// <summary>Whether the AI text provider may be called for the current facility, and which model it would use.</summary>
internal sealed record AiAvailability(bool Enabled, string Provider, string? Model, string? Reason);

/// <summary>
/// Async-first view of the configured AI text provider for this module. The platform's <see cref="IAiTextService"/>
/// exposes a synchronous <c>IsEnabled</c>, which cannot read settings without blocking.
/// </summary>
internal interface IAiGateway
{
    Task<AiAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);

    Task<AiCompletion> CompleteAsync(AiRequest request, CancellationToken cancellationToken);
}

/// <summary>What is recorded for one AI call — never the text itself.</summary>
internal sealed record AiUsageRecord(
    string Purpose,
    string Provider,
    string? Model,
    int InputTokens,
    int OutputTokens,
    TimeSpan Latency,
    bool Success,
    string? Error,
    Guid? PatientId);

/// <summary>Writes the usage row and the clinical audit entry (<c>ai.&lt;purpose&gt;</c>) for an AI call.</summary>
internal interface IAiUsageRecorder
{
    Task RecordAsync(AiUsageRecord usage, CancellationToken cancellationToken);
}

/// <summary>Per-user AI quotas.</summary>
internal interface IAiQuota
{
    /// <summary>Null when the current user may make another AI call; otherwise the staff-facing reason.</summary>
    Task<string?> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>The few patient facts the AI module needs: UHID look-up and the identifiers to redact.</summary>
internal interface IPatientDirectory
{
    Task<Guid?> FindByUhidAsync(string uhid, CancellationToken cancellationToken);

    Task<RedactionContext> RedactionContextAsync(Guid? patientId, CancellationToken cancellationToken);
}
