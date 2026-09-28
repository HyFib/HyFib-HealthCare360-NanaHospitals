using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Patients.Domain;
using ClinicalEncounter = HyFib.HealthCare360.Modules.Clinical.Domain.Encounter;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>An in-patient admission with the visit it is anchored to.</summary>
internal sealed record VisitAdmission(Guid VisitId, AdmissionRow Admission);

/// <summary>Encounters: Patients visits (with Inpatient admissions) and Clinical encounters.</summary>
internal sealed class EncounterProvider(HospitalDbContext db, ReferenceDirectoryLoader references) : IFhirResourceProvider
{
    public string ResourceType => FhirResourceTypes.Encounter;

    public async Task<ResourceLookup> FindAsync(string id, CancellationToken ct)
    {
        if (FhirIds.TryParse(id, FhirIds.Visit, out var visitId))
        {
            var state = await db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(v => v.Id == visitId).Select(v => new RowState(v.PatientId, v.IsDeleted)).FirstOrDefaultAsync(ct);
            if (ResourceLookup.Unavailable(state) is { } unavailable)
            {
                return unavailable;
            }

            var visits = await WithAdmissionsAsync(await Visits(v => v.Id == visitId).ToListAsync(ct), ct);
            return visits.Count == 0 ? ResourceLookup.Missing : ResourceLookup.Found((await MapAsync(visits, [], ct))[0], state!.PatientId);
        }

        if (FhirIds.TryParse(id, FhirIds.ClinicalEncounter, out var encounterId))
        {
            var state = await db.Set<ClinicalEncounter>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(e => e.Id == encounterId).Select(e => new RowState(e.PatientId, e.IsDeleted)).FirstOrDefaultAsync(ct);
            if (ResourceLookup.Unavailable(state) is { } unavailable)
            {
                return unavailable;
            }

            var encounters = await Encounters(e => e.Id == encounterId).ToListAsync(ct);
            return encounters.Count == 0 ? ResourceLookup.Missing : ResourceLookup.Found((await MapAsync([], encounters, ct))[0], state!.PatientId);
        }

        return ResourceLookup.Missing;
    }

    public async Task<IReadOnlyList<R4.Resource>> ListForPatientAsync(Guid patientId, CancellationToken ct)
    {
        var visits = await WithAdmissionsAsync(await Visits(v => v.PatientId == patientId).ToListAsync(ct), ct);
        var encounters = await Encounters(e => e.PatientId == patientId).ToListAsync(ct);
        return await MapAsync(visits, encounters, ct);
    }

    internal IQueryable<VisitRow> Visits(Expression<Func<Visit, bool>> filter) =>
        db.Set<Visit>().AsNoTracking().Where(filter).Select(v => new VisitRow
        {
            Id = v.Id,
            PatientId = v.PatientId,
            FacilityId = v.FacilityId,
            VisitNumber = v.VisitNumber,
            Type = v.Type,
            Status = v.Status,
            DepartmentId = v.DepartmentId,
            PractitionerId = v.PractitionerId,
            VisitedAt = v.VisitedAt,
            CompletedAt = v.CompletedAt,
            ChiefComplaint = v.ChiefComplaint,
            CreatedAt = v.CreatedAt,
            UpdatedAt = v.UpdatedAt,
        });

    internal IQueryable<ClinicalEncounterRow> Encounters(Expression<Func<ClinicalEncounter, bool>> filter) =>
        db.Set<ClinicalEncounter>().AsNoTracking().Where(filter).Select(e => new ClinicalEncounterRow
        {
            Id = e.Id,
            PatientId = e.PatientId,
            FacilityId = e.FacilityId,
            VisitId = e.VisitId,
            AdmissionId = e.AdmissionId,
            PractitionerId = e.PractitionerId,
            DepartmentId = e.DepartmentId,
            Type = e.Type,
            Status = e.Status,
            StartedAt = e.StartedAt,
            SignedAt = e.SignedAt,
            ChiefComplaint = e.ChiefComplaint,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt,
        });

    internal IQueryable<VisitAdmission> Admissions(Guid[] visitIds) =>
        db.Set<Admission>().AsNoTracking().Where(a => visitIds.Contains(a.VisitId)).Select(a => new VisitAdmission(a.VisitId, new AdmissionRow
        {
            Id = a.Id,
            AdmissionNumber = a.AdmissionNumber,
            Type = a.Type,
            Source = a.Source,
            Status = a.Status,
            AdmittedAt = a.AdmittedAt,
            AdmittingPractitionerId = a.AdmittingPractitionerId,
            ConsultantPractitionerId = a.ConsultantPractitionerId,
            DepartmentId = a.DepartmentId,
            DischargeType = a.DischargeType,
            DischargedAt = a.DischargedAt,
            CancelledAt = a.CancelledAt,
            CreatedAt = a.CreatedAt,
            UpdatedAt = a.UpdatedAt,
        }));

    /// <summary>Attaches the admission of each in-patient visit (an admission is anchored to its visit).</summary>
    private async Task<List<VisitRow>> WithAdmissionsAsync(List<VisitRow> visits, CancellationToken ct)
    {
        if (visits.Count == 0)
        {
            return visits;
        }

        var admissions = await Admissions([.. visits.Select(v => v.Id)]).ToListAsync(ct);
        var byVisit = admissions.GroupBy(a => a.VisitId).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Admission.AdmittedAt).Last().Admission);
        return [.. visits.Select(v => byVisit.TryGetValue(v.Id, out var admission) ? v with { Admission = admission } : v)];
    }

    private async Task<List<R4.Resource>> MapAsync(IReadOnlyList<VisitRow> visits, IReadOnlyList<ClinicalEncounterRow> encounters, CancellationToken ct)
    {
        var ids = new ReferenceIds();
        foreach (var v in visits)
        {
            ids.Practitioner(v.PractitionerId, v.Admission?.AdmittingPractitionerId, v.Admission?.ConsultantPractitionerId)
                .Department(v.DepartmentId, v.Admission?.DepartmentId)
                .Facility(v.FacilityId);
        }

        foreach (var e in encounters)
        {
            ids.Practitioner(e.PractitionerId).Department(e.DepartmentId).Facility(e.FacilityId).Admission(e.AdmissionId);
        }

        var refs = await references.LoadAsync(ids, ct);
        return [.. visits.Select(v => (R4.Resource)EncounterMapper.FromVisit(v, refs)), .. encounters.Select(e => EncounterMapper.FromClinicalEncounter(e, refs))];
    }
}
