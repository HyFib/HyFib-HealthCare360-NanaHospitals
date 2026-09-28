using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>Conditions: Clinical diagnoses and the problem list.</summary>
internal sealed class ConditionProvider(HospitalDbContext db) : IFhirResourceProvider
{
    public string ResourceType => FhirResourceTypes.Condition;

    public async Task<ResourceLookup> FindAsync(string id, CancellationToken ct)
    {
        if (!FhirIds.TryParse(id, out var diagnosisId))
        {
            return ResourceLookup.Missing;
        }

        var state = await db.Set<Diagnosis>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(d => d.Id == diagnosisId).Select(d => new RowState(d.PatientId, d.IsDeleted)).FirstOrDefaultAsync(ct);
        if (ResourceLookup.Unavailable(state) is { } unavailable)
        {
            return unavailable;
        }

        var diagnosis = await Diagnoses(d => d.Id == diagnosisId).FirstOrDefaultAsync(ct);
        return diagnosis is null ? ResourceLookup.Missing : ResourceLookup.Found(ConditionMapper.Map(diagnosis), diagnosis.PatientId);
    }

    public async Task<IReadOnlyList<R4.Resource>> ListForPatientAsync(Guid patientId, CancellationToken ct)
    {
        var diagnoses = await Diagnoses(d => d.PatientId == patientId).ToListAsync(ct);
        return [.. diagnoses.Select(ConditionMapper.Map)];
    }

    internal IQueryable<DiagnosisRow> Diagnoses(Expression<Func<Diagnosis, bool>> filter) =>
        db.Set<Diagnosis>().AsNoTracking().Where(filter).Select(d => new DiagnosisRow
        {
            Id = d.Id,
            PatientId = d.PatientId,
            EncounterId = d.EncounterId,
            IcdCode = d.IcdCode,
            Title = d.Title,
            Type = d.Type,
            IsPrimary = d.IsPrimary,
            OnsetDate = d.OnsetDate,
            Status = d.Status,
            ResolvedOn = d.ResolvedOn,
            RecordedAt = d.RecordedAt,
            Notes = d.Notes,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt,
        });
}
