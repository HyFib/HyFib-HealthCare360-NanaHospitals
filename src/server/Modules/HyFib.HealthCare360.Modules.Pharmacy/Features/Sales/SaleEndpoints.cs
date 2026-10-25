using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;

/// <summary>/api/v1/pharmacy — counter stock, GST invoices, counter sales, dues, cancellation and returns (PH-02, PH-04).</summary>
internal static class SaleEndpoints
{
    private static readonly string[] SaleReaders = [PharmacyPermissions.Sales, PharmacyPermissions.Returns];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pharmacy").WithTags("Pharmacy");

        group.MapGet("/stock", Stock).RequireAnyPermission(PharmacyPermissions.CounterStaff);
        group.MapGet("/stock/batches", async (Guid storeId, Guid itemId, StockService stock, CancellationToken ct) =>
                TypedResults.Ok(await stock.GetBatchesAsync(storeId, itemId, includeEmpty: false, ct)))
            .RequireAnyPermission(PharmacyPermissions.CounterStaff);

        group.MapGet("/sales", List).RequireAnyPermission(SaleReaders);
        group.MapGet("/sales/{id:guid}", Get).RequireAnyPermission(SaleReaders);
        group.MapGet("/sales/{id:guid}/returns", Returns).RequireAnyPermission(SaleReaders);
        group.MapPost("/sales/counter", async (CounterSaleRequest r, DispensingService dispensing, CancellationToken ct) =>
                (await dispensing.SellOverCounterAsync(r, ct)).ToCreated(s => $"/api/v1/pharmacy/sales/{s.Id}"))
            .RequirePermission(PharmacyPermissions.Sales).WithValidation<CounterSaleRequest>();
        group.MapPost("/sales/{id:guid}/payments", async (Guid id, CollectPaymentRequest r, SaleAdjustmentService sales, CancellationToken ct) =>
                (await sales.CollectAsync(id, r, ct)).ToNoContent())
            .RequirePermission(PharmacyPermissions.Sales).WithValidation<CollectPaymentRequest>();
        group.MapPost("/sales/{id:guid}/cancel", async (Guid id, PharmacyReasonRequest r, SaleAdjustmentService sales, CancellationToken ct) =>
                (await sales.CancelAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(PharmacyPermissions.Returns).WithValidation<PharmacyReasonRequest>();
        group.MapPost("/sales/{id:guid}/returns", async (Guid id, SaleReturnRequest r, SaleAdjustmentService sales, CancellationToken ct) =>
                (await sales.ReturnAsync(id, r, ct)).ToCreated(ret => $"/api/v1/pharmacy/sales/{id}/returns"))
            .RequirePermission(PharmacyPermissions.Returns).WithValidation<SaleReturnRequest>();
    }

    /// <summary>
    /// Counter picker: sellable stock of matching items in a pharmacy store, with MRP and the prescription flag. A
    /// scanned pharmacy tag finds its item and says which of the store's batches it names.
    /// </summary>
    private static async Task<IResult> Stock(
        Guid storeId, string? search, bool? inStockOnly, int? take, HospitalDbContext db, StockService stock, TimeProvider time, CancellationToken ct)
    {
        var items = await db.Set<Item>().AsNoTracking()
            .Where(i => i.IsActive)
            .Search(search)
            .OrderBy(i => i.Name)
            .Take(Math.Clamp(take ?? 30, 1, 100))
            .Select(i => new { i.Id, i.Code, i.Barcode, i.Name, i.GenericName, i.Strength, i.Schedule, i.IsControlled, i.IsColdChain, i.BaseUnit })
            .ToListAsync(ct);
        var availability = await InventoryQueries.AvailabilityAsync(db, storeId, items.Select(i => i.Id).ToList(), time.Today(), ct);

        var rows = items.Select(i =>
            {
                var stock = availability.GetValueOrDefault(i.Id);
                return new PharmacyStockRow(i.Id, i.Code, i.Name, i.GenericName, i.Strength, i.Schedule, i.IsControlled,
                    Item.NeedsPrescription(i.Schedule, i.IsControlled), i.IsColdChain, i.BaseUnit, stock?.Available ?? 0, stock?.CurrentMrp, stock?.NearestExpiry,
                    i.Barcode);
            })
            .Where(r => inStockOnly != true || r.Available > 0)
            .ToList();
        // Only the tag's own item: not an item whose barcode merely looks like a tag.
        if (BatchTag.TryParse(search, out var taggedCode, out var batchNumber)
            && rows.Count == 1
            && string.Equals(rows[0].Code, taggedCode, StringComparison.OrdinalIgnoreCase))
        {
            var batches = await stock.GetBatchesAsync(storeId, rows[0].ItemId, includeEmpty: true, ct);
            rows[0] = rows[0] with { Scanned = Scanned(batchNumber, batches) };
        }

        return TypedResults.Ok(rows);
    }

    /// <summary>The store's batch a tag names: the usable one expiring first, or why none can be handed out.</summary>
    internal static ScannedBatch Scanned(string batchNumber, IReadOnlyList<BatchStock> batches)
    {
        var named = batches.Where(b => BatchTag.Matches(b.BatchNumber, batchNumber)).ToList();
        var usable = named.Where(b => b.Available > 0 && !b.IsExpired && !b.IsBlocked).OrderBy(b => b.ExpiryDate ?? DateOnly.MaxValue).FirstOrDefault();
        if (usable is not null)
        {
            return new ScannedBatch(usable.BatchNumber, usable.BatchId, usable.ExpiryDate, usable.Mrp, usable.Available, null);
        }

        var problem = named.Count == 0 ? $"Batch {batchNumber} is not in this store."
            : named.Any(b => b.IsBlocked && b.OnHand > 0) ? $"Batch {batchNumber} is blocked: {named.First(b => b.IsBlocked).BlockReason ?? "see the stock screen"}."
            : named.Any(b => b.IsExpired && b.OnHand > 0) ? $"Batch {batchNumber} has expired."
            : $"Nothing of batch {batchNumber} is left to sell here.";
        var latest = named.OrderByDescending(b => b.ExpiryDate ?? DateOnly.MaxValue).FirstOrDefault();
        return new ScannedBatch(latest?.BatchNumber ?? batchNumber, null, latest?.ExpiryDate, latest?.Mrp, 0, problem);
    }

    /// <summary>Invoices for a date range (local dates, default today); search by invoice number, customer or UHID.</summary>
    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        DateOnly? from,
        DateOnly? to,
        SaleStatus? status,
        SaleType? type,
        Guid? storeId,
        bool? withDues,
        HospitalDbContext db,
        IFieldProtector protector,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = time.Today();
        var (start, end) = HospitalClock.Range(from ?? today, to ?? from ?? today);
        var sales = db.Set<PharmacySale>().AsNoTracking().Where(s => s.SoldAt >= start && s.SoldAt < end);
        if (status is not null)
        {
            sales = sales.Where(s => s.Status == status);
        }

        if (type is not null)
        {
            sales = sales.Where(s => s.Type == type);
        }

        if (storeId is not null)
        {
            sales = sales.Where(s => s.StoreId == storeId);
        }

        if (withDues == true)
        {
            sales = sales.Where(s => s.DueAmount > 0);
        }

        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            var like = term.ToLikePattern();
            var patients = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            sales = sales.Where(s => s.InvoiceNumber == upper || (s.CustomerName != null && EF.Functions.ILike(s.CustomerName, like, QueryExtensions.LikeEscape))
                                     || (s.PatientId != null && patients.Contains(s.PatientId.Value)));
        }

