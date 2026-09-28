using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>AllergyIntolerance: Clinical allergies (including those marked entered-in-error, for reads).</summary>
internal sealed class AllergyIntoleranceProvider(HospitalDbContext db) : IFhirResourceProvider
{
    public string ResourceType => FhirResourceTypes.AllergyIntolerance;

    public async Task<ResourceLookup> FindAsync(string id, CancellationToken ct)
    {
        if (!FhirIds.TryParse(id, out var allergyId))
        {
            return ResourceLookup.Missing;
        }

        var state = await db.Set<Allergy>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(a => a.Id == allergyId).Select(a => new RowState(a.PatientId, a.IsDeleted)).FirstOrDefaultAsync(ct);
        if (ResourceLookup.Unavailable(state) is { } unavailable)
        {
            return unavailable;
        }

        var allergy = await Allergies(a => a.Id == allergyId).FirstOrDefaultAsync(ct);
        return allergy is null ? ResourceLookup.Missing : ResourceLookup.Found(AllergyIntoleranceMapper.Map(allergy), allergy.PatientId);
    }

    public async Task<IReadOnlyList<R4.Resource>> ListForPatientAsync(Guid patientId, CancellationToken ct)
    {
        var allergies = await Allergies(a => a.PatientId == patientId).ToListAsync(ct);
        return [.. allergies.Select(AllergyIntoleranceMapper.Map)];
    }

    internal IQueryable<AllergyRow> Allergies(Expression<Func<Allergy, bool>> filter) =>
        db.Set<Allergy>().AsNoTracking().Where(filter).Select(a => new AllergyRow
        {
            Id = a.Id,
            PatientId = a.PatientId,
            Allergen = a.Allergen,
            Type = a.Type,
            Reaction = a.Reaction,
            Severity = a.Severity,
            Status = a.Status,
            OnsetDate = a.OnsetDate,
            RecordedAt = a.RecordedAt,
            Notes = a.Notes,
            StatusReason = a.StatusReason,
            CreatedAt = a.CreatedAt,
            UpdatedAt = a.UpdatedAt,
        });
}
