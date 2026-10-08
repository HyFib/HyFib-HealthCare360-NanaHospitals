using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Pharmacy tags over the real API (INT-BAR): scanning a tag's barcode (item code and batch joined by #) at the counter
/// or in the dispensing queue finds the medicine in the store and picks that batch, and says why when the batch cannot
/// be handed out there.
/// </summary>
public sealed class PharmacyTagTests(HospitalServer server)
{
    [Fact]
    public async Task Scanning_a_pharmacy_tag_finds_the_medicine_and_picks_its_batch()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var store = (await pharmacist.GetAsync<List<StoreDto>>("/api/v1/inventory/stores?type=Pharmacy")).Single(s => s.Code == "OPPH");
        var medicine = (await StockAsync(pharmacist, store.Id, "paracetamol")).First(r => r.Available > 0);
        var batches = await pharmacist.GetAsync<List<BatchStock>>($"/api/v1/pharmacy/stock/batches?storeId={store.Id}&itemId={medicine.ItemId}");
        var tagged = batches.Where(b => b.Available > 0 && !b.IsExpired && !b.IsBlocked).OrderByDescending(b => b.ExpiryDate).First();
        medicine.Scanned.ShouldBeNull("An ordinary search names no batch.");

        // The tag picks the batch it names (in whatever case it was printed), even when another expires first.
        var scanned = (await StockAsync(pharmacist, store.Id, $"{medicine.Code.ToLowerInvariant()}#{tagged.BatchNumber}")).ShouldHaveSingleItem();

        scanned.ItemId.ShouldBe(medicine.ItemId);
        var batch = scanned.Scanned.ShouldNotBeNull();
        var expected = batches
            .Where(b => b.BatchNumber == tagged.BatchNumber && b.Available > 0 && !b.IsExpired && !b.IsBlocked)
            .OrderBy(b => b.ExpiryDate ?? DateOnly.MaxValue)
            .First();
        batch.ShouldBe(new ScannedBatch(tagged.BatchNumber, expected.BatchId, expected.ExpiryDate, expected.Mrp, expected.Available, null));

        // A batch the store does not hold finds the medicine, and says so.
        var unknown = (await StockAsync(pharmacist, store.Id, $"{medicine.Code}#NOSUCH9")).ShouldHaveSingleItem();
        unknown.ItemId.ShouldBe(medicine.ItemId);
        unknown.Scanned.ShouldBe(new ScannedBatch("NOSUCH9", null, null, null, 0, "Batch NOSUCH9 is not in this store."));
    }

    private static Task<List<PharmacyStockRow>> StockAsync(ApiClient pharmacist, Guid storeId, string search) =>
        pharmacist.GetAsync<List<PharmacyStockRow>>($"/api/v1/pharmacy/stock?storeId={storeId}&search={Uri.EscapeDataString(search)}&take=2");
}
