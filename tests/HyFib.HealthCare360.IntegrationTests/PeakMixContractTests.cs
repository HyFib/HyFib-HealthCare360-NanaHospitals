using System.Text.Json;
using HyFib.HealthCare360.LoadTest;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Http;
using Microsoft.AspNetCore.Http;

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

    [Fact]
    public void The_lab_orders_only_tests_of_the_lab_test_list_that_can_be_ordered_on_their_own()
    {
        var sugar = new LabTestListItem(Guid.NewGuid(), "LAB-FBS", "Glucose, fasting", "FBS", LabSection.Hematology, SpecimenType.WholeBlood,
            ContainerType.Edta, LabResultType.Numeric, "mg/dL", IsPanel: false, IsOrderable: true, IsProvisional: false, IsActive: true, TatHours: 4,
            ServiceItemId: Guid.NewGuid(), Price: 80m, Preparation: null, ComponentCount: 0);
        LabTestListItem[] rows =
        [
            sugar,
            // A panel's component, or a value calculated from others: the server refuses to order it alone.
            sugar with { Id = Guid.NewGuid(), Name = "Albumin/globulin ratio", IsOrderable = false },
            sugar with { Id = Guid.NewGuid(), IsActive = false },
        ];

        var tests = FixtureDiscovery.OrderableTests(JsonSerializer.SerializeToElement(rows, ApiClient.Json));

        tests.ShouldBe(new[] { sugar.Id });
    }

    [Fact]
    public void Every_portal_step_is_one_the_portals_address_serves()
    {
        // The load test sends the portal's steps to the portal's address (--portal-server), which answers only the portal's own
        // API, the endpoints it shares with the staff app and outside services' callbacks: a staff endpoint there is a 404.
        var context = new OperationContext([Guid.NewGuid()], new Random(1), () => 1, new Fixtures());
        var portalSteps = PeakMix.Extra().Where(o => o.Role == Role.Portal).ToList();
        portalSteps.Single(o => o.Name == PeakMix.PortalMe).OnSuccess!(context, JsonSerializer.SerializeToElement(new[] { new { id = Guid.NewGuid() } }));
        portalSteps.ShouldNotBeEmpty();
        foreach (var step in portalSteps)
        {
            using var request = step.Request(context);
            var path = "/" + request.RequestUri!.OriginalString.Split('?')[0];
            ListenerPaths.Decide(Listener.Portal, new PathString(path)).ShouldBe(ListenerRoute.Serve, $"{step.Name}: {path}");
        }
    }
}
