namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>A record exported as an ABDM health record: its entity type and id, and what staff call it.</summary>
internal sealed record AbdmRecord(string EntityType, Guid Id, string Name)
{
    public static AbdmRecord DischargeSummary(Guid id) => new("DischargeSummary", id, "discharge summary");
}

/// <summary>
/// Audit of ABDM health record exports (PRV-04: an export or transfer of records is itself audited; SEC-04). An export is
/// PHI access in the patient's record (category <c>phi</c>, the patient's id), so it appears in the patient's access
/// history, under its own action. An export refused because the patient has not consented to sharing, or because the
/// record is restricted, is a security event, as for the FHIR API. Entries name the record, the profile and the
/// document's identifier, never its content.
/// </summary>
internal sealed class AbdmAuditTrail(IAuditLogger audit)
{
    public const string Exported = "abdm.record.exported";
    public const string Refused = "abdm.export_refused";

    public Task ExportedAsync(AbdmRecord record, Guid patientId, string profile, string document, int resources, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, Exported, $"Exported the {record.Name} as an ABDM health record")
        {
            PatientId = patientId,
            EntityType = record.EntityType,
            EntityId = record.Id.ToString("D"),
            Details = new { format = "fhir-r4-document", profile, document, resources },
        }, ct);

    public Task RefusedAsync(AbdmRecord record, Guid patientId, string reason, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.Security, Refused, $"ABDM export of the {record.Name} refused: {reason}")
        {
            PatientId = patientId,
            EntityType = record.EntityType,
            EntityId = record.Id.ToString("D"),
            Severity = AuditSeverity.Warning,
            Details = new { reason },
        }, ct);
}
