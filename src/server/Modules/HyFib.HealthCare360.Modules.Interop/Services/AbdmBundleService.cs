using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// Exports one record as an ABDM health record (SRS EHR-08): a FHIR document of the NRCeS implementation guide for the
/// record's type. The record must be complete (signed or released) and in the caller's facility; a restricted (VIP)
/// record needs <c>patients.restricted.read</c> or a break-the-glass grant (EHR-07), as when it is opened on screen; a
/// record of a merged or deleted patient is not exported. Every export is audited as an access to the patient's record.
/// <para>
/// The export is a staff download, like the printed document: it does not need the patient's record-sharing consent
/// of the FHIR read API. Sending a document to ABDM is a separate step that ABDM itself authorises with the patient's
/// consent artefact, through the gateway (<see cref="IAbdmGatewayClient"/>), which is not live yet.
/// </para>
/// </summary>
internal sealed class AbdmBundleService(
    AbdmRecordLoader records,
    PatientAccessGuard guard,
    HospitalDbContext db,
    IAuditLogger audit,
    TimeProvider time)
{
    public const string Exported = "patient.accessed";

    public async Task<Result<R4.Bundle>> ExportAsync(AbdmRecordType type, Guid id, CancellationToken ct)
    {
        var located = await records.LocateAsync(type, id, ct);
        if (located.IsFailure)
        {
            return located.Error!;
        }

        var source = located.Value;
        var patient = await db.Set<Patient>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(p => p.Id == source.PatientId)
            .Select(p => new { p.IsDeleted, p.MergedIntoPatientId })
            .FirstOrDefaultAsync(ct);
        if (patient is null || patient.IsDeleted)
        {
            return Error.NotFound(nameof(Patient), source.PatientId);
        }

        if (patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("abdm.patient_merged", "This patient's record was merged into another UHID. Export the record from the surviving patient.");
        }

        var access = await guard.EnsureCanOpenAsync(source.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        var document = await records.LoadAsync(type, id, source, ct);
        var bundle = AbdmDocumentBuilder.Build(document, time.GetUtcNow());
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, Exported, $"ABDM health record exported: {Describe(type)}")
        {
            PatientId = source.PatientId,
            EntityType = $"abdm.{type}",
            EntityId = id.ToString("D"),
            Details = new { api = "abdm", record = type.ToString(), document = bundle.Id, version = document.VersionId, entries = bundle.Entry.Count },
        }, ct);
        return bundle;
    }

    public static string Describe(AbdmRecordType type) => type switch
    {
        AbdmRecordType.DischargeSummary => "discharge summary",
        AbdmRecordType.LabReport => "lab report",
        AbdmRecordType.OpConsultation => "consultation note",
        _ => "prescription",
    };
}
