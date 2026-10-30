using System.Text.Json;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A medicine of the test's own on the OP pharmacy's shelf, with one batch of it. The seeded shelf holds a few units of
/// each batch, and tests that sell from it at the same time empty a batch under each other ("0 available, 1 requested"):
/// a test that sells brings its own.
/// </summary>
internal sealed record OwnMedicine(Guid StoreId, Guid ItemId, BatchStock Batch)
{
    /// <summary>
    /// The store keeper adds an over-the-counter tablet that carries GST, as the seeded paracetamol does, and receives
    /// <paramref name="tablets"/> of it into the OP pharmacy at an MRP of <paramref name="mrp"/> a tablet (the seeded
    /// paracetamol suspension's MRP, so that its tax rounds as before).
    /// </summary>
    public static async Task<OwnMedicine> OnTheShelfAsync(HospitalServer server, ApiClient pharmacist, decimal tablets = 20m, decimal mrp = 32m)
    {
        var storeKeeper = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var store = (await pharmacist.GetAsync<List<StoreDto>>("/api/v1/inventory/stores?type=Pharmacy")).Single(s => s.Code == "OPPH");
        var itemId = (await storeKeeper.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: $"Paracetamol 500 mg Tablet {Letters(6)}", GenericName: "Paracetamol", BrandName: null, Type: ItemType.Drug,
            CategoryId: null, Strength: "500 mg", DosageForm: "Tablet", PackSize: "Strip of 10", BaseUnit: "TAB", PurchaseUnit: "TAB", PurchaseUnitFactor: 1,
            HsnCode: "300490", GstRate: 5m, Schedule: DrugSchedule.Otc, IsControlled: false, IsColdChain: false, VedClass: VedClass.Essential,
            ReorderLevel: 0, ReorderQuantity: 0, MinimumStock: 0, MaximumStock: 0, Manufacturer: "Test Pharma", Barcode: null, IsActive: true, Version: null)))
            .GetProperty("id").GetGuid();

        var vendor = (await storeKeeper.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var batchNumber = $"OWN{Letters(8)}";
        var receipt = await storeKeeper.PostAsync<GoodsReceiptCreated>("/api/v1/purchase/grns", new CreateGoodsReceiptRequest(
            vendor.Id, store.Id, null, $"INV-{Letters(10)}", today, today, null,
            [new GoodsReceiptLineInput(itemId, null, batchNumber, null, today.AddYears(2), tablets, 0m, mrp / 2, mrp, 0m, null, AcceptShortExpiry: false)],
            Post: true));
        receipt.Status.ShouldBe(GoodsReceiptStatus.Posted);

        var batch = (await pharmacist.GetAsync<List<BatchStock>>($"/api/v1/pharmacy/stock/batches?storeId={store.Id}&itemId={itemId}"))
            .Single(b => b.BatchNumber == batchNumber);
        return new OwnMedicine(store.Id, itemId, batch);
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));
}
