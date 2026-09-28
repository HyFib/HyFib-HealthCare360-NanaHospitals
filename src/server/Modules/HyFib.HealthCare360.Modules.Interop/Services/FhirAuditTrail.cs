namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// PHI-access audit of the FHIR API (SEC-04) through the platform <see cref="IAuditLogger"/>. Reads and searches are
/// recorded like every other patient-record access (category <c>phi</c>, action <c>patient.accessed</c>, the patient's
/// id), so they appear in the patient's access history; refused reads and searches of non-consenting or restricted
/// records are security events. Entries name the resource type, the interaction and the search parameter names, never
/// parameter values (they can be identifiers or names).
/// </summary>
internal sealed class FhirAuditTrail(IAuditLogger audit, HospitalDbContext db)
{
    public const string PatientAccessed = "patient.accessed";
    public const string AccessDenied = "fhir.access_denied";
    public const string SearchWithoutMatch = "fhir.search";

    public Task ReadAsync(string resourceType, string id, Guid patientId, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, PatientAccessed, $"FHIR R4 read {resourceType}")
        {
            PatientId = patientId,
            EntityType = EntityType(resourceType),
            EntityId = id,
            Details = new { api = "fhir-r4", interaction = "read", resourceType },
        }, ct);

    public Task SearchAsync(string resourceType, Guid patientId, int returned, int total, IReadOnlyList<string> parameters, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, PatientAccessed, $"FHIR R4 search {resourceType}: {returned} of {total} returned")
        {
            PatientId = patientId,
            EntityType = EntityType(resourceType),
            Details = new { api = "fhir-r4", interaction = "search", resourceType, returned, total, parameters },
        }, ct);

    /// <summary>One entry per patient returned by a Patient search (one transaction); one entry when nothing matched.</summary>
    public async Task PatientSearchAsync(IReadOnlyCollection<Guid> patientIds, int total, IReadOnlyList<string> parameters, CancellationToken ct)
    {
        if (patientIds.Count == 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditCategories.Integration, SearchWithoutMatch, $"FHIR R4 search Patient: no patient returned ({total} matched)")
            {
                EntityType = EntityType("Patient"),
                Details = new { api = "fhir-r4", interaction = "search", resourceType = "Patient", returned = 0, total, parameters },
            }, ct);
            return;
        }

        foreach (var patientId in patientIds)
        {
            audit.Add(new AuditEntry(AuditCategories.PhiAccess, PatientAccessed, "FHIR R4 search Patient")
            {
                PatientId = patientId,
                EntityType = EntityType("Patient"),
                EntityId = patientId.ToString("D"),
                Details = new { api = "fhir-r4", interaction = "search", resourceType = "Patient", returned = patientIds.Count, total, parameters },
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>A read or search refused because of the consent or restricted-record rules.</summary>
    public Task DeniedAsync(string interaction, string resourceType, string? id, Guid patientId, string reason, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.Security, AccessDenied, $"FHIR R4 {interaction} {resourceType} refused: {reason}")
        {
            PatientId = patientId,
            EntityType = EntityType(resourceType),
            EntityId = id,
            Severity = AuditSeverity.Warning,
            Details = new { api = "fhir-r4", interaction, resourceType, reason },
        }, ct);

    private static string EntityType(string resourceType) => $"fhir.{resourceType}";
}
