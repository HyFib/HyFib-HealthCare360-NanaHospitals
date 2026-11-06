using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Platform.Identity;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// Vaccine doses (Clinical <see cref="Immunization"/>), given here or copied from the patient's card. A dose marked as
/// entered in error is returned too, as <c>entered-in-error</c>; searches leave it out unless asked for. A dose is the
/// facility's that recorded it, and a reader reads its own facility's only (<see cref="ReaderFacility"/>), though staff
/// see a patient's doses from every facility.
/// </summary>
internal sealed class ImmunizationProvider(HospitalDbContext db) : IFhirResourceProvider
{
    public string ResourceType => FhirResourceTypes.Immunization;

    public async Task<ResourceLookup> FindAsync(string id, CancellationToken ct)
    {
        if (!FhirIds.TryParse(id, out var immunizationId))
        {
            return ResourceLookup.Missing;
        }

        var state = await StateOf(immunizationId).FirstOrDefaultAsync(ct);
        if (ResourceLookup.Unavailable(state) is { } unavailable)
        {
            return unavailable;
        }

        var immunization = await ReadersImmunizations(i => i.Id == immunizationId).FirstOrDefaultAsync(ct);
        return immunization is null ? ResourceLookup.Missing : ResourceLookup.Found(ImmunizationMapper.Map(immunization), immunization.PatientId);
    }

    public async Task<SearchCandidates> ListForPatientAsync(Guid patientId, SearchRequest request, PatientAccess access, CancellationToken ct)
    {
        var immunizations = await ReadersImmunizations(i => i.PatientId == patientId).ToListAsync(ct);
        return new SearchCandidates([.. immunizations.Select(ImmunizationMapper.Map)]);
    }

    /// <summary>The patient and removal of a dose recorded at the reader's facility, removed doses included (to answer 410).</summary>
    internal IQueryable<RowState> StateOf(Guid immunizationId)
    {
        var facility = ReaderFacility.Of(db);
        return db.Set<Immunization>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(i => i.Id == immunizationId && i.FacilityId == facility).Select(i => new RowState(i.PatientId, i.IsDeleted));
    }

    /// <summary>The doses recorded at the reader's facility, as rows.</summary>
    internal IQueryable<ImmunizationRow> ReadersImmunizations(Expression<Func<Immunization, bool>> filter)
    {
        var facility = ReaderFacility.Of(db);
        return Rows(db.Set<Immunization>().Where(filter).Where(i => i.FacilityId == facility));
    }

    /// <summary>Doses as rows, of every facility (the ABDM export reads a dose by id).</summary>
    internal IQueryable<ImmunizationRow> Immunizations(Expression<Func<Immunization, bool>> filter) => Rows(db.Set<Immunization>().Where(filter));

    private IQueryable<ImmunizationRow> Rows(IQueryable<Immunization> doses)
    {
        var users = db.Set<User>();
        return doses.AsNoTracking().Select(i => new ImmunizationRow
        {
            Id = i.Id,
            PatientId = i.PatientId,
            EncounterId = i.EncounterId,
            VaccineName = i.VaccineName,
            VaccineCode = i.VaccineCode,
            DoseNumber = i.DoseNumber,
            AdministeredOn = i.AdministeredOn,
            Route = i.Route,
            Site = i.Site,
            BatchNumber = i.BatchNumber,
            Manufacturer = i.Manufacturer,
            ExpiryDate = i.ExpiryDate,
            AdministeredByUserName = users.Where(u => u.Id == i.AdministeredByUserId).Select(u => u.DisplayName).FirstOrDefault(),
            AdministeredBy = i.AdministeredBy,
            IsExternal = i.IsExternal,
            NextDueOn = i.NextDueOn,
            Notes = i.Notes,
            IsEnteredInError = i.IsEnteredInError,
            EnteredInErrorReason = i.EnteredInErrorReason,
            CreatedAt = i.CreatedAt,
            UpdatedAt = i.UpdatedAt,
        });
    }
}
