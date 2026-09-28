using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;
using HyFib.HealthCare360.Modules.SupportServices.Services;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Dietary;

/// <summary>/api/v1/dietary — diet master and diet orders (DIET-01).</summary>
internal static class DietaryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/dietary").WithTags("Dietary");

        group.MapGet("/diet-types", ListDietTypes)
            .RequireAnyPermission(SupportServicesPermissions.DietOrdersRead, SupportServicesPermissions.DietOrdersManage, SupportServicesPermissions.DietMasters);
        group.MapPost("/diet-types", CreateDietType).RequirePermission(SupportServicesPermissions.DietMasters).WithValidation<SaveDietTypeRequest>();
        group.MapPut("/diet-types/{id:guid}", UpdateDietType).RequirePermission(SupportServicesPermissions.DietMasters).WithValidation<SaveDietTypeRequest>();

        group.MapGet("/orders", ListOrders).RequirePermission(SupportServicesPermissions.DietOrdersRead);
        group.MapGet("/orders/{id:guid}", async (Guid id, DietOrderService orders, CancellationToken ct) => (await orders.GetAsync(id, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.DietOrdersRead);
        group.MapGet("/admissions/{admissionId:guid}/orders", async (Guid admissionId, DietOrderService orders, HospitalDbContext db, CancellationToken ct) =>
                TypedResults.Ok(await orders.DtosAsync(db.Set<DietOrder>().AsNoTracking().Where(o => o.AdmissionId == admissionId), ct)))
            .RequirePermission(SupportServicesPermissions.DietOrdersRead);
        group.MapPost("/orders", async (CreateDietOrderRequest r, DietOrderService orders, CancellationToken ct) =>
                (await orders.CreateAsync(r, ct)).ToCreated(o => $"/api/v1/dietary/orders/{o.Id}"))
            .RequirePermission(SupportServicesPermissions.DietOrdersManage).WithValidation<CreateDietOrderRequest>();
        group.MapPost("/orders/{id:guid}/stop", async (Guid id, StopDietOrderRequest r, DietOrderService orders, CancellationToken ct) =>
                (await orders.StopAsync(id, r.Reason, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.DietOrdersManage).WithValidation<StopDietOrderRequest>();
        group.MapPost("/orders/{id:guid}/review", async (Guid id, DietOrderService orders, CancellationToken ct) => (await orders.ReviewAsync(id, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.DietOrdersManage);
    }

    private static async Task<IResult> ListDietTypes(bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var types = db.Set<DietType>().AsNoTracking();
        if (includeInactive != true)
        {
            types = types.Where(t => t.IsActive);
        }

        var rows = await (
                from t in types
                join s in db.Set<ServiceItem>() on t.ChargeServiceItemId equals s.Id into services
                from s in services.DefaultIfEmpty()
                orderby t.DisplayOrder, t.Name
                select new { Type = t, ServiceCode = s != null ? s.Code : null })
            .ToListAsync(ct);
        return TypedResults.Ok(rows.Select(r => ToDto(r.Type, r.ServiceCode)).ToList());
    }

    /// <summary>
    /// Active orders by default (the dietitian's round list); filter by ward, status or orders needing review. Orders
    /// come from the whole facility, so no single patient's record is opened here.
    /// </summary>
    private static async Task<IResult> ListOrders(
        DietOrderStatus? status, Guid? wardId, bool? needsReview, DietOrderService orders, HospitalDbContext db, CancellationToken ct)
    {
        var query = db.Set<DietOrder>().AsNoTracking().Where(o => o.Status == (status ?? DietOrderStatus.Active));
        if (needsReview == true)
        {
            query = query.Where(o => o.NeedsReview);
        }

        if (wardId is not null)
        {
            var inWard = db.Set<Admission>().Where(a => a.WardId == wardId).Select(a => a.Id);
            query = query.Where(o => inWard.Contains(o.AdmissionId));
        }

        return TypedResults.Ok(await orders.DtosAsync(query, ct));
    }

    private static async Task<IResult> CreateDietType(SaveDietTypeRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<DietType>().AnyAsync(t => t.Code == code, ct))
        {
            return Error.Conflict("diet.code_in_use", $"Diet code {code} is already in use.").ToProblem();
        }

        var problem = await CheckChargeServiceAsync(request.ChargeServiceItemId, db, ct);
        if (problem is not null)
        {
            return problem.ToProblem();
        }

        var dietType = new DietType();
        Apply(dietType, request, code);
        db.Add(dietType);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/dietary/diet-types/{dietType.Id}", ToDto(dietType, null));
    }

    private static async Task<IResult> UpdateDietType(Guid id, SaveDietTypeRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var dietType = await db.Set<DietType>().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (dietType is null)
        {
            return Error.NotFound("Diet type", id).ToProblem();
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<DietType>().AnyAsync(t => t.Code == code && t.Id != id, ct))
        {
            return Error.Conflict("diet.code_in_use", $"Diet code {code} is already in use.").ToProblem();
        }

        var problem = await CheckChargeServiceAsync(request.ChargeServiceItemId, db, ct);
        if (problem is not null)
        {
            return problem.ToProblem();
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(dietType, version);
        }

        Apply(dietType, request, code);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Error?> CheckChargeServiceAsync(Guid? serviceItemId, HospitalDbContext db, CancellationToken ct) =>
        serviceItemId is { } id && !await db.Set<ServiceItem>().AnyAsync(s => s.Id == id && s.IsActive, ct)
            ? Error.Field("chargeServiceItemId", "Choose an active service for diet charges.")
            : null;

    private static void Apply(DietType dietType, SaveDietTypeRequest request, string code)
    {
        dietType.Code = code;
        dietType.Name = request.Name.Trim();
        dietType.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        dietType.Category = request.Category;
        dietType.ChargeServiceItemId = request.ChargeServiceItemId;
        dietType.DefaultMealSlots = request.DefaultMealSlots;
        dietType.DisplayOrder = request.DisplayOrder;
        dietType.IsActive = request.IsActive;
    }

    private static DietTypeDto ToDto(DietType t, string? serviceCode) => new(
        t.Id, t.Code, t.Name, t.Description, t.Category, t.ChargeServiceItemId, serviceCode, t.DefaultMealSlots, MealSlots.Split(t.DefaultMealSlots),
        t.DisplayOrder, t.IsActive, t.Version);
}
