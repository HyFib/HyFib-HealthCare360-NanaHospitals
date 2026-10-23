using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;

/// <summary>
/// /api/v1/clinical — the configurable prescribing-safety tables (drug–drug interactions and maximum daily doses, ERX-02)
/// and the prescribing register of Schedule H1/X medicines (ERX-06).
/// </summary>
internal static class DrugSafetyEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/clinical").WithTags("E-Prescribing");

        group.MapGet("/drug-interactions", ListInteractions).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/drug-interactions", (SaveDrugInteractionRequest r, HospitalDbContext db, CancellationToken ct) => SaveInteraction(null, r, db, ct))
            .RequirePermission(ClinicalPermissions.TemplatesManage).WithValidation<SaveDrugInteractionRequest>();
        group.MapPut("/drug-interactions/{id:guid}", (Guid id, SaveDrugInteractionRequest r, HospitalDbContext db, CancellationToken ct) => SaveInteraction(id, r, db, ct))
            .RequirePermission(ClinicalPermissions.TemplatesManage).WithValidation<SaveDrugInteractionRequest>().RequireVersion();

        group.MapGet("/dose-limits", ListDoseLimits).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/dose-limits", (SaveDoseLimitRequest r, HospitalDbContext db, CancellationToken ct) => SaveDoseLimit(null, r, db, ct))
            .RequirePermission(ClinicalPermissions.TemplatesManage).WithValidation<SaveDoseLimitRequest>();
        group.MapPut("/dose-limits/{id:guid}", (Guid id, SaveDoseLimitRequest r, HospitalDbContext db, CancellationToken ct) => SaveDoseLimit(id, r, db, ct))
            .RequirePermission(ClinicalPermissions.TemplatesManage).WithValidation<SaveDoseLimitRequest>().RequireVersion();

        group.MapGet("/controlled-drug-register", Register).RequireAnyPermission(ClinicalPermissions.PrescriptionsSign, ClinicalPermissions.MrdRecords);
    }

    private static async Task<IResult> ListInteractions([AsParameters] PageQuery query, InteractionSeverity? severity, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var rows = db.Set<DrugInteraction>().AsNoTracking();
        if (severity is not null)
        {
            rows = rows.Where(i => i.Severity == severity);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            rows = rows.Where(i => EF.Functions.ILike(i.DrugA, like, QueryExtensions.LikeEscape) || EF.Functions.ILike(i.DrugB, like, QueryExtensions.LikeEscape));
        }

        // Severity is stored as text; order by clinical weight, not alphabetically.
        return TypedResults.Ok(await rows.OrderByDescending(i => i.Severity == InteractionSeverity.Major)
            .ThenByDescending(i => i.Severity == InteractionSeverity.Moderate)
            .ThenBy(i => i.DrugA).ThenBy(i => i.DrugB)
            .Select(i => new DrugInteractionDto(i.Id, i.DrugA, i.DrugB, i.Severity, i.Effect, i.Management, i.IsActive, i.Version))
            .ToPagedResultAsync(page, ct));
    }

    /// <summary>Pairs are stored in canonical, alphabetical order so (A, B) and (B, A) cannot both exist.</summary>
    private static async Task<IResult> SaveInteraction(Guid? id, SaveDrugInteractionRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var a = Canonical(request.DrugA);
        var b = Canonical(request.DrugB);
        if (a.Length == 0 || b.Length == 0 || a == b)
        {
            return Error.Field("drugB", "Choose two different generic drugs.").ToProblem();
        }

        (a, b) = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
        if (await db.Set<DrugInteraction>().AnyAsync(i => i.DrugA == a && i.DrugB == b && i.Id != id, ct))
        {
            return Error.Conflict("interaction.exists", $"An interaction between {a} and {b} is already defined.").ToProblem();
        }

        DrugInteraction interaction;
        if (id is { } existingId)
        {
            var found = await db.Set<DrugInteraction>().FirstOrDefaultAsync(i => i.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(DrugInteraction), existingId).ToProblem();
            }

            interaction = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(interaction, version);
            }
        }
        else
        {
            interaction = new DrugInteraction();
            db.Add(interaction);
        }

        interaction.DrugA = a;
        interaction.DrugB = b;
        interaction.Severity = request.Severity;
        interaction.Effect = request.Effect.Trim();
        interaction.Management = string.IsNullOrWhiteSpace(request.Management) ? null : request.Management.Trim();
        interaction.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        var dto = new DrugInteractionDto(interaction.Id, interaction.DrugA, interaction.DrugB, interaction.Severity, interaction.Effect, interaction.Management,
            interaction.IsActive, interaction.Version);
        return id is null ? TypedResults.Created($"/api/v1/clinical/drug-interactions/{interaction.Id}", dto) : TypedResults.Ok(dto);
    }

    private static async Task<IResult> ListDoseLimits(HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<DrugDoseLimit>().AsNoTracking().OrderBy(l => l.GenericName)
            .Select(l => new DoseLimitDto(l.Id, l.GenericName, l.MaxDailyDoseMg, l.Route, l.Notes, l.IsActive, l.Version))
            .ToListAsync(ct));

    private static async Task<IResult> SaveDoseLimit(Guid? id, SaveDoseLimitRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var name = Canonical(request.GenericName);
        var route = string.IsNullOrWhiteSpace(request.Route) ? null : request.Route.Trim().ToLowerInvariant();
        if (name.Length == 0)
        {
            return Error.Field("genericName", "Enter the generic name.").ToProblem();
        }

        if (await db.Set<DrugDoseLimit>().AnyAsync(l => l.GenericName == name && l.Route == route && l.Id != id, ct))
        {
            return Error.Conflict("dose_limit.exists", $"A limit for {name} is already defined for this route.").ToProblem();
        }

        DrugDoseLimit limit;
        if (id is { } existingId)
        {
            var found = await db.Set<DrugDoseLimit>().FirstOrDefaultAsync(l => l.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound("Dose limit", existingId).ToProblem();
            }

            limit = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(limit, version);
            }
        }
        else
        {
            limit = new DrugDoseLimit();
            db.Add(limit);
        }

        limit.GenericName = name;
        limit.MaxDailyDoseMg = request.MaxDailyDoseMg;
        limit.Route = route;
        limit.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        limit.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        var dto = new DoseLimitDto(limit.Id, limit.GenericName, limit.MaxDailyDoseMg, limit.Route, limit.Notes, limit.IsActive, limit.Version);
        return id is null ? TypedResults.Created($"/api/v1/clinical/dose-limits/{limit.Id}", dto) : TypedResults.Ok(dto);
    }

    /// <summary>Register for a date range (IST calendar dates), newest first.</summary>
    private static async Task<IResult> Register([AsParameters] PageQuery query, DateOnly? from, DateOnly? to, HospitalDbContext db, TimeProvider time,
        CancellationToken ct)
    {
        var today = Patients.Services.PatientQueries.Today(time);
        var (start, end) = ClinicalClock.RangeUtc(from ?? today.AddDays(-30), to ?? today);
        var page = await (
                from r in db.Set<ControlledDrugRegisterEntry>().AsNoTracking()
                join p in db.Set<Patient>() on r.PatientId equals p.Id
                where r.PrescribedAt >= start && r.PrescribedAt < end
                orderby r.PrescribedAt descending
                select new { Entry = r, p.Uhid, p.FullName })
            .ToPagedResultAsync(query.ToRequest(), ct);
        var names = await PrintQueries.PractitionerNamesAsync(db, page.Items.Select(x => (Guid?)x.Entry.PractitionerId), ct);
        return TypedResults.Ok(page.Map(x => new ControlledDrugRegisterDto(x.Entry.Id, x.Entry.PrescriptionId, x.Entry.PrescriptionNumber, x.Entry.PatientId, x.Uhid,
            x.FullName, x.Entry.PractitionerId, names.GetValueOrDefault(x.Entry.PractitionerId), x.Entry.DrugName, x.Entry.GenericName, x.Entry.Strength,
            x.Entry.Schedule, x.Entry.Quantity, x.Entry.PrescribedAt, x.Entry.ConfirmedByUserId)));
    }

    private static string Canonical(string name) => DrugNames.Components(name).FirstOrDefault() ?? string.Empty;
}
