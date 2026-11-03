using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Platform.Identity;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// Vaccine doses (Clinical <see cref="Immunization"/>), given here or copied from the patient's card. A dose marked as
/// entered in error is returned too, as <c>entered-in-error</c>; searches leave it out unless asked for.
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

        var state = await db.Set<Immunization>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(i => i.Id == immunizationId).Select(i => new RowState(i.PatientId, i.IsDeleted)).FirstOrDefaultAsync(ct);
        if (ResourceLookup.Unavailable(state) is { } unavailable)
        {
            return unavailable;
        }

        var immunization = await Immunizations(i => i.Id == immunizationId).FirstOrDefaultAsync(ct);
        return immunization is null ? ResourceLookup.Missing : ResourceLookup.Found(ImmunizationMapper.Map(immunization), immunization.PatientId);
    }

    public async Task<IReadOnlyList<R4.Resource>> ListForPatientAsync(Guid patientId, CancellationToken ct)
    {
        var immunizations = await Immunizations(i => i.PatientId == patientId).ToListAsync(ct);
        return [.. immunizations.Select(ImmunizationMapper.Map)];
    }

    internal IQueryable<ImmunizationRow> Immunizations(Expression<Func<Immunization, bool>> filter)
    {
        var users = db.Set<User>();
        return db.Set<Immunization>().AsNoTracking().Where(filter).Select(i => new ImmunizationRow
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
