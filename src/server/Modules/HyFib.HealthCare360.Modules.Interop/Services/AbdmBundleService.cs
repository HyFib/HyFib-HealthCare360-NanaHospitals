using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>An exported document and the name to save it under.</summary>
internal sealed record AbdmExport(R4.Bundle Bundle, string FileName);

/// <summary>
/// Exports one record as an ABDM health record (SRS EHR-08): a FHIR document of the NRCeS implementation guide for the
/// record's type. The record must be complete (signed or released) and in the caller's facility; a restricted (VIP)
/// record needs <c>patients.restricted.read</c> or a break-the-glass grant (EHR-07), as when it is opened on screen,
/// and that is checked before anything about the record is told (that it is a draft, or that its patient was deleted);
/// a refusal is audited as a security event. A record still kept under a duplicate UHID that was merged (a lab or
/// radiology report: the Diagnostics module keeps its records where they are) is exported as the surviving patient's,
/// and a discharge summary holds the stay's lab reports kept under such a duplicate, once every patient on the way may be
/// opened; a record of a deleted patient is not exported. A prescription the hospital's DSC rule does not let it issue
/// is not exported either: it is a copy of the prescription like the printout (<see cref="PrescriptionFinalCopies"/>).
/// Every export is audited as an access to the patient's record.
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
    IAuditLogger audit)
{
    public const string Exported = "patient.accessed";
    public const string Refused = "abdm.export_denied";
    public const string RestrictedDuplicateCode = "abdm.restricted_duplicate";

    /// <summary>More merges in a row than any real chain of duplicates has: a loop in the data, which is not followed.</summary>
    private const int MaxMerges = 16;

    /// <summary>
    /// The refusal when a duplicate whose records the document would hold is restricted: it does not tell which record
    /// that is, nor offer to break the glass for the patient on screen, which would not open it.
    /// </summary>
    private static readonly Error RestrictedDuplicate = Error.Forbidden(
        RestrictedDuplicateCode,
        "Part of this record is kept under a restricted record that was merged into this patient. Someone who may open restricted records can export it.");

    public async Task<Result<AbdmExport>> ExportAsync(AbdmRecordType type, Guid id, CancellationToken ct)
    {
        var located = await records.LocateAsync(type, id, ct);
        if (located.IsFailure)
        {
            return located.Error!;
        }

        // Whose record it is, through the merges of duplicate UHIDs to the surviving patient. Each of them may be
        // restricted, and is checked first; a patient deleted on the way means the record is not exported, with the
        // answer for a record that does not exist.
        var source = located.Value;
        var chain = await ChainAsync(source.PatientId, ct);
        if (await DeniedAsync(type, id, chain.Patients, ct) is { } denied)
        {
            return denied.Type == ErrorType.Forbidden ? denied : AbdmRecordLoader.NotFound(type, id);
        }

        if (!chain.Complete)
        {
            return AbdmRecordLoader.NotFound(type, id);
        }

        // Records of the same patient kept under other UHIDs merged into them; a record of anyone else is left out.
        var survivor = chain.Patients[^1];
        var duplicates = chain.Patients.SkipLast(1).ToList();
        foreach (var other in await records.OtherPatientsAsync(type, id, ct))
        {
            if (chain.Patients.Contains(other))
            {
                continue;
            }

            var theirs = await ChainAsync(other, ct);
            if (!theirs.Complete || theirs.Patients[^1] != survivor)
            {
                continue;
            }

            var notChecked = theirs.Patients.SkipLast(1).Where(p => !duplicates.Contains(p)).ToList();
            if (await DeniedAsync(type, id, notChecked, ct) is { } deniedDuplicate)
            {
                if (deniedDuplicate.Type == ErrorType.Forbidden)
                {
                    return RestrictedDuplicate;
                }

                // Deleted since the merge was followed: its records are left out.
                continue;
            }

            duplicates.AddRange(notChecked);
        }

        if (source.NotExportable is { } reason)
        {
            return Error.Rule("abdm.not_exportable", reason);
        }

        if (type == AbdmRecordType.Prescription && await records.PrescriptionNotIssuedAsync(id, ct) is { } notIssued)
        {
            return notIssued;
        }

        source = source with { PatientId = survivor, Duplicates = duplicates };
        var document = await records.LoadAsync(type, id, source, ct);
        var bundle = AbdmDocumentBuilder.Build(document);
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, Exported, $"ABDM health record exported: {Describe(type)}")
        {
            PatientId = source.PatientId,
            EntityType = $"abdm.{type}",
            EntityId = id.ToString("D"),
            Details = new { api = "abdm", record = type.ToString(), document = bundle.Id, version = bundle.Meta?.VersionId, entries = bundle.Entry.Count },
        }, ct);
        return new AbdmExport(bundle, document.FileName);
    }

    public static string Describe(AbdmRecordType type) => type switch
    {
        AbdmRecordType.DischargeSummary => "discharge summary",
        AbdmRecordType.LabReport => "lab report",
        AbdmRecordType.RadiologyReport => "radiology report",
        AbdmRecordType.OpConsultation => "consultation note",
        _ => "prescription",
    };

    /// <summary>
    /// The patient and those they were merged into, to the surviving patient (last); not complete when a patient on the
    /// way was deleted or the merges loop, and then only the patients before that one.
    /// </summary>
    private async Task<MergeChain> ChainAsync(Guid patientId, CancellationToken ct)
    {
        var patients = new List<Guid>();
        for (Guid? next = patientId; next is { } current;)
        {
            var patient = await db.Set<Patient>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(p => p.Id == current)
                .Select(p => new { p.IsDeleted, p.MergedIntoPatientId })
                .FirstOrDefaultAsync(ct);
            if (patient is null || patient.IsDeleted || patients.Contains(current) || patients.Count == MaxMerges)
            {
                return new MergeChain(patients, Complete: false);
            }

            patients.Add(current);
            next = patient.MergedIntoPatientId;
        }

        return new MergeChain(patients, Complete: true);
    }

    /// <summary>
    /// Why the caller may not open the first of <paramref name="patients"/> they may not: a restricted record without
    /// the permission or a break-the-glass grant, audited as a security event under that patient, or a patient deleted
    /// since it was read; null when they may open all.
    /// </summary>
    private async Task<Error?> DeniedAsync(AbdmRecordType type, Guid id, IEnumerable<Guid> patients, CancellationToken ct)
    {
        foreach (var patientId in patients)
        {
            if ((await guard.EnsureCanOpenAsync(patientId, ct)).Error is not { } error)
            {
                continue;
            }

            if (error.Type == ErrorType.Forbidden)
            {
                await audit.WriteAsync(new AuditEntry(AuditCategories.Security, Refused, $"ABDM health record export refused: {Describe(type)}")
                {
                    PatientId = patientId,
                    EntityType = $"abdm.{type}",
                    EntityId = id.ToString("D"),
                    Severity = AuditSeverity.Warning,
                    Details = new { api = "abdm", record = type.ToString(), reason = error.Code },
                }, ct);
            }

            return error;
        }

        return null;
    }

    private sealed record MergeChain(List<Guid> Patients, bool Complete);
}