        var result = await sales.OrderByDescending(s => s.SoldAt)
            .Select(s => new PharmacySaleListItem(
                s.Id, s.InvoiceNumber, s.Type, s.SoldAt, s.StoreId, s.PatientId, s.CustomerName, s.PrescriptionNumber, s.NetAmount, s.PaidAmount,
                s.DueAmount, s.ReturnedAmount, s.Status))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> Get(
        Guid id, HospitalDbContext db, PatientAccessGuard guard, PatientContactAccess contacts, IAuditLogger audit, CancellationToken ct)
    {
        var sale = await db.Set<PharmacySale>().AsNoTracking().Include(s => s.Items).Include(s => s.Payments).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sale is null)
        {
            return Error.NotFound(nameof(PharmacySale), id).ToProblem();
        }

        string? uhid = null;
        if (sale.PatientId is { } patientId)
        {
            var access = await guard.EnsureCanOpenAsync(patientId, ct);
            if (access.IsFailure)
            {
                return access.Error!.ToProblem();
            }

            await audit.PatientRecordAccessedAsync(patientId, $"Viewed pharmacy invoice {sale.InvoiceNumber}", ct);
            uhid = await db.Set<Patient>().Where(p => p.Id == patientId).Select(p => p.Uhid).FirstOrDefaultAsync(ct);
        }

