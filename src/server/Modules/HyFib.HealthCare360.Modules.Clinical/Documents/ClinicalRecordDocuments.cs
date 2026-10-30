using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Localization;

namespace HyFib.HealthCare360.Modules.Clinical.Documents;

/// <summary>
/// This module's documents for a copy of the record (MRD-04): signed prescriptions that the DSC rules let the hospital
/// issue, signed or amended discharge summaries, and the MLC intimations to the police. Each is printed as the hospital
/// prints it, in English.
/// </summary>
internal sealed class ClinicalRecordDocuments(
    HospitalDbContext db,
    PrescriptionPrintBuilder prescriptions,
    PrescriptionFinalCopies finalCopies,
    DischargeSummaryService summaries,
    DocumentAssets assets) : IRecordDocumentSource
{
    public const string Prescription = PrescriptionDocumentSource.DocumentKind;
    public const string DischargeSummary = DischargeSummaryDocumentSource.DocumentKind;
    public const string Mlc = "clinical.mlc";

    public IReadOnlyCollection<string> Kinds { get; } = [Prescription, DischargeSummary, Mlc];

    public async Task<IReadOnlyList<RecordDocumentEntry>> ListAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var signed = await IssuablePrescriptionsAsync(patientId, cancellationToken);
        var discharges = await db.Set<DischargeSummary>().AsNoTracking()
            .Where(s => s.PatientId == patientId && s.Status != DischargeSummaryStatus.Draft && s.SignedAt != null)
            .Select(s => new RecordDocumentEntry(DischargeSummary, s.Id, "Discharge summary", s.AmendedAt ?? s.SignedAt!.Value, null, s.AdmissionId))
            .ToListAsync(cancellationToken);
        var mlcs = await db.Set<MlcRecord>().AsNoTracking()
            .Where(m => m.PatientId == patientId)
            .Select(m => new RecordDocumentEntry(Mlc, m.Id, "MLC intimation " + m.Number, m.IdentifiedAt, m.VisitId, null))
            .ToListAsync(cancellationToken);
        return [.. signed, .. discharges, .. mlcs];
    }

    /// <summary>
    /// The signed prescriptions the DSC rules let the hospital issue, checked as <see cref="BuildAsync"/> checks them: where
    /// it requires the doctor's DSC, one without a valid DSC is not offered, as a copy could not hold it.
    /// </summary>
    private async Task<List<RecordDocumentEntry>> IssuablePrescriptionsAsync(Guid patientId, CancellationToken ct)
    {
        var signed = await db.Set<Prescription>().AsNoTracking().Include(p => p.Items)
            .Where(p => p.PatientId == patientId && p.Status == PrescriptionStatus.Signed)
            .ToListAsync(ct);
        var issuable = new List<RecordDocumentEntry>(signed.Count);
        foreach (var p in signed)
        {
            if (PrescriptionFinalCopies.Problem(await finalCopies.CheckAsync(p, ct)) is null)
            {
                issuable.Add(new RecordDocumentEntry(Prescription, p.Id, "Prescription " + p.PrescriptionNumber, p.SignedAt ?? p.PrescribedAt, p.VisitId, p.AdmissionId));
            }
        }

        return issuable;
    }

    public async Task<PrintableDocument?> BuildAsync(string kind, Guid id, Guid patientId, CancellationToken cancellationToken) => kind switch
    {
        Prescription => await PrescriptionAsync(id, patientId, cancellationToken),
        DischargeSummary => await DischargeSummaryAsync(id, patientId, cancellationToken),
        Mlc => await MlcAsync(id, patientId, cancellationToken),
        _ => null,
    };

    private async Task<PrintableDocument?> PrescriptionAsync(Guid id, Guid patientId, CancellationToken ct)
    {
        var prescription = await db.Set<Prescription>().AsNoTracking().Include(p => p.Items)
            .FirstOrDefaultAsync(p => p.Id == id && p.PatientId == patientId && p.Status == PrescriptionStatus.Signed, ct);
        if (prescription is null)
        {
            return null;
        }

        var signature = await finalCopies.CheckAsync(prescription, ct);
        return PrescriptionFinalCopies.Problem(signature) is null
            ? await PrescriptionDocument.BuildAsync(await prescriptions.BuildAsync(prescription, signature, ct), assets, ct)
            : null;
    }

    private async Task<PrintableDocument?> DischargeSummaryAsync(Guid id, Guid patientId, CancellationToken ct)
    {
        if (!await db.Set<DischargeSummary>().AnyAsync(s => s.Id == id && s.PatientId == patientId && s.Status != DischargeSummaryStatus.Draft, ct))
        {
            return null;
        }

        var print = await summaries.PrintAsync(id, ct);
        return print.IsSuccess ? await DischargeSummaryDocument.BuildAsync(print.Value, assets, DocumentText.English, ct) : null;
    }

    private async Task<PrintableDocument?> MlcAsync(Guid id, Guid patientId, CancellationToken ct)
    {
        var mlc = await db.Set<MlcRecord>().AsNoTracking().FirstOrDefaultAsync(m => m.Id == id && m.PatientId == patientId, ct);
        var patient = await db.Set<Patient>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == patientId, ct);
        return mlc is null || patient is null ? null : MlcDocument.Build(mlc, patient, await assets.LetterheadAsync(mlc.FacilityId, ct));
    }
}
