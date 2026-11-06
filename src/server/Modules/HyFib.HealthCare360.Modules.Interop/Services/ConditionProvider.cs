using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using ClinicalEncounter = HyFib.HealthCare360.Modules.Clinical.Domain.Encounter;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// Conditions: Clinical diagnoses and the problem list. A diagnosis is its encounter's facility's, and a reader reads its
/// own facility's only (<see cref="ReaderFacility"/>).
/// </summary>
internal sealed class ConditionProvider(HospitalDbContext db) : IFhirResourceProvider
{
    public string ResourceType => FhirResourceTypes.Condition;

    public async Task<ResourceLookup> FindAsync(string id, CancellationToken ct)
    {
        if (!FhirIds.TryParse(id, out var diagnosisId))
        {
            return ResourceLookup.Missing;
        }

        var state = await StateOf(diagnosisId).FirstOrDefaultAsync(ct);
        if (ResourceLookup.Unavailable(state) is { } unavailable)
        {
            return unavailable;
        }

        var diagnosis = await Rows(ReadersDiagnoses().Where(d => d.Id == diagnosisId)).FirstOrDefaultAsync(ct);
        return diagnosis is null ? ResourceLookup.Missing : ResourceLookup.Found(ConditionMapper.Map(diagnosis), diagnosis.PatientId);
    }

    public async Task<SearchCandidates> ListForPatientAsync(Guid patientId, SearchRequest request, PatientAccess access, CancellationToken ct)
    {
        var diagnoses = await Rows(ReadersDiagnoses().Where(d => d.PatientId == patientId)).ToListAsync(ct);
        return new SearchCandidates([.. diagnoses.Select(ConditionMapper.Map)]);
    }

    /// <summary>Diagnoses as rows, of every facility (the ABDM export reads a patient's own encounters by id).</summary>
    internal IQueryable<DiagnosisRow> Diagnoses(Expression<Func<Diagnosis, bool>> filter) => Rows(db.Set<Diagnosis>().Where(filter));

    /// <summary>
    /// The patient and removal of a diagnosis of the reader's facility, removed rows included (to answer 410): each source
    /// says itself that it is read past its soft-delete filter, and the encounter keeps its facility filter.
    /// </summary>
    internal IQueryable<RowState> StateOf(Guid diagnosisId) =>
        db.Set<Diagnosis>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(d => d.Id == diagnosisId)
            .Where(AtReadersFacility(db.Set<ClinicalEncounter>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])))
            .Select(d => new RowState(d.PatientId, d.IsDeleted));

    /// <summary>The diagnoses made in the reader's facility's encounters.</summary>
    internal IQueryable<Diagnosis> ReadersDiagnoses() => db.Set<Diagnosis>().Where(AtReadersFacility(db.Set<ClinicalEncounter>()));

    /// <summary>A diagnosis made in an encounter of the reader's facility (one with no encounter is no facility's).</summary>
    private Expression<Func<Diagnosis, bool>> AtReadersFacility(IQueryable<ClinicalEncounter> encounters)
    {
        var facility = ReaderFacility.Of(db);
        return d => encounters.Any(e => e.Id == d.EncounterId && e.FacilityId == facility);
    }

    private static IQueryable<DiagnosisRow> Rows(IQueryable<Diagnosis> diagnoses) =>
        diagnoses.AsNoTracking().Select(d => new DiagnosisRow
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
