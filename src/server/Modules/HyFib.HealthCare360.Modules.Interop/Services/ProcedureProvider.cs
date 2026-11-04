using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// Procedures (Clinical <see cref="ProcedureRecord"/>): recorded on the chart, or recorded when the theatre completed an
/// operation. A record marked as entered in error is returned too, as <c>entered-in-error</c>; searches leave it out
/// unless asked for.
/// </summary>
internal sealed class ProcedureProvider(HospitalDbContext db, ReferenceDirectoryLoader references) : IFhirResourceProvider
{
    public string ResourceType => FhirResourceTypes.Procedure;

    public async Task<ResourceLookup> FindAsync(string id, CancellationToken ct)
    {
        if (!FhirIds.TryParse(id, out var procedureId))
        {
            return ResourceLookup.Missing;
        }

        // A removed record too, only to tell it (410) from one that never was (404); what is shared is read below with
        // every filter.
        var state = await db.Set<ProcedureRecord>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(p => p.Id == procedureId).Select(p => new RowState(p.PatientId, p.IsDeleted)).FirstOrDefaultAsync(ct);
        if (ResourceLookup.Unavailable(state) is { } unavailable)
        {
            return unavailable;
        }

        var rows = await Procedures(p => p.Id == procedureId).ToListAsync(ct);
        return rows.Count == 0 ? ResourceLookup.Missing : ResourceLookup.Found((await MapAsync(rows, ct))[0], rows[0].PatientId);
    }

    public async Task<SearchCandidates> ListForPatientAsync(Guid patientId, SearchRequest request, PatientAccess access, CancellationToken ct)
    {
        var rows = await Procedures(p => p.PatientId == patientId).ToListAsync(ct);
        return new SearchCandidates(await MapAsync(rows, ct));
    }

    internal IQueryable<ProcedureRow> Procedures(Expression<Func<ProcedureRecord, bool>> filter) =>
        db.Set<ProcedureRecord>().AsNoTracking().Where(filter).Select(p => new ProcedureRow
        {
            Id = p.Id,
            PatientId = p.PatientId,
            EncounterId = p.EncounterId,
            VisitId = p.VisitId,
            AdmissionId = p.AdmissionId,
            PerformedAt = p.PerformedAt,
            Code = p.Code,
            CodeSystem = p.CodeSystem,
            Display = p.Display,
            PerformerPractitionerId = p.PerformerPractitionerId,
            PerformerName = p.PerformerName,
            Side = p.Side,
            BodySite = p.BodySite,
            Outcome = p.Outcome,
            Notes = p.Notes,
            Source = p.Source,
            Status = p.Status,
            StatusReason = p.StatusReason,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
        });

    private async Task<List<R4.Resource>> MapAsync(IReadOnlyList<ProcedureRow> rows, CancellationToken ct)
    {
        var ids = new ReferenceIds();
        foreach (var row in rows)
        {
            ids.Practitioner(row.PerformerPractitionerId).Admission(row.AdmissionId);
        }

        var refs = await references.LoadAsync(ids, ct);
        return [.. rows.Select(r => ProcedureMapper.Map(r, refs))];
    }
}
