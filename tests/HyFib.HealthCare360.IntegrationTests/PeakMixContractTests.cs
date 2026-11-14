using System.Text.Json;
using HyFib.HealthCare360.LoadTest;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The load test reads the server's answers as JSON, by name: tools/HyFib.HealthCare360.LoadTest has no reference to the
/// server. These read rows made from the server's own contracts, written as the server writes them (camelCase, enums as
/// words), so that a name the load test gets wrong, or one the server renames, fails here at once rather than leaving a
/// step out of the mix with "no medicine in stock".
/// </summary>
public sealed class PeakMixContractTests
{
    [Fact]
    public void The_counter_sells_the_over_the_counter_medicines_of_the_pharmacy_stock_rows()
    {
        var tablet = new PharmacyStockRow(Guid.NewGuid(), "PCM500", "Paracetamol 500 mg Tablet", "Paracetamol", "500 mg", DrugSchedule.Otc,
            IsControlled: false, RequiresPrescription: false, IsColdChain: false, BaseUnit: "TAB", Available: 40m, Mrp: 3.2m, NearestExpiry: null);
        PharmacyStockRow[] rows =
        [
            tablet,
            tablet with { ItemId = Guid.NewGuid(), Schedule = DrugSchedule.H, RequiresPrescription = true },
            tablet with { ItemId = Guid.NewGuid(), Available = 0m },
            tablet with { ItemId = Guid.NewGuid(), Mrp = null },
        ];

        var sellable = FixtureDiscovery.SellableItems(JsonSerializer.SerializeToElement(rows, ApiClient.Json));

        // Only the tablet in stock, sold without a prescription and at an MRP; the counter charges that MRP.
        var item = sellable.ShouldHaveSingleItem();
        item.ItemId.ShouldBe(tablet.ItemId);
        item.Mrp.ShouldBe(3.2m);
    }
}
