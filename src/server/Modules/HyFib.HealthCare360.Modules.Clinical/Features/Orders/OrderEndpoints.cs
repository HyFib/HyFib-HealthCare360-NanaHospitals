using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Orders;

/// <summary>/api/v1/clinical/orders — lab, imaging, cardiology and procedure orders (EMR-05).</summary>
internal static class OrderEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/clinical/orders").WithTags("Clinical Orders");

        group.MapPost("/", Place).RequirePermission(ClinicalPermissions.OrdersPlace).WithValidation<PlaceOrderRequest>();
        group.MapGet("/", List).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapGet("/{id:guid}", Get).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPost("/{id:guid}/cancel", Cancel).RequirePermission(ClinicalPermissions.OrdersPlace).WithValidation<CancelOrderRequest>();
        group.MapPost("/items/{itemId:guid}/cancel", CancelItem).RequirePermission(ClinicalPermissions.OrdersPlace).WithValidation<CancelOrderRequest>();
        group.MapPost("/items/{itemId:guid}/status", UpdateItemStatus).RequirePermission(ClinicalPermissions.OrdersPlace)
            .WithValidation<UpdateOrderItemStatusRequest>();
    }

    private static async Task<IResult> Place(PlaceOrderRequest request, ClinicalOrderService orders, CancellationToken ct) =>
        (await orders.PlaceAsync(request, ct)).ToCreated(o => $"/api/v1/clinical/orders/{o.Id}");

    /// <summary>Orders of one patient (every facility), optionally narrowed to an encounter, visit or admission.</summary>
    private static async Task<IResult> List(
        Guid patientId,
        [AsParameters] PageQuery query,
        Guid? encounterId,
        Guid? visitId,
        Guid? admissionId,
        ClinicalOrderStatus? status,
        ClinicalAccess access,
        HospitalDbContext db,
        CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Viewed clinical orders", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var orders = db.Set<ClinicalOrder>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Include(o => o.Items)
            .Where(o => o.PatientId == patientId);
        if (encounterId is not null)
        {
            orders = orders.Where(o => o.EncounterId == encounterId);
        }

        if (visitId is not null)
        {
            orders = orders.Where(o => o.VisitId == visitId);
        }

        if (admissionId is not null)
        {
            orders = orders.Where(o => o.AdmissionId == admissionId);
        }

        if (status is not null)
        {
            orders = orders.Where(o => o.Status == status);
        }

        var page = await orders.OrderByDescending(o => o.OrderedAt).ToPagedResultAsync(query.ToRequest(), ct);
        var names = await PrintQueries.PractitionerNamesAsync(db, page.Items.Select(o => o.OrderingPractitionerId), ct);
        return TypedResults.Ok(page.Map(o => ClinicalOrderService.ToDto(o, o.OrderingPractitionerId is { } p ? names.GetValueOrDefault(p) : null)));
    }

    private static async Task<IResult> Get(Guid id, ClinicalAccess access, HospitalDbContext db, ClinicalOrderService orders, CancellationToken ct)
    {
        var order = await db.Set<ClinicalOrder>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null)
        {
            return Error.NotFound("Order", id).ToProblem();
        }

        var opened = await access.OpenAsync(order.PatientId, "Viewed clinical order", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await orders.ToDtoAsync(order, ct));
    }

    private static async Task<IResult> Cancel(Guid id, CancelOrderRequest request, ClinicalOrderService orders, CancellationToken ct) =>
        (await orders.CancelAsync(id, request.Reason, ct)).ToOk();

    private static async Task<IResult> CancelItem(Guid itemId, CancelOrderRequest request, ClinicalOrderService orders, CancellationToken ct) =>
        (await orders.CancelItemAsync(itemId, request.Reason, ct)).ToOk();

    /// <summary>For services performed at the bedside or in clinic (procedures); diagnostics update their lines through reports.</summary>
    private static async Task<IResult> UpdateItemStatus(Guid itemId, UpdateOrderItemStatusRequest request, ClinicalOrderService orders, CancellationToken ct) =>
        (await orders.UpdateItemStatusAsync(itemId, request.Status, request.ResultReference, ct)).ToOk();
}
