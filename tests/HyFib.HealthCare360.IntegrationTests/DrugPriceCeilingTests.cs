using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;
using SalePayment = HyFib.HealthCare360.Modules.Pharmacy.Domain.SalePayment;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A scheduled medicine's NPPA ceiling price (DPCO 2013, MAKER-CHECKER.md slice 13), over the real API: a pack whose MRP is
/// above the ceiling is not received, a batch above it is not sold, a ceiling cut applies at once, and a ceiling raised or a
/// GST rate changed waits for someone who approves prices, the item keeping its pricing until then.
/// </summary>
public sealed class DrugPriceCeilingTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>₹2.50 a tablet with GST: ₹25.00 a strip of 10.</summary>
    private const decimal Ceiling = 2.5m;

    [Fact]
    public async Task A_pack_priced_above_the_ceiling_is_not_received_and_one_at_it_is()
    {
        var storeKeeper = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var itemId = await MedicineAsync(storeKeeper, Ceiling);
        var store = await StoreAsync(storeKeeper, "OPPH");

        using var above = await storeKeeper.SendRawAsync(HttpMethod.Post, GoodsReceipts.Path, await ReceiptAsync(storeKeeper, store, itemId, packMrp: 25.01m));
        (above.StatusCode, await CodeAsync(above)).ShouldBe((HttpStatusCode.UnprocessableEntity, "grn.mrp_above_ceiling"));

        await GoodsReceipts.IntoStockAsync(server, storeKeeper, await ReceiptAsync(storeKeeper, store, itemId, packMrp: 25m));
    }

    [Fact]
    public async Task A_ceiling_cut_applies_at_once_and_a_batch_priced_above_it_is_not_sold_nor_posted()
    {
        var storeKeeper = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var itemId = await MedicineAsync(storeKeeper, Ceiling);
        var store = await StoreAsync(storeKeeper, "OPPH");
        await GoodsReceipts.IntoStockAsync(server, storeKeeper, await ReceiptAsync(storeKeeper, store, itemId, packMrp: 25m));

        // A second receipt at the ceiling waits for its check while NPPA cuts the ceiling to ₹2 a tablet.
        var waiting = await storeKeeper.PostAsync<GoodsReceiptCreated>(GoodsReceipts.Path, await ReceiptAsync(storeKeeper, store, itemId, packMrp: 25m));

        // The cut is saved at once (no approval: it only tightens).
        using (var cut = await storeKeeper.SendRawAsync(HttpMethod.Put, Item(itemId), await EditAsync(storeKeeper, itemId, d => d with { CeilingPrice = 2m })))
        {
            cut.StatusCode.ShouldBe(HttpStatusCode.NoContent, await cut.Content.ReadAsStringAsync(Ct));
        }

        (await storeKeeper.GetAsync<ItemDetail>(Item(itemId))).CeilingPrice.ShouldBe(2m);

        // The tablets in stock were priced at ₹2.50: the counter may not sell them above the new ceiling.
        using var sold = await pharmacist.SendRawAsync(HttpMethod.Post, "/api/v1/pharmacy/sales/counter", new CounterSaleRequest(
            store, null, "Walk-in customer", null, new ExternalPrescription("Dr Outside", "KMC-1234", null, null), [new CounterSaleLine(itemId, 2m, null)], 0m,
            [new SalePayment(PaymentMode.Cash, 5m, null)], false));
        (sold.StatusCode, await CodeAsync(sold)).ShouldBe((HttpStatusCode.UnprocessableEntity, "pharmacy.price_above_ceiling"));

        // And the receipt that waited is checked against the ceiling as it is now when it is approved: nothing goes into stock.
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var request = await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{waiting.ApprovalId}");
        using var posted = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{waiting.ApprovalId}/approve", new ApproveRequest(null, request.Version));
        (posted.StatusCode, await CodeAsync(posted)).ShouldBe((HttpStatusCode.UnprocessableEntity, "grn.mrp_above_ceiling"));
    }

    [Fact]
    public async Task A_ceiling_raised_waits_for_someone_who_approves_prices_and_the_item_keeps_its_own_until_then()
    {
        var storeKeeper = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var itemId = await MedicineAsync(storeKeeper, Ceiling);
        var store = await StoreAsync(storeKeeper, "OPPH");

        // Raised, with the notification named: 202, the request saying in words what changes; the rest of the item is saved.
        var body = await EditAsync(storeKeeper, itemId, d => d with
        {
            CeilingPrice = 3m,
            CeilingNotification = "S.O. 1234(E) of 30-Mar-2026",
            PriceChangeReason = "NPPA revised the ceiling",
            ReorderLevel = 50,
        });
        using var response = await storeKeeper.SendRawAsync(HttpMethod.Put, Item(itemId), body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Put, Item(itemId));
        asked.ShouldSatisfyAllConditions(
            a => a.Kind.ShouldBe(ItemPriceApprovals.Kind),
            a => a.Reason.ShouldBe("NPPA revised the ceiling"),
            a => Detail(a, "Now").ShouldStartWith("Ceiling ₹2.50 a TAB (₹25.00 a STRIP of 10) with GST"),
            a => Detail(a, "Asked for").ShouldStartWith("Ceiling ₹3.00 a TAB (₹30.00 a STRIP of 10) with GST, S.O. 1234(E)"),
            a => Detail(a, "What it changes").ShouldContain("packs priced up to ₹3.00 a TAB are received and sold"));
        var waiting = await storeKeeper.GetAsync<ItemDetail>(Item(itemId));
        (waiting.CeilingPrice, waiting.ReorderLevel, waiting.PendingPricing.ShouldNotBeNull().ApprovalId).ShouldBe((Ceiling, 50m, asked.Id));

        // Until it is approved a pack at ₹30 is still above the ceiling.
        using (var above = await storeKeeper.SendRawAsync(HttpMethod.Post, GoodsReceipts.Path, await ReceiptAsync(storeKeeper, store, itemId, packMrp: 30m)))
        {
            (await CodeAsync(above)).ShouldBe("grn.mrp_above_ceiling");
        }

        // Neither the one who asked nor a pharmacist (who does not approve prices) decides it; the accountant does.
        (await RefusalAsync(storeKeeper, asked)).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        (await RefusalAsync(pharmacist, asked)).ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
        var approved = await MakerChecker.ApproveAsync(accountant, asked);
        approved.Outcome.ShouldNotBeNull().ShouldContain("was Ceiling ₹2.50");
        var after = await storeKeeper.GetAsync<ItemDetail>(Item(itemId));
        (after.CeilingPrice, after.CeilingNotification, after.PendingPricing).ShouldBe((3m, "S.O. 1234(E) of 30-Mar-2026", (PendingItemPricing?)null));
        await GoodsReceipts.IntoStockAsync(server, storeKeeper, await ReceiptAsync(storeKeeper, store, itemId, packMrp: 30m));

        var audit = await AuditAsync("inventory.item_price_changed", itemId);
        audit.Summary.ShouldNotBeNull().ShouldContain($"asked for by {asked.RequestedByName}, approved by {approved.DecidedByName}");
    }

    [Fact]
    public async Task A_gst_rate_changed_waits_too_and_a_ceiling_removed_says_what_it_switches_off()
    {
        var storeKeeper = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var itemId = await MedicineAsync(storeKeeper, Ceiling);

        var gst = await AcceptedAsync(storeKeeper, itemId, d => d with { GstRate = 12m });
        Detail(gst, "What it changes").ShouldContain("GST at 12% instead of 5%");
        (await storeKeeper.GetAsync<ItemDetail>(Item(itemId))).GstRate.ShouldBe(5m);

        // While that waits, another loosening is refused rather than asked for twice.
        using (var other = await storeKeeper.SendRawAsync(HttpMethod.Put, Item(itemId), await EditAsync(storeKeeper, itemId, d => d with { CeilingPrice = null })))
        {
            (other.StatusCode, await CodeAsync(other)).ShouldBe((HttpStatusCode.Conflict, ItemPriceApprovals.WaitingCode));
        }

        await server.ApprovePriceAsync(gst.Id);
        (await storeKeeper.GetAsync<ItemDetail>(Item(itemId))).GstRate.ShouldBe(12m);

        var removed = await AcceptedAsync(storeKeeper, itemId, d => d with { CeilingPrice = null });
        Detail(removed, "What it changes").ShouldContain("no NPPA ceiling is checked");
    }

    // ------------------------------------------------------------------ helpers

    private static string Item(Guid id) => $"/api/v1/inventory/items/{id}";

    private static string Detail(ApprovalRequestDto request, string label) => request.Details.Single(d => d.Label == label).Value;

    /// <summary>A Schedule H tablet bought in strips of 10, at <paramref name="ceiling"/> a tablet with GST.</summary>
    internal static async Task<Guid> MedicineAsync(ApiClient storeKeeper, decimal ceiling) =>
        (await storeKeeper.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: $"Metformin 500 mg Tablet {Letters(6)}", GenericName: "Metformin", BrandName: null, Type: ItemType.Drug,
            CategoryId: null, Strength: "500 mg", DosageForm: "Tablet", PackSize: "Strip of 10", BaseUnit: "TAB", PurchaseUnit: "STRIP", PurchaseUnitFactor: 10,
            HsnCode: "300490", GstRate: 5m, Schedule: DrugSchedule.H, IsControlled: false, IsColdChain: false, VedClass: VedClass.Essential,
            ReorderLevel: 0, ReorderQuantity: 0, MinimumStock: 0, MaximumStock: 0, Manufacturer: "Test Pharma", Barcode: null, IsActive: true, Version: null,
            CeilingPrice: ceiling)))
        .GetProperty("id").GetGuid();

    private static async Task<SaveItemRequest> EditAsync(ApiClient storeKeeper, Guid itemId, Func<SaveItemRequest, SaveItemRequest> change)
    {
        var d = await storeKeeper.GetAsync<ItemDetail>(Item(itemId));
        return change(new SaveItemRequest(
            d.Code, d.Name, d.GenericName, d.BrandName, d.Type, d.CategoryId, d.Strength, d.DosageForm, d.PackSize, d.BaseUnit, d.PurchaseUnit,
            d.PurchaseUnitFactor, d.HsnCode, d.GstRate, d.Schedule, d.IsControlled, d.IsColdChain, d.VedClass, d.ReorderLevel, d.ReorderQuantity,
            d.MinimumStock, d.MaximumStock, d.Manufacturer, d.Barcode, d.IsActive, d.Version, d.Specification, d.CeilingPrice, d.IsHighAlert,
            CeilingNotification: d.CeilingNotification));
    }

    private static async Task<ApprovalRequestDto> AcceptedAsync(ApiClient storeKeeper, Guid itemId, Func<SaveItemRequest, SaveItemRequest> change)
    {
        using var response = await storeKeeper.SendRawAsync(HttpMethod.Put, Item(itemId), await EditAsync(storeKeeper, itemId, change));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Put, Item(itemId));
    }

    private static async Task<Guid> StoreAsync(ApiClient api, string code) =>
        (await api.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == code).Id;

    /// <summary>Ten strips at a rate of ₹15 a strip and the given MRP a strip.</summary>
    private static async Task<CreateGoodsReceiptRequest> ReceiptAsync(ApiClient storeKeeper, Guid storeId, Guid itemId, decimal packMrp)
    {
        var vendor = (await storeKeeper.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return new CreateGoodsReceiptRequest(vendor.Id, storeId, null, $"INV-{Letters(10)}", today, today, null,
            [new GoodsReceiptLineInput(itemId, null, $"B{Letters(8)}", null, today.AddYears(2), 10m, 0m, 15m, packMrp, 0m, null, AcceptShortExpiry: false)],
            SendForApproval: true);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, ApprovalRequestDto request)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{request.Id}/approve", new ApproveRequest(null, request.Version));
        return (response.StatusCode, await CodeAsync(response));
    }

    private async Task<AuditEvent> AuditAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .SingleAsync(a => a.Action == action && a.EntityId == entityId.ToString(), Ct);
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));
}
