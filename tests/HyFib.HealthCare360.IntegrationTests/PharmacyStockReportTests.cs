using System.Text.Json;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Reports;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stock;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A pharmacy store's stock reports (PH-04) on real stock: medicines received on goods receipts with chosen batches and
/// expiry dates, then the stock of each store, the reorder report, near-expiry stock inside the chosen window and expired
/// stock, as the pharmacist reads them through the API. The stores and medicines are the test's own, so no other test's
/// stock reaches the reports read here.
/// </summary>
public sealed class PharmacyStockReportTests(HospitalServer server)
{
    private const string Reports = "/api/v1/inventory/reports";

    [Fact]
    public async Task A_pharmacy_stores_stock_reorder_near_expiry_and_expired_reports_show_its_own_batches()
    {
        // The demonstration seed has no store keeper account. The hospital administrator holds the store keeper's and the
        // purchase approver's permissions and plays both parts; the pharmacist reads the reports.
        var storeKeeper = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var today = await HospitalDayAsync();
        var suffix = Letters(6);

        var store = await StoreAsync(storeKeeper, $"PH{suffix}", $"Ward pharmacy {suffix}");
        var otherStore = await StoreAsync(storeKeeper, $"PX{suffix}", $"Day-care pharmacy {suffix}");

        // Four medicines, each bought in packs of 10. Adrenaline is vital, ceftriaxone and amoxicillin essential, the
        // vitamin desirable.
        var adrenaline = await MedicineAsync(storeKeeper, $"Adrenaline 1 mg/ml injection {suffix}", VedClass.Vital, "AMP", "BOX", reorderLevel: 50, reorderQuantity: 100, maximum: 200);
        var ceftriaxone = await MedicineAsync(storeKeeper, $"Ceftriaxone 1 g injection {suffix}", VedClass.Essential, "VIAL", "BOX", reorderLevel: 40, reorderQuantity: 50, maximum: 0);
        var amoxicillin = await MedicineAsync(storeKeeper, $"Amoxicillin 500 mg capsule {suffix}", VedClass.Essential, "CAP", "STRIP", reorderLevel: 20, reorderQuantity: 50, maximum: 300);
        var vitamin = await MedicineAsync(storeKeeper, $"Vitamin B complex tablet {suffix}", VedClass.Desirable, "TAB", "STRIP", reorderLevel: 100, reorderQuantity: 200, maximum: 500);
        Guid[] ours = [adrenaline, ceftriaxone, amoxicillin, vitamin];

        // Two months ago the pharmacy received short-dated stock, accepted as such: vitamins that have since expired, and
        // some that expire today, and ceftriaxone that expires in 20 days.
        var vendor = (await storeKeeper.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD");
        await ReceiveAsync(storeKeeper, vendor.Id, store, today.AddDays(-60),
            Line(vitamin, $"VX{suffix}", today.AddDays(-5), packs: 6, rate: 20m, mrp: 30m),
            Line(vitamin, $"VT{suffix}", today, packs: 3, rate: 20m, mrp: 30m),
            Line(ceftriaxone, $"CN{suffix}", today.AddDays(20), packs: 2, rate: 600m, mrp: 800m));

        // Today it receives fresh adrenaline, amoxicillin and vitamins, and ceftriaxone expiring in 60 and 75 days.
        await ReceiveAsync(storeKeeper, vendor.Id, store, today,
            Line(adrenaline, $"AF{suffix}", today.AddDays(400), packs: 3, rate: 500m, mrp: 700m),
            Line(amoxicillin, $"MF{suffix}", today.AddDays(500), packs: 10, rate: 50m, mrp: 80m),
            Line(vitamin, $"VF{suffix}", today.AddDays(300), packs: 5, rate: 20m, mrp: 30m),
            Line(ceftriaxone, $"CE{suffix}", today.AddDays(60), packs: 1, rate: 600m, mrp: 800m),
            Line(ceftriaxone, $"CL{suffix}", today.AddDays(75), packs: 1, rate: 600m, mrp: 800m));

        // ------------------------------------------------------------------ stock per store

        // The store's stock, item by item, in base units: expired stock is on hand but not available, and an item is below
        // its reorder level when what is available is at or under it.
        var stock = (await pharmacist.GetAsync<PagedResult<StockSummaryRow>>($"/api/v1/inventory/stock?storeId={store}&pageSize=200"))
            .Items.Where(r => ours.Contains(r.ItemId)).ToDictionary(r => r.ItemId);
        stock.Count.ShouldBe(4);
        Position(stock[adrenaline]).ShouldBe((30m, 0m, 30m, 0m, today.AddDays(400), 1500m, true));
        Position(stock[ceftriaxone]).ShouldBe((40m, 0m, 40m, 0m, today.AddDays(20), 2400m, true), "at the level counts as below it");
        Position(stock[amoxicillin]).ShouldBe((100m, 0m, 100m, 0m, today.AddDays(500), 500m, false));
        Position(stock[vitamin]).ShouldBe((140m, 0m, 80m, 60m, today.AddDays(-5), 280m, true), "the expired 60 tablets are on hand but not available");

        // The other store holds none of it.
        (await pharmacist.GetAsync<PagedResult<StockSummaryRow>>($"/api/v1/inventory/stock?storeId={otherStore}&pageSize=200"))
            .Items.ShouldNotContain(r => ours.Contains(r.ItemId));

        // ------------------------------------------------------------------ near-expiry and expired stock

        // Near-expiry inside a 60-day window: the batches that expire from today up to the 60th day, soonest first. The
        // batch expiring on day 75, the fresh stock and the batch already expired are not in it.
        var nearExpiry = await pharmacist.GetAsync<ExpiryReport>($"{Reports}/near-expiry?storeId={store}&days=60");
        nearExpiry.AsOf.ShouldBe(today);
        nearExpiry.WithinDays.ShouldBe(60);
        nearExpiry.Batches.Select(b => (b.BatchNumber, b.ItemId, b.ExpiryDate, b.DaysToExpiry, b.Quantity)).ToArray().ShouldBe([
            ($"VT{suffix}", vitamin, today, 0, 30m),
            ($"CN{suffix}", ceftriaxone, today.AddDays(20), 20, 20m),
            ($"CE{suffix}", ceftriaxone, today.AddDays(60), 60, 10m),
        ]);
        nearExpiry.Batches.ShouldAllBe(b => b.StoreId == store);
        nearExpiry.Batches.Select(b => (b.ValueAtCost, b.ValueAtMrp)).ToArray().ShouldBe([(60m, 90m), (1200m, 1600m), (600m, 800m)]);
        (nearExpiry.BatchCount, nearExpiry.ValueAtCost, nearExpiry.ValueAtMrp).ShouldBe((3, 1860m, 2490m));

        // The default window is 90 days: it takes the batch expiring on day 75 too. The narrowest window is a day.
        var nearExpiryDefault = await pharmacist.GetAsync<ExpiryReport>($"{Reports}/near-expiry?storeId={store}");
        nearExpiryDefault.WithinDays.ShouldBe(InventoryReportService.DefaultNearExpiryDays);
        nearExpiryDefault.Batches.Select(b => b.BatchNumber).ShouldBe([$"VT{suffix}", $"CN{suffix}", $"CE{suffix}", $"CL{suffix}"]);
        var nearExpiryNarrowest = await pharmacist.GetAsync<ExpiryReport>($"{Reports}/near-expiry?storeId={store}&days=0");
        nearExpiryNarrowest.WithinDays.ShouldBe(1);
        nearExpiryNarrowest.Batches.Select(b => b.BatchNumber).ShouldBe([$"VT{suffix}"]);

        // Expired: the batch whose expiry day has passed, still holding stock. One that expires today is still usable.
        var expired = await pharmacist.GetAsync<ExpiryReport>($"{Reports}/expired?storeId={store}");
        var expiredBatch = expired.Batches.ShouldHaveSingleItem();
        (expiredBatch.BatchNumber, expiredBatch.ItemId, expiredBatch.ExpiryDate, expiredBatch.DaysToExpiry, expiredBatch.Quantity)
            .ShouldBe(($"VX{suffix}", vitamin, today.AddDays(-5), -5, 60m));
        (expired.BatchCount, expired.ValueAtCost, expired.ValueAtMrp).ShouldBe((1, 120m, 180m));

        // The other store has nothing near expiry or expired of ours.
        (await pharmacist.GetAsync<ExpiryReport>($"{Reports}/near-expiry?storeId={otherStore}&days=730")).Batches.ShouldNotContain(b => ours.Contains(b.ItemId));
        (await pharmacist.GetAsync<ExpiryReport>($"{Reports}/expired?storeId={otherStore}")).Batches.ShouldNotContain(b => ours.Contains(b.ItemId));

        // ------------------------------------------------------------------ reorder

        // Reorder: adrenaline (30 available) and the vitamin (80: the expired batch does not count) are under their levels,
        // ceftriaxone (40) is at its level, amoxicillin (100) is above its level. Each is topped up to its maximum (with no
        // maximum, ceftriaxone to its level plus its reorder quantity) in whole packs; vital first, then essential, then
        // desirable.
        var reorder = await ReorderAsync(pharmacist, store, ours);
        reorder.Select(r => (r.ItemId, r.Available, r.OnOrder, r.SuggestedQuantity, r.SuggestedPurchaseUnits, r.LastUnitCost)).ToArray().ShouldBe([
            (adrenaline, 30m, 0m, 170m, 17m, (decimal?)50m),
            (ceftriaxone, 40m, 0m, 50m, 5m, (decimal?)60m),
            (vitamin, 80m, 0m, 420m, 42m, (decimal?)2m),
        ]);

        // The store keeper orders ceftriaxone. A submitted order is not yet on order; once approved, its pack of 10 counts,
        // and 50 is above the level, so ceftriaxone leaves the report.
        var order = await storeKeeper.PostAsync<DocumentCreated>("/api/v1/purchase/orders", new SavePurchaseOrderRequest(
            vendor.Id, store, null, today.AddDays(7), null, "Ceftriaxone for the ward", [new PurchaseOrderLineInput(ceftriaxone, 1m, 0m, 600m, 0m, null)], null));
        await storeKeeper.PostAsync($"/api/v1/purchase/orders/{order.Id}/submit");
        (await ReorderAsync(pharmacist, store, ours)).Select(r => r.ItemId).ShouldBe([adrenaline, ceftriaxone, vitamin]);

        await storeKeeper.PostAsync($"/api/v1/purchase/orders/{order.Id}/approve");
        (await ReorderAsync(pharmacist, store, ours)).Select(r => r.ItemId).ShouldBe([adrenaline, vitamin]);

        // In the other store, which holds none of them, all four are due at their full top-up; the order is for the first
        // store, so it does not count there.
        (await ReorderAsync(pharmacist, otherStore, ours)).Select(r => (r.ItemId, r.Available, r.OnOrder, r.SuggestedQuantity)).ToArray().ShouldBe([
            (adrenaline, 0m, 0m, 200m),
            (amoxicillin, 0m, 0m, 300m),
            (ceftriaxone, 0m, 0m, 90m),
            (vitamin, 0m, 0m, 500m),
        ]);

        // One click turns the store's report into a requisition, for the suggested packs.
        var requisition = await storeKeeper.PostAsync<DocumentCreated>($"/api/v1/purchase/requisitions/from-reorder?storeId={store}");
        var lines = (await storeKeeper.GetAsync<RequisitionDetail>($"/api/v1/purchase/requisitions/{requisition.Id}")).Lines;
        lines.Where(l => ours.Contains(l.ItemId)).Select(l => (l.ItemId, l.Quantity)).ToArray().ShouldBe([(adrenaline, 17m), (vitamin, 42m)], ignoreOrder: true);
    }

    // ---------------------------------------------------------------------- steps

    /// <summary>Store keeper: opens a pharmacy store of the signed-in facility.</summary>
    private static async Task<Guid> StoreAsync(ApiClient storeKeeper, string code, string name) =>
        (await storeKeeper.PostAsync<JsonElement>("/api/v1/inventory/stores", new SaveStoreRequest(code, name, StoreType.Pharmacy, null, null, null, true, null)))
        .GetProperty("id").GetGuid();

    /// <summary>Store keeper: adds a Schedule H medicine, counted in its base unit and bought in packs of 10.</summary>
    private static async Task<Guid> MedicineAsync(
        ApiClient storeKeeper, string name, VedClass ved, string baseUnit, string purchaseUnit, decimal reorderLevel, decimal reorderQuantity, decimal maximum) =>
        (await storeKeeper.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: name, GenericName: null, BrandName: null, Type: ItemType.Drug, CategoryId: null, Strength: null,
            DosageForm: null, PackSize: $"{purchaseUnit} of 10", BaseUnit: baseUnit, PurchaseUnit: purchaseUnit, PurchaseUnitFactor: 10, HsnCode: null,
            GstRate: 5m, Schedule: DrugSchedule.H, IsControlled: false, IsColdChain: false, VedClass: ved, ReorderLevel: reorderLevel,
            ReorderQuantity: reorderQuantity, MinimumStock: 0, MaximumStock: maximum, Manufacturer: null, Barcode: null, IsActive: true, Version: null)))
        .GetProperty("id").GetGuid();

    /// <summary>A received line: packs of 10 at a rate and MRP per pack; short-dated stock is accepted knowingly.</summary>
    private static GoodsReceiptLineInput Line(Guid itemId, string batch, DateOnly expiry, decimal packs, decimal rate, decimal mrp) =>
        new(itemId, null, batch, null, expiry, packs, 0m, rate, mrp, 0m, null, AcceptShortExpiry: true);

    /// <summary>Store keeper: receives a vendor invoice into the store on a day, and posts it to stock.</summary>
    private static async Task ReceiveAsync(ApiClient storeKeeper, Guid vendorId, Guid storeId, DateOnly receivedOn, params GoodsReceiptLineInput[] lines)
    {
        var receipt = await storeKeeper.PostAsync<GoodsReceiptCreated>("/api/v1/purchase/grns", new CreateGoodsReceiptRequest(
            vendorId, storeId, null, $"INV-{Letters(10)}", receivedOn, receivedOn, null, lines, Post: true));
        receipt.Status.ShouldBe(GoodsReceiptStatus.Posted);
    }

    /// <summary>On hand, reserved, available, expired, nearest expiry, value at cost and the below-reorder flag of a stock row.</summary>
    private static (decimal, decimal, decimal, decimal, DateOnly, decimal, bool) Position(StockSummaryRow row) =>
        (row.OnHand, row.Reserved, row.Available, row.ExpiredQuantity, row.NearestExpiry.ShouldNotBeNull(), row.ValueAtCost, row.BelowReorder);

    /// <summary>The store's reorder report, cut down to the test's own medicines (every active item has a row in a store without it).</summary>
    private static async Task<List<ReorderRow>> ReorderAsync(ApiClient pharmacist, Guid storeId, Guid[] ours) =>
        (await pharmacist.GetAsync<List<ReorderRow>>($"{Reports}/reorder?storeId={storeId}")).Where(r => ours.Contains(r.ItemId)).ToList();

    /// <summary>
    /// Today in the hospital's calendar (IST). Expiry is counted in days, so the test must not straddle midnight: started
    /// in the last two minutes of a day, it waits for the next.
    /// </summary>
    private async Task<DateOnly> HospitalDayAsync()
    {
        var clock = server.Services.GetRequiredService<TimeProvider>();
        var untilMidnight = HospitalClock.StartOfDayUtc(HospitalClock.Today(clock).AddDays(1)) - clock.GetUtcNow();
        if (untilMidnight < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(untilMidnight + TimeSpan.FromSeconds(1), clock, TestContext.Current.CancellationToken);
        }

        return HospitalClock.Today(clock);
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);
}
