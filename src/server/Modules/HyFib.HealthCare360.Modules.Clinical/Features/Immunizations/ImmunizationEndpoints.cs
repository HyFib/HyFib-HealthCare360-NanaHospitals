using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Vitals;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Immunizations;

/// <summary>/api/v1/emr — immunisation history (doses given here and transcribed from cards) (EHR-01).</summary>
internal static class ImmunizationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/emr").WithTags("EMR");

        group.MapGet("/patients/{patientId:guid}/immunizations", List).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/patients/{patientId:guid}/immunizations", Record).RequirePermission(ClinicalPermissions.ImmunizationsRecord)
            .WithValidation<RecordImmunizationRequest>();
        group.MapPost("/immunizations/{id:guid}/entered-in-error", EnteredInError).RequirePermission(ClinicalPermissions.ImmunizationsRecord)
            .WithValidation<EnteredInErrorRequest>();
    }

    private static async Task<IResult> List(Guid patientId, bool? includeEnteredInError, ClinicalAccess access, HospitalDbContext db, CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Viewed immunisations", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var rows = db.Set<Immunization>().AsNoTracking().Where(i => i.PatientId == patientId);
        if (includeEnteredInError != true)
        {
            rows = rows.Where(i => !i.IsEnteredInError);
        }

        return TypedResults.Ok((await rows.OrderByDescending(i => i.AdministeredOn).ToListAsync(ct)).Select(ToDto).ToList());
    }

    private static async Task<IResult> Record(Guid patientId, RecordImmunizationRequest request, ClinicalAccess access, HospitalDbContext db, ICurrentUser user,
        CancellationToken ct)
    {
        var allowed = await access.EnsureCanRecordAsync(patientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        var immunization = new Immunization
        {
            PatientId = patientId,
            EncounterId = request.EncounterId,
            VaccineName = request.VaccineName.Trim(),
            VaccineCode = request.VaccineCode?.Trim().ToUpperInvariant(),
            DoseNumber = request.DoseNumber,
            AdministeredOn = request.AdministeredOn,
            Route = request.Route?.Trim(),
            Site = request.Site?.Trim(),
            BatchNumber = request.BatchNumber?.Trim(),
            Manufacturer = request.Manufacturer?.Trim(),
            ExpiryDate = request.ExpiryDate,
            AdministeredByUserId = request.IsExternal ? null : user.UserId,
            AdministeredBy = request.AdministeredBy?.Trim(),
            IsExternal = request.IsExternal,
            NextDueOn = request.NextDueOn,
            Notes = request.Notes?.Trim(),
        };
        db.Add(immunization);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/emr/patients/{patientId}/immunizations", ToDto(immunization));
    }

    private static async Task<IResult> EnteredInError(Guid id, EnteredInErrorRequest request, ClinicalAccess access, HospitalDbContext db, CancellationToken ct)
    {
        var immunization = await db.Set<Immunization>().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (immunization is null)
        {
            return Error.NotFound(nameof(Immunization), id).ToProblem();
        }

        var allowed = await access.EnsureCanOpenAsync(immunization.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        var result = immunization.MarkEnteredInError(request.Reason);
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(immunization));
    }

    internal static ImmunizationDto ToDto(Immunization i) => new(
        i.Id, i.PatientId, i.EncounterId, i.VaccineName, i.VaccineCode, i.DoseNumber, i.AdministeredOn, i.Route, i.Site, i.BatchNumber, i.Manufacturer,
        i.ExpiryDate, i.AdministeredBy, i.IsExternal, i.NextDueOn, i.Notes, i.IsEnteredInError, i.EnteredInErrorReason);
}
