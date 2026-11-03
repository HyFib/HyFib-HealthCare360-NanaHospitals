using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// PHI-access audit of the FHIR API (SEC-04) through the platform <see cref="IAuditLogger"/>. Reads and searches are
/// recorded like every other patient-record access (category <c>phi</c>, action <c>patient.accessed</c>, the patient's
/// id), so they appear in the patient's access history; refused reads and searches of non-consenting or restricted
/// records are security events. Entries name the resource type, the interaction, the purpose of use the reader stated
/// (<c>X-Purpose-Of-Use</c>, or "not stated") and the search parameter names, never parameter values (they can be
/// identifiers or names).
/// </summary>
internal sealed class FhirAuditTrail(IAuditLogger audit, HospitalDbContext db)
{
    public const string PatientAccessed = "patient.accessed";
    public const string AccessDenied = "fhir.access_denied";
    public const string SearchWithoutMatch = "fhir.search";

    public Task ReadAsync(string resourceType, string id, Guid patientId, SharingReader reader, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, PatientAccessed, $"FHIR R4 read {resourceType}")
        {
            PatientId = patientId,
            EntityType = EntityType(resourceType),
            EntityId = id,
            Details = new { api = "fhir-r4", interaction = "read", resourceType, purpose = PurposeOf(reader) },
        }, ct);

    /// <param name="withheld">Records of the patient left out because the consent does not cover them for this reader.</param>
    public Task SearchAsync(
        string resourceType, Guid patientId, int returned, int total, int withheld, IReadOnlyList<string> parameters, SharingReader reader, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, PatientAccessed, $"FHIR R4 search {resourceType}: {returned} of {total} returned")
        {
            PatientId = patientId,
            EntityType = EntityType(resourceType),
            Details = new { api = "fhir-r4", interaction = "search", resourceType, returned, total, withheld, parameters, purpose = PurposeOf(reader) },
        }, ct);

    /// <summary>One entry per patient returned by a Patient search (one transaction); one entry when nothing matched.</summary>
    public async Task PatientSearchAsync(IReadOnlyCollection<Guid> patientIds, int total, IReadOnlyList<string> parameters, SharingReader reader, CancellationToken ct)
    {
        var purpose = PurposeOf(reader);
        if (patientIds.Count == 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditCategories.Integration, SearchWithoutMatch, $"FHIR R4 search Patient: no patient returned ({total} matched)")
            {
                EntityType = EntityType("Patient"),
                Details = new { api = "fhir-r4", interaction = "search", resourceType = "Patient", returned = 0, total, parameters, purpose },
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
                Details = new { api = "fhir-r4", interaction = "search", resourceType = "Patient", returned = patientIds.Count, total, parameters, purpose },
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>A read or search refused because of the consent or restricted-record rules.</summary>
    public Task DeniedAsync(string interaction, string resourceType, string? id, Guid patientId, string reason, SharingReader reader, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.Security, AccessDenied, $"FHIR R4 {interaction} {resourceType} refused: {reason}")
        {
            PatientId = patientId,
            EntityType = EntityType(resourceType),
            EntityId = id,
            Severity = AuditSeverity.Warning,
            Details = new { api = "fhir-r4", interaction, resourceType, reason, purpose = PurposeOf(reader) },
        }, ct);

    private static string EntityType(string resourceType) => $"fhir.{resourceType}";

    /// <summary>The purpose of use the reader stated, as its v3 code, or "not stated".</summary>
    private static string PurposeOf(SharingReader reader) => reader.Purpose is { } purpose ? SharingPurposeCodes.CodeOf(purpose) : "not stated";
}
