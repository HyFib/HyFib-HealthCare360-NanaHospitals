using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Emr;

/// <summary>/api/v1/emr/patients/{patientId} — banner summary and flags, EHR timeline, current medicines and encounter history.</summary>
internal static class PatientRecordEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/emr/patients/{patientId:guid}").WithTags("EMR");

        group.MapGet("/summary", Summary).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapGet("/flags", Flags).RequireAnyPermission(ClinicalPermissions.FlagsRead, ClinicalPermissions.EmrRead);
        group.MapGet("/timeline", Timeline).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapGet("/medications", Medications).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapGet("/encounters", Encounters).RequirePermission(ClinicalPermissions.EmrRead);
    }

    private static async Task<IResult> Summary(Guid patientId, ClinicalAccess access, PatientRecordQueries queries, CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Viewed clinical summary", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var summary = await queries.SummaryAsync(patientId, ct);
        return summary is null ? Error.NotFound("Patient", patientId).ToProblem() : TypedResults.Ok(summary);
    }

    /// <summary>
    /// Allergies and alerts for the banner on every clinical screen (EHR-03), for staff who may see the flags without the
    /// chart. Restricted records stay behind the same guard as the chart, and each read is recorded like any other.
    /// </summary>
    private static async Task<IResult> Flags(Guid patientId, ClinicalAccess access, PatientRecordQueries queries, CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Viewed allergies and alerts", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await queries.FlagsAsync(patientId, ct));
    }

    /// <summary><c>types</c> is a comma-separated list of <see cref="TimelineEntryType"/> names; dates are IST calendar dates.</summary>
    private static async Task<IResult> Timeline(
        Guid patientId,
        [AsParameters] PageQuery query,
        string? types,
        DateOnly? from,
        DateOnly? to,
        Guid? departmentId,
        ClinicalAccess access,
        TimelineQueries timeline,
        CancellationToken ct)
    {
        var wanted = new HashSet<TimelineEntryType>();
        foreach (var name in (types ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<TimelineEntryType>(name, ignoreCase: true, out var type))
            {
                return Error.Field("types", $"Unknown timeline type '{name}'.").ToProblem();
            }

            wanted.Add(type);
        }

        if (from is not null && to is not null && from > to)
        {
            return Error.Field("from", "The start date must be on or before the end date.").ToProblem();
        }

        var opened = await access.OpenAsync(patientId, "Viewed EHR timeline", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        return TypedResults.Ok(await timeline.QueryAsync(patientId, new TimelineFilter(wanted, from, to, departmentId), query.ToRequest(), ct));
    }

    private static async Task<IResult> Medications(Guid patientId, ClinicalAccess access, PatientRecordQueries queries, CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Viewed current medications", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await queries.CurrentMedicationsAsync(patientId, ct));
    }

    private static async Task<IResult> Encounters(Guid patientId, [AsParameters] PageQuery query, EncounterType? type, ClinicalAccess access,
        HospitalDbContext db, CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Viewed encounter history", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var encounters = db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(e => e.PatientId == patientId);
        if (type is not null)
        {
            encounters = encounters.Where(e => e.Type == type);
        }

        var page = await encounters.OrderByDescending(e => e.StartedAt)
            .Select(e => new { e.Id, e.PatientId, e.Type, e.Status, e.StartedAt, e.SignedAt, e.PractitionerId, e.DepartmentId, e.ChiefComplaint })
            .ToPagedResultAsync(query.ToRequest(), ct);
        var ids = page.Items.Select(e => (Guid?)e.Id).ToList();
        var diagnoses = await db.Set<Diagnosis>().AsNoTracking().Where(d => ids.Contains(d.EncounterId))
            .OrderByDescending(d => d.IsPrimary)
            .Select(d => new { d.EncounterId, Label = d.IcdCode == null ? d.Title : d.IcdCode + " " + d.Title })
            .ToListAsync(ct);
        var practitioners = await PrintQueries.PractitionerNamesAsync(db, page.Items.Select(e => (Guid?)e.PractitionerId), ct);
        var departments = await PrintQueries.DepartmentNamesAsync(db, page.Items.Select(e => e.DepartmentId), ct);

        return TypedResults.Ok(page.Map(e => new EncounterListItem(e.Id, e.PatientId, e.Type, e.Status, e.StartedAt, e.SignedAt, e.PractitionerId,
            practitioners.GetValueOrDefault(e.PractitionerId), e.DepartmentId, e.DepartmentId is { } d ? departments.GetValueOrDefault(d) : null,
            e.ChiefComplaint, diagnoses.Where(x => x.EncounterId == e.Id).Select(x => x.Label).ToList())));
    }
}
