using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Services;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Canteen;

/// <summary>/api/v1/canteen — menu and pricing, stock for packaged items, POS bills, cancellation and the day-end report (CAN-01/02/04).</summary>
internal static class CanteenEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/canteen").WithTags("Canteen");

        group.MapGet("/menu", ListMenu).RequireAnyPermission(SupportServicesPermissions.CanteenSales, SupportServicesPermissions.CanteenMenu);
        group.MapPost("/menu", CreateMenuItem).RequirePermission(SupportServicesPermissions.CanteenMenu).WithValidation<SaveMenuItemRequest>();
        group.MapPut("/menu/{id:guid}", UpdateMenuItem).RequirePermission(SupportServicesPermissions.CanteenMenu).WithValidation<SaveMenuItemRequest>().RequireVersion();
        group.MapPost("/menu/{id:guid}/stock", async (Guid id, StockMovementRequest r, CanteenService canteen, CancellationToken ct) =>
                (await canteen.MoveStockAsync(id, r, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.CanteenMenu).WithValidation<StockMovementRequest>();

        group.MapPost("/sales", async (CreateSaleRequest r, CanteenService canteen, CancellationToken ct) =>
                (await canteen.CreateSaleAsync(r, ct)).ToCreated(s => $"/api/v1/canteen/sales/{s.Id}"))
            .RequirePermission(SupportServicesPermissions.CanteenSales).WithValidation<CreateSaleRequest>();
        group.MapGet("/sales", ListSales).RequireAnyPermission(SupportServicesPermissions.CanteenSales, SupportServicesPermissions.CanteenReports);
        group.MapGet("/sales/{id:guid}", GetSale).RequireAnyPermission(SupportServicesPermissions.CanteenSales, SupportServicesPermissions.CanteenReports);
        group.MapPost("/sales/{id:guid}/cancel", async (Guid id, CancelSaleRequest r, CanteenService canteen, CancellationToken ct) =>
                (await canteen.CancelSaleAsync(id, r.Reason, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.CanteenCancel).WithValidation<CancelSaleRequest>();

        group.MapGet("/reports/daily", async (DateOnly? date, CanteenService canteen, TimeProvider time, CancellationToken ct) =>
                TypedResults.Ok(await canteen.DailySummaryAsync(date ?? time.Today(), ct)))
            .RequirePermission(SupportServicesPermissions.CanteenReports);
    }

    private static async Task<IResult> ListMenu(MenuCategory? category, bool? availableOnly, HospitalDbContext db, CancellationToken ct)
    {
        var items = db.Set<MenuItem>().AsNoTracking();
        if (category is not null)
        {
            items = items.Where(m => m.Category == category);
        }

        if (availableOnly == true)
        {
            items = items.Where(m => m.IsAvailable);
        }

        var rows = await items.OrderBy(m => m.Category).ThenBy(m => m.DisplayOrder).ThenBy(m => m.Name).Take(500).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(CanteenService.ToDto).ToList());
    }

    private static async Task<IResult> CreateMenuItem(SaveMenuItemRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<MenuItem>().AnyAsync(m => m.Code == code, ct))
        {
            return Error.Conflict("canteen.code_in_use", $"Item code {code} is already in use.").ToProblem();
        }

        var item = new MenuItem { FacilityId = facilityId };
        Apply(item, request, code);
        db.Add(item);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/canteen/menu/{item.Id}", CanteenService.ToDto(item));
    }

    private static async Task<IResult> UpdateMenuItem(Guid id, SaveMenuItemRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var item = await db.Set<MenuItem>().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (item is null)
        {
            return Error.NotFound("Menu item", id).ToProblem();
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<MenuItem>().AnyAsync(m => m.Code == code && m.Id != id, ct))
        {
            return Error.Conflict("canteen.code_in_use", $"Item code {code} is already in use.").ToProblem();
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(item, version);
        }

        Apply(item, request, code);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListSales(
        [AsParameters] PageQuery query, DateOnly? date, CanteenSaleStatus? status, CanteenPaymentMode? paymentMode, string? employeeCode,
        HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var day = date ?? time.Today();
        var sales = db.Set<CanteenSale>().AsNoTracking().Where(s => s.BusinessDate == day);
        if (status is not null)
        {
            sales = sales.Where(s => s.Status == status);
        }

        if (paymentMode is not null)
        {
            sales = sales.Where(s => s.PaymentMode == paymentMode);
        }

        if (!string.IsNullOrWhiteSpace(employeeCode))
        {
            var code = StaffCreditRules.NormalizeEmployeeCode(employeeCode);
            sales = sales.Where(s => s.EmployeeCode == code);
        }

        if (page.SearchTerm is { } term)
        {
            var number = term.ToUpperInvariant();
            sales = sales.Where(s => s.BillNumber == number || s.BillNumber.EndsWith(number));
        }

        var result = await sales.OrderByDescending(s => s.SoldAt)
            .Select(s => new CanteenSaleListItem(s.Id, s.BillNumber, s.SoldAt, s.CustomerType, s.CustomerName, s.EmployeeCode, s.NetAmount, s.PaymentMode,
                s.Status, s.CashierName))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> GetSale(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var sale = await db.Set<CanteenSale>().AsNoTracking().Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id, ct);
        return sale is null ? Error.NotFound("Canteen bill", id).ToProblem() : TypedResults.Ok(CanteenService.ToDto(sale));
    }

    private static void Apply(MenuItem item, SaveMenuItemRequest request, string code)
    {
        item.Code = code;
        item.Name = request.Name.Trim();
        item.Category = request.Category;
        item.Price = Money.Round(request.Price);
        item.GstRate = request.GstRate;
        item.HsnSac = string.IsNullOrWhiteSpace(request.HsnSac) ? null : request.HsnSac.Trim();
        item.PriceIncludesGst = request.PriceIncludesGst;
        item.IsVeg = request.IsVeg;
        item.IsAvailable = request.IsAvailable;
        item.TrackStock = request.TrackStock;
        item.DisplayOrder = request.DisplayOrder;
        item.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
    }
}
