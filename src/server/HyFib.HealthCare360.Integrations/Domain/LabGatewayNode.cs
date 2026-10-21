namespace HyFib.HealthCare360.Integrations.Domain;

/// <summary>
/// One server's live analyser status, shared with the other servers on the database (SCAL-01):
/// <c>integrations.lab_gateway_nodes</c>, written by <see cref="Laboratory.LabStatusBoard"/>. A server writes its row while
/// it runs and removes it when it stops; a row past <see cref="ExpiresAt"/> belongs to a server that is gone.
/// </summary>
[NotAudited]
public sealed class LabGatewayNode
{
    /// <summary>The server's id for as long as it runs.</summary>
    public string Node { get; set; } = string.Empty;

    /// <summary>Its analysers' status, as JSON: a list of <see cref="Laboratory.LabListenerStatus"/>.</summary>
    public string Statuses { get; set; } = "[]";

    /// <summary>The lab agents relaying an analyser through it, as a JSON list of their device ids.</summary>
    public string RelayingAgents { get; set; } = "[]";

    public DateTimeOffset ExpiresAt { get; set; }
}
