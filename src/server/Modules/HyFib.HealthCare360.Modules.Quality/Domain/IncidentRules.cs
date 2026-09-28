namespace HyFib.HealthCare360.Modules.Quality.Domain;

/// <summary>Facts about an incident's investigation and CAPAs that the workflow needs.</summary>
public sealed record IncidentProgress(bool RcaCompleted, int CapaCount, int OpenCapaCount)
{
    public static IncidentProgress None { get; } = new(false, 0, 0);
}

/// <summary>
/// The NABH incident review path (IRMS-03/04):
/// Reported → UnderReview → Investigation (RCA) → CapaInProgress → Closed, with short cuts where appropriate
/// (a duplicate report closes straight away; a minor event can go from review to CAPA or closure) and reopening.
/// Sentinel events can only be closed after a completed root-cause analysis, and nothing moves to CAPA without a CAPA.
/// </summary>
public static class IncidentWorkflow
{
    private static readonly Dictionary<IncidentStatus, IncidentStatus[]> Allowed = new()
    {
        [IncidentStatus.Reported] = [IncidentStatus.UnderReview, IncidentStatus.Closed],
        [IncidentStatus.UnderReview] = [IncidentStatus.Investigation, IncidentStatus.CapaInProgress, IncidentStatus.Closed],
        [IncidentStatus.Investigation] = [IncidentStatus.CapaInProgress, IncidentStatus.Closed],
        [IncidentStatus.CapaInProgress] = [IncidentStatus.Investigation, IncidentStatus.Closed],
        [IncidentStatus.Closed] = [IncidentStatus.UnderReview],
    };

    public static IReadOnlyList<IncidentStatus> NextStates(IncidentStatus current) => Allowed[current];

    public static Result CanMove(IncidentStatus from, IncidentStatus to, bool isSentinel, IncidentProgress progress, string? note)
    {
        if (!Allowed[from].Contains(to))
        {
            return Error.Rule("incident.invalid_transition", $"An incident cannot move from {from} to {to}.");
        }

        if (to == IncidentStatus.Closed && string.IsNullOrWhiteSpace(note))
        {
            return Error.Field("note", "Summarise the outcome before closing the incident.");
        }

        if (from == IncidentStatus.Closed && string.IsNullOrWhiteSpace(note))
        {
            return Error.Field("note", "Give the reason for reopening the incident.");
        }

        if (from == IncidentStatus.Investigation && to is IncidentStatus.CapaInProgress or IncidentStatus.Closed && !progress.RcaCompleted)
        {
            return Error.Rule("incident.rca_incomplete", "Complete the root-cause analysis first.");
        }

        // Closing straight from "Reported" is for duplicate/invalid reports, which need no RCA of their own.
        if (to == IncidentStatus.Closed && isSentinel && !progress.RcaCompleted && from != IncidentStatus.Reported)
        {
            return Error.Rule("incident.sentinel_needs_rca", "A sentinel event can be closed only after a completed root-cause analysis.");
        }

        if (to == IncidentStatus.CapaInProgress && progress.CapaCount == 0)
        {
            return Error.Rule("incident.no_capa", "Add at least one corrective or preventive action first.");
        }

        if (from == IncidentStatus.CapaInProgress && to == IncidentStatus.Closed && progress.OpenCapaCount > 0)
        {
            return Error.Rule("incident.capa_open", $"{progress.OpenCapaCount} corrective/preventive action(s) are still open.");
        }

        return Result.Success();
    }
}

/// <summary>Which incidents are sentinel events (immediate escalation and mandatory RCA).</summary>
public static class SentinelRules
{
    public static bool IsSentinel(IncidentSeverity severity, SentinelEventKind? kind, bool flagged) =>
        flagged || kind is not null || severity is IncidentSeverity.Severe or IncidentSeverity.Death;

    public static string? Reason(IncidentSeverity severity, SentinelEventKind? kind, bool flagged) =>
        severity == IncidentSeverity.Death ? "Death"
        : kind is { } never ? $"Never event: {never}"
        : severity == IncidentSeverity.Severe ? "Severe harm"
        : flagged ? "Flagged as sentinel"
        : null;
}