        var storeName = await db.Set<Store>().Where(s => s.Id == sale.StoreId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        var soldBy = await db.Set<User>().Where(u => u.Id == sale.SoldByUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        return TypedResults.Ok(new PharmacySaleDetail(
            sale.Id, sale.InvoiceNumber, sale.Type, sale.StoreId, storeName, sale.PatientId, uhid, sale.CustomerName,
            PatientContacts.Phone(sale.CustomerPhone, await contacts.FullAsync(ct)),
            sale.PrescriptionNumber, sale.PrescriberName, sale.PrescriberRegistrationNumber, sale.SoldAt, soldBy, sale.CounsellingNotes,
            sale.DiscountPercent, sale.GrossAmount, sale.DiscountAmount, sale.TaxableAmount, sale.CgstAmount, sale.SgstAmount, sale.IgstAmount,
            sale.BuyerGstin, sale.BuyerLegalName, sale.PlaceOfSupply, sale.RoundOff,
            sale.NetAmount, sale.PaidAmount, sale.DueAmount, sale.ReturnedAmount, sale.RefundedAmount, sale.Status, sale.CancelReason,
            sale.Items.OrderBy(i => i.ItemName).Select(i => new PharmacySaleItemDto(
                i.Id, i.ItemId, i.ItemName, i.GenericName, i.HsnCode, i.Schedule, i.BatchNumber, i.ExpiryDate, i.Quantity, i.UnitMrp, i.GstRate,
                i.GrossAmount, i.DiscountAmount, i.TaxableAmount, i.CgstAmount, i.SgstAmount, i.IgstAmount, i.NetAmount, i.ReturnedQuantity, i.IsSubstitution,
                i.PrescribedDrugName, i.SubstitutionReason)).ToList(),
            sale.Payments.OrderBy(p => p.ReceivedAt).Select(p => new PharmacyPaymentDto(p.Id, p.Mode, p.Amount, p.Reference, p.ReceivedAt, p.IsRefund)).ToList(),
            sale.Version));
    }

    private static async Task<IResult> Returns(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var returns = await db.Set<PharmacySaleReturn>().AsNoTracking().Include(r => r.Items)
            .Where(r => r.SaleId == id)
            .OrderBy(r => r.ReturnedAt)
            .ToListAsync(ct);
        return TypedResults.Ok(returns.Select(r => new SaleReturnDto(
            r.Id, r.ReturnNumber, r.ReturnedAt, r.Reason, r.RefundAmount, r.AdjustedAgainstDue, r.CashRefund, r.RefundMode,
            r.Items.Select(i => new SaleReturnItemDto(i.SaleItemId, i.ItemName, i.BatchNumber, i.Quantity, i.RefundAmount, i.Restocked)).ToList())).ToList());
    }
}
