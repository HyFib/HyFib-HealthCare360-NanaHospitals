using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Templates;

/// <summary>
/// Speciality templates (EMR-02), order sets (EMR-05) and practitioner favourites (ERX-01). Shared templates are visible
/// to everyone; personal templates only to their owner.
/// </summary>
internal static class TemplateEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var templates = app.MapGroup("/api/v1/emr/templates").WithTags("EMR");
        templates.MapGet("/", ListTemplates).RequirePermission(ClinicalPermissions.EmrRead);
        templates.MapGet("/{id:guid}", GetTemplate).RequirePermission(ClinicalPermissions.EmrRead);
        templates.MapPost("/", (SaveTemplateRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveTemplate(null, r, db, u, ct))
            .RequirePermission(ClinicalPermissions.TemplatesManage).WithValidation<SaveTemplateRequest>();
        templates.MapPut("/{id:guid}", (Guid id, SaveTemplateRequest r, HospitalDbContext db, ICurrentUser u, CancellationToken ct) => SaveTemplate(id, r, db, u, ct))
            .RequirePermission(ClinicalPermissions.TemplatesManage).WithValidation<SaveTemplateRequest>();

        var orderSets = app.MapGroup("/api/v1/clinical/order-sets").WithTags("Clinical Orders");
        orderSets.MapGet("/", ListOrderSets).RequirePermission(ClinicalPermissions.EmrRead);
        orderSets.MapPost("/", (SaveOrderSetRequest r, HospitalDbContext db, CancellationToken ct) => SaveOrderSet(null, r, db, ct))
            .RequirePermission(ClinicalPermissions.TemplatesManage).WithValidation<SaveOrderSetRequest>();
        orderSets.MapPut("/{id:guid}", (Guid id, SaveOrderSetRequest r, HospitalDbContext db, CancellationToken ct) => SaveOrderSet(id, r, db, ct))
            .RequirePermission(ClinicalPermissions.TemplatesManage).WithValidation<SaveOrderSetRequest>();

        var favourites = app.MapGroup("/api/v1/emr/favourites").WithTags("EMR");
        favourites.MapGet("/", ListFavourites).RequirePermission(ClinicalPermissions.EmrRead);
        favourites.MapPost("/", AddFavourite).RequireAnyPermission(ClinicalPermissions.EmrWrite, ClinicalPermissions.PrescriptionsWrite)
            .WithValidation<SaveFavouriteRequest>();
        favourites.MapDelete("/{id:guid}", RemoveFavourite).RequireAnyPermission(ClinicalPermissions.EmrWrite, ClinicalPermissions.PrescriptionsWrite);
    }

    // ------------------------------------------------------------------ templates

    private static async Task<IResult> ListTemplates(string? specialtyCode, string? search, bool? includeInactive, HospitalDbContext db, ICurrentUser user,
        CancellationToken ct)
    {
        var practitionerId = user.PractitionerId;
        var templates = db.Set<ClinicalTemplate>().AsNoTracking().Where(t => t.OwnerPractitionerId == null || t.OwnerPractitionerId == practitionerId);
        if (includeInactive != true)
        {
            templates = templates.Where(t => t.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(specialtyCode))
        {
            var code = specialtyCode.Trim().ToUpperInvariant();
            templates = templates.Where(t => t.SpecialtyCode == code);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = search.Trim().ToLikePattern();
            templates = templates.Where(t => EF.Functions.ILike(t.Name, like) || EF.Functions.ILike(t.Code, like));
        }

        return TypedResults.Ok(await templates.OrderBy(t => t.SpecialtyCode).ThenBy(t => t.DisplayOrder).ThenBy(t => t.Name)
            .Select(t => new TemplateListItem(t.Id, t.Code, t.Name, t.SpecialtyCode, t.OwnerPractitionerId != null, t.DisplayOrder, t.IsActive))
            .ToListAsync(ct));
    }

    private static async Task<IResult> GetTemplate(Guid id, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var template = await db.Set<ClinicalTemplate>().AsNoTracking().Include(t => t.Medications).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (template is null || (template.OwnerPractitionerId is not null && template.OwnerPractitionerId != user.PractitionerId))
        {
            return Error.NotFound("Template", id).ToProblem();
        }

        return TypedResults.Ok(await ToDtoAsync(template, db, ct));
    }

    private static async Task<IResult> SaveTemplate(Guid? id, SaveTemplateRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (request.Personal && user.PractitionerId is null)
        {
            return Error.Rule("practitioner.required", "Personal templates need a user account linked to a doctor.").ToProblem();
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<ClinicalTemplate>().AnyAsync(t => t.Code == code && t.Id != id, ct))
        {
            return Error.Conflict("template.code_in_use", $"Template code {code} is already used.").ToProblem();
        }

        var services = await ValidServiceIdsAsync(db, request.DefaultServiceItemIds ?? [], ct);
        if (services.IsFailure)
        {
            return services.Error!.ToProblem();
        }

        ClinicalTemplate template;
        if (id is { } existingId)
        {
            var found = await db.Set<ClinicalTemplate>().Include(t => t.Medications).FirstOrDefaultAsync(t => t.Id == existingId, ct);
            if (found is null || (found.OwnerPractitionerId is not null && found.OwnerPractitionerId != user.PractitionerId))
            {
                return Error.NotFound("Template", existingId).ToProblem();
            }

            template = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(template, version);
            }
        }
        else
        {
            template = new ClinicalTemplate();
            db.Add(template);
        }

        template.Code = code;
        template.Name = request.Name.Trim();
        template.SpecialtyCode = request.SpecialtyCode?.Trim().ToUpperInvariant();
        template.OwnerPractitionerId = request.Personal ? user.PractitionerId : null;
        template.ChiefComplaint = Clean(request.ChiefComplaint);
        template.HistoryOfPresentIllness = Clean(request.HistoryOfPresentIllness);
        template.Examination = Clean(request.Examination);
        template.Assessment = Clean(request.Assessment);
        template.Plan = Clean(request.Plan);
        template.FollowUpAdvice = Clean(request.FollowUpAdvice);
        template.FollowUpDays = request.FollowUpDays;
        template.CommonComplaints = (request.CommonComplaints ?? []).Select(c => c.Trim()).Where(c => c.Length > 0).Distinct().ToList();
        template.DefaultServiceItemIds = services.Value;
        template.DisplayOrder = request.DisplayOrder;
        template.IsActive = request.IsActive;
        template.Medications.Clear();
        var sequence = 0;
        foreach (var m in request.Medications ?? [])
        {
            template.Medications.Add(new TemplateMedication
            {
                TemplateId = template.Id,
                Sequence = ++sequence,
                DrugItemId = m.DrugItemId,
                DrugName = m.DrugName.Trim(),
                GenericName = Clean(m.GenericName),
                Strength = Clean(m.Strength),
                DosageForm = Clean(m.DosageForm),
                Route = Clean(m.Route),
                Dose = m.Dose.Trim(),
                Frequency = m.Frequency,
                CustomFrequency = m.Frequency == DoseFrequency.Custom ? Clean(m.CustomFrequency) : null,
                DurationDays = m.DurationDays,
                Instructions = Clean(m.Instructions),
            });
        }

        await db.SaveChangesAsync(ct);
        var dto = await ToDtoAsync(template, db, ct);
        return id is null ? TypedResults.Created($"/api/v1/emr/templates/{template.Id}", dto) : TypedResults.Ok(dto);
    }

    // ------------------------------------------------------------------ order sets

    private static async Task<IResult> ListOrderSets(string? specialtyCode, bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var sets = db.Set<OrderSet>().AsNoTracking();
        if (includeInactive != true)
        {
            sets = sets.Where(s => s.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(specialtyCode))
        {
            var code = specialtyCode.Trim().ToUpperInvariant();
            sets = sets.Where(s => s.SpecialtyCode == null || s.SpecialtyCode == code);
        }

        var rows = await sets.OrderBy(s => s.Name).ToListAsync(ct);
        var services = await ServicesAsync(db, rows.SelectMany(s => s.ServiceItemIds), ct);
        return TypedResults.Ok(rows.Select(s => ToDto(s, services)).ToList());
    }

    private static async Task<IResult> SaveOrderSet(Guid? id, SaveOrderSetRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<OrderSet>().AnyAsync(s => s.Code == code && s.Id != id, ct))
        {
            return Error.Conflict("order_set.code_in_use", $"Order set code {code} is already used.").ToProblem();
        }

        var services = await ValidServiceIdsAsync(db, request.ServiceItemIds, ct);
        if (services.IsFailure)
        {
            return services.Error!.ToProblem();
        }

        OrderSet set;
        if (id is { } existingId)
        {
            var found = await db.Set<OrderSet>().FirstOrDefaultAsync(s => s.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(OrderSet), existingId).ToProblem();
            }

            set = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(set, version);
            }
        }
        else
        {
            set = new OrderSet();
            db.Add(set);
        }

        set.Code = code;
        set.Name = request.Name.Trim();
        set.SpecialtyCode = request.SpecialtyCode?.Trim().ToUpperInvariant();
        set.Description = Clean(request.Description);
        set.ServiceItemIds = services.Value;
        set.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        var dto = ToDto(set, await ServicesAsync(db, set.ServiceItemIds, ct));
        return id is null ? TypedResults.Created($"/api/v1/clinical/order-sets/{set.Id}", dto) : TypedResults.Ok(dto);
    }

    // ------------------------------------------------------------------ favourites

    private static async Task<IResult> ListFavourites(FavouriteKind? kind, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.PractitionerId is not { } practitionerId)
        {
            return TypedResults.Ok(Array.Empty<FavouriteDto>());
        }

        var favourites = db.Set<PractitionerFavourite>().AsNoTracking().Where(f => f.PractitionerId == practitionerId);
        if (kind is not null)
        {
            favourites = favourites.Where(f => f.Kind == kind);
        }

        return TypedResults.Ok((await favourites.OrderBy(f => f.DisplayOrder).ThenBy(f => f.Label).ToListAsync(ct)).Select(ToDto).ToList());
    }

    private static async Task<IResult> AddFavourite(SaveFavouriteRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.PractitionerId is not { } practitionerId)
        {
            return Error.Rule("practitioner.required", "Favourites belong to a doctor; link your user account to a practitioner.").ToProblem();
        }

        if (request.ServiceItemId is { } serviceId && !await db.Set<ServiceItem>().AnyAsync(s => s.Id == serviceId && s.IsActive, ct))
        {
            return Error.Field("serviceItemId", "Choose an active service.").ToProblem();
        }

        var favourite = new PractitionerFavourite
        {
            PractitionerId = practitionerId,
            Kind = request.Kind,
            Label = request.Label.Trim(),
            ServiceItemId = request.Kind == FavouriteKind.Order ? request.ServiceItemId : null,
            DrugItemId = request.Kind == FavouriteKind.Drug ? request.DrugItemId : null,
            GenericName = Clean(request.GenericName),
            Strength = Clean(request.Strength),
            DosageForm = Clean(request.DosageForm),
            Route = Clean(request.Route),
            Dose = Clean(request.Dose),
            Frequency = request.Frequency,
            CustomFrequency = Clean(request.CustomFrequency),
            DurationDays = request.DurationDays,
            Instructions = Clean(request.Instructions),
            IsControlledDrug = request.IsControlledDrug,
            DisplayOrder = request.DisplayOrder,
        };
        db.Add(favourite);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created("/api/v1/emr/favourites", ToDto(favourite));
    }

    private static async Task<IResult> RemoveFavourite(Guid id, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var favourite = await db.Set<PractitionerFavourite>().FirstOrDefaultAsync(f => f.Id == id && f.PractitionerId == user.PractitionerId, ct);
        if (favourite is null)
        {
            return Error.NotFound("Favourite", id).ToProblem();
        }

        db.Remove(favourite);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<Result<List<Guid>>> ValidServiceIdsAsync(HospitalDbContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        var found = await db.Set<ServiceItem>().Where(s => distinct.Contains(s.Id) && s.IsActive).Select(s => s.Id).ToListAsync(ct);
        return found.Count == distinct.Count
            ? Result<List<Guid>>.Ok(distinct)
            : Error.Field("serviceItemIds", "One or more services are unknown or inactive.");
    }

    private static async Task<Dictionary<Guid, TemplateServiceDto>> ServicesAsync(HospitalDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<ServiceItem>().AsNoTracking().Where(s => list.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => new TemplateServiceDto(s.Id, s.Code, s.Name, s.Category), ct);
    }

    private static async Task<TemplateDto> ToDtoAsync(ClinicalTemplate t, HospitalDbContext db, CancellationToken ct)
    {
        var services = await ServicesAsync(db, t.DefaultServiceItemIds, ct);
        return new TemplateDto(t.Id, t.Code, t.Name, t.SpecialtyCode, t.OwnerPractitionerId, t.ChiefComplaint, t.HistoryOfPresentIllness, t.Examination,
            t.Assessment, t.Plan, t.FollowUpAdvice, t.FollowUpDays, t.CommonComplaints,
            t.DefaultServiceItemIds.Where(services.ContainsKey).Select(id => services[id]).ToList(),
            t.Medications.OrderBy(m => m.Sequence).Select(m => new TemplateMedicationDto(m.Id, m.DrugItemId, m.DrugName, m.GenericName, m.Strength, m.DosageForm,
                m.Route, m.Dose, m.Frequency, m.CustomFrequency, m.DurationDays, m.Instructions)).ToList(),
            t.DisplayOrder, t.IsActive, t.Version);
    }

    private static OrderSetDto ToDto(OrderSet s, Dictionary<Guid, TemplateServiceDto> services) => new(
        s.Id, s.Code, s.Name, s.SpecialtyCode, s.Description, s.ServiceItemIds.Where(services.ContainsKey).Select(id => services[id]).ToList(), s.IsActive, s.Version);

    private static FavouriteDto ToDto(PractitionerFavourite f) => new(
        f.Id, f.Kind, f.Label, f.ServiceItemId, f.DrugItemId, f.GenericName, f.Strength, f.DosageForm, f.Route, f.Dose, f.Frequency, f.CustomFrequency,
        f.DurationDays, f.Instructions, f.IsControlledDrug, f.DisplayOrder);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
