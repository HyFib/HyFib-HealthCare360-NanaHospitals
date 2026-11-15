using System.Text.Json;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The counter's stock picker over the real API (PH-04): asked for stock only, it lists the first items by name that the
/// store can sell, however many items it has none of come before them. The load test finds the medicines it sells so.
/// </summary>
public sealed class PharmacyStockPickerTests(HospitalServer server)
{
    [Fact]
    public async Task In_stock_only_lists_what_the_store_can_sell_ahead_of_items_it_has_none_of()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var storeKeeper = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        // Two tablets a word of this test's own finds: the first by name has none in the OP pharmacy, the second is on its shelf.
        var word = Letters(10);
        var none = await NewTabletAsync(storeKeeper, $"{word} 250 mg Tablet");
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist, name: $"{word} 500 mg Tablet");

        var first = await StockAsync(pharmacist, medicine.StoreId, word, inStockOnly: false);
        var inStock = await StockAsync(pharmacist, medicine.StoreId, word, inStockOnly: true);

        // The first by name, held or not; and, in stock only, the first the store can sell.
        first.ShouldHaveSingleItem().ItemId.ShouldBe(none);
        first[0].Available.ShouldBe(0m);
        var row = inStock.ShouldHaveSingleItem();
        row.ItemId.ShouldBe(medicine.ItemId);
        row.Available.ShouldBeGreaterThan(0m);
    }

    private static Task<List<PharmacyStockRow>> StockAsync(ApiClient pharmacist, Guid storeId, string search, bool inStockOnly) =>
        pharmacist.GetAsync<List<PharmacyStockRow>>(
            $"/api/v1/pharmacy/stock?storeId={storeId}&search={Uri.EscapeDataString(search)}&inStockOnly={(inStockOnly ? "true" : "false")}&take=1");

    /// <summary>The store keeper adds an over-the-counter tablet, and receives none of it.</summary>
    private static async Task<Guid> NewTabletAsync(ApiClient storeKeeper, string name) =>
        (await storeKeeper.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: name, GenericName: "Paracetamol", BrandName: null, Type: ItemType.Drug,
            CategoryId: null, Strength: "250 mg", DosageForm: "Tablet", PackSize: "Strip of 10", BaseUnit: "TAB", PurchaseUnit: "TAB", PurchaseUnitFactor: 1,
            HsnCode: "300490", GstRate: 5m, Schedule: DrugSchedule.Otc, IsControlled: false, IsColdChain: false, VedClass: VedClass.Essential,
            ReorderLevel: 0, ReorderQuantity: 0, MinimumStock: 0, MaximumStock: 0, Manufacturer: "Test Pharma", Barcode: null, IsActive: true, Version: null)))
        .GetProperty("id").GetGuid();

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));
}
