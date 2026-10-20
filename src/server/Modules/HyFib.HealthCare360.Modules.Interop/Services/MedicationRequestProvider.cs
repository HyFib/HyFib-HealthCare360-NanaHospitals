using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Patients.Services;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>MedicationRequest: lines of signed Clinical e-prescriptions (and of signed ones cancelled later).</summary>
internal sealed class MedicationRequestProvider(HospitalDbContext db, ReferenceDirectoryLoader references, TimeProvider time) : IFhirResourceProvider
{
    public string ResourceType => FhirResourceTypes.MedicationRequest;

    public async Task<ResourceLookup> FindAsync(string id, CancellationToken ct)
    {
        if (!FhirIds.TryParse(id, out var itemId))
        {
            return ResourceLookup.Missing;
        }

        var state = await (from i in db.Set<PrescriptionItem>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                           join p in db.Set<Prescription>() on i.PrescriptionId equals p.Id
                           where i.Id == itemId
                           select new RowState(p.PatientId, i.IsDeleted)).FirstOrDefaultAsync(ct);
        if (ResourceLookup.Unavailable(state) is { } unavailable)
        {
            return unavailable;
        }

        var rows = await SignedLines(i => i.Id == itemId, p => true).ToListAsync(ct);
        return rows.Count == 0 ? ResourceLookup.Missing : ResourceLookup.Found((await MapAsync(rows, ct))[0], state!.PatientId);
    }

    public async Task<IReadOnlyList<R4.Resource>> ListForPatientAsync(Guid patientId, CancellationToken ct)
    {
        var rows = await SignedLines(i => true, p => p.PatientId == patientId).ToListAsync(ct);
        return await MapAsync(rows, ct);
    }

    /// <summary>Lines of signed prescriptions, and of signed ones cancelled later; drafts never leave the hospital.</summary>
    internal IQueryable<PrescriptionItemRow> SignedLines(Expression<Func<PrescriptionItem, bool>> items, Expression<Func<Prescription, bool>> prescriptions) =>
        LineRows(db.Set<PrescriptionItem>().AsNoTracking().Where(items), db.Set<Prescription>().Where(prescriptions)
            .Where(p => p.Status == PrescriptionStatus.Signed || (p.Status == PrescriptionStatus.Cancelled && p.SignedAt != null)));

    /// <summary>The lines of these prescriptions as rows, whatever their status (also an ABDM prescription record's).</summary>
    internal static IQueryable<PrescriptionItemRow> LineRows(IQueryable<PrescriptionItem> items, IQueryable<Prescription> prescriptions) =>
        from i in items
        join p in prescriptions on i.PrescriptionId equals p.Id
        select new PrescriptionItemRow
        {
            Id = i.Id,
            PatientId = p.PatientId,
            PrescriptionId = p.Id,
            PrescriptionNumber = p.PrescriptionNumber,
            EncounterId = p.EncounterId,
            VisitId = p.VisitId,
            AdmissionId = p.AdmissionId,
            PractitionerId = p.PractitionerId,
            IsInpatient = p.IsInpatient,
            Status = p.Status,
            PrescribedAt = p.PrescribedAt,
            SignedAt = p.SignedAt,
            CancelledAt = p.CancelledAt,
            CancelReason = p.CancelReason,
            RefillsAllowed = p.RefillsAllowed,
            Sequence = i.Sequence,
            DrugName = i.DrugName,
            GenericName = i.GenericName,
            Strength = i.Strength,
            DosageForm = i.DosageForm,
            Route = i.Route,
            Dose = i.Dose,
            Frequency = i.Frequency,
            CustomFrequency = i.CustomFrequency,
            DurationDays = i.DurationDays,
            Quantity = i.Quantity,
            Instructions = i.Instructions,
            CreatedAt = i.CreatedAt,
            UpdatedAt = i.UpdatedAt,
            PrescriptionUpdatedAt = p.UpdatedAt,
        };

    private async Task<List<R4.Resource>> MapAsync(IReadOnlyList<PrescriptionItemRow> rows, CancellationToken ct)
    {
        var ids = new ReferenceIds();
        foreach (var row in rows)
        {
            ids.Practitioner(row.PractitionerId).Admission(row.AdmissionId);
        }

        var refs = await references.LoadAsync(ids, ct);
        var today = time.Today();
        return [.. rows.OrderBy(r => r.PrescriptionId).ThenBy(r => r.Sequence).Select(r => MedicationRequestMapper.Map(r, refs, today))];
    }
}
