using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

/// <summary>
/// The OT store look-up lists only what can be issued now from the facility's OT store — the store surgery materials are
/// consumed from — with the batches FEFO would issue first.
/// </summary>
public sealed class OtStoreLookupTests
{
    private static readonly DateOnly Today = TestData.Today;
    private static readonly Guid OtStore = Guid.CreateVersion7();

    [Theory]
    [InlineData(null, 20)]
    [InlineData(10, 10)]
    [InlineData(50, 50)]
    [InlineData(51, 50)]
    [InlineData(0, 1)]
    public void Take_defaults_to_twenty_and_is_capped_at_fifty(int? take, int expected) => OtStoreLookup.Take(take).ShouldBe(expected);

    [Fact]
    public void Batches_are_listed_first_expiry_first_out_and_unusable_ones_are_left_out()
    {
        var item = Candidate();
        OtStoreBatchRow[] batches =
        [
            Batch(item.Id, "LATE", Today.AddDays(300), 5),
            Batch(item.Id, "SOON", Today.AddDays(20), 4, reserved: 1),
            Batch(item.Id, "EXPIRED", Today.AddDays(-1), 9),
            Batch(item.Id, "BLOCKED", Today.AddDays(40), 9, blocked: true),
            Batch(item.Id, "BOOKED", Today.AddDays(10), 3, reserved: 3),
            Batch(item.Id, "NOEXPIRY", null, 2),
            Batch(Guid.CreateVersion7(), "OTHER-ITEM", Today.AddDays(5), 7),
        ];

        var result = OtStoreLookup.ToItem(item, batches, Today);

        result.Batches.Select(b => b.BatchNumber).ShouldBe(["SOON", "LATE", "NOEXPIRY"]);
        result.Available.ShouldBe(3 + 5 + 2);
        result.NearestExpiry.ShouldBe(Today.AddDays(20));
        result.Mrp.ShouldBe(104m); // the MRP of the batch FEFO issues first ("SOON")
        result.Batches[0].Available.ShouldBe(3);
    }

    [Fact]
    public void A_batch_expiring_today_can_still_be_used() =>
        OtStoreLookup.IsIssuable(Batch(Guid.CreateVersion7(), "B1", Today, 1), Today).ShouldBeTrue();

    [Fact]
    public void At_most_three_batches_are_listed_but_all_count_towards_availability()
    {
        var item = Candidate();
        var batches = Enumerable.Range(1, 5).Select(n => Batch(item.Id, $"B{n}", Today.AddDays(n * 30), 2)).ToList();

        var result = OtStoreLookup.ToItem(item, batches, Today);

        result.Batches.Count.ShouldBe(OtStoreLookup.BatchesPerItem);
        result.Available.ShouldBe(10);
    }

    [Fact]
    public void An_item_without_issuable_stock_shows_nothing_available()
    {
        var item = Candidate();

        var result = OtStoreLookup.ToItem(item, [Batch(item.Id, "OLD", Today.AddDays(-30), 4)], Today);

        result.Available.ShouldBe(0);
        result.NearestExpiry.ShouldBeNull();
        result.Mrp.ShouldBeNull();
        result.Batches.ShouldBeEmpty();
    }

    [Fact]
    public void The_item_fields_needed_to_record_a_material_are_carried()
    {
        var item = Candidate();

        var result = OtStoreLookup.ToItem(item, [], Today);

        result.ItemId.ShouldBe(item.Id);
        result.Code.ShouldBe("IMP-0042");
        result.Name.ShouldBe("Titanium locking plate 6-hole");
        result.Manufacturer.ShouldBe("Sharma Ortho");
        result.Type.ShouldBe(ItemType.Implant);
        result.BaseUnit.ShouldBe("NOS");
    }

    [Fact]
    public void Only_active_items_with_issuable_stock_in_the_ot_store_are_candidates()
    {
        var plate = NewItem("IMP-1", "Locking plate");
        var screw = NewItem("IMP-2", "Cortical screw");
        var mesh = NewItem("CON-1", "Prolene mesh");
        var expiredOnly = NewItem("CON-2", "Suture 2-0");
        var elsewhere = NewItem("CON-3", "Bone wax");
        var inactive = NewItem("CON-4", "Old stapler", active: false);
        var blockedOnly = NewItem("CON-5", "Drape");
        StockBatch[] batches =
        [
            Stock(plate, OtStore, Today.AddDays(90), 2),
            Stock(screw, OtStore, null, 10),
            Stock(mesh, OtStore, Today.AddDays(200), 1),
            Stock(expiredOnly, OtStore, Today.AddDays(-2), 5),
            Stock(elsewhere, Guid.CreateVersion7(), Today.AddDays(90), 5),
            Stock(inactive, OtStore, Today.AddDays(90), 5),
            Stock(blockedOnly, OtStore, Today.AddDays(90), 5, blocked: true),
        ];
        Item[] items = [plate, screw, mesh, expiredOnly, elsewhere, inactive, blockedOnly];

        var all = OtStoreLookup.Candidates(items.AsQueryable(), batches.AsQueryable(), OtStore, Today, null, null).ToList();
        var first = OtStoreLookup.Candidates(items.AsQueryable(), batches.AsQueryable(), OtStore, Today, null, 1).ToList();

        all.Select(c => c.Name).ShouldBe(["Cortical screw", "Locking plate", "Prolene mesh"]);
        first.Select(c => c.Name).ShouldBe(["Cortical screw"]);
    }

    private static OtStoreCandidate Candidate() =>
        new(Guid.CreateVersion7(), "IMP-0042", "Titanium locking plate 6-hole", null, "Sharma Ortho", ItemType.Implant, "NOS");

    private static OtStoreBatchRow Batch(Guid itemId, string number, DateOnly? expiry, decimal onHand, decimal reserved = 0, bool blocked = false) =>
        new(Guid.CreateVersion7(), itemId, number, expiry, onHand, reserved, blocked, onHand + 100, Today.AddDays(-60));

    private static Item NewItem(string code, string name, bool active = true) =>
        new() { Code = code, Name = name, Type = ItemType.Consumable, IsActive = active };

    private static StockBatch Stock(Item item, Guid storeId, DateOnly? expiry, decimal quantity, bool blocked = false)
    {
        var batch = new StockBatch { StoreId = storeId, ItemId = item.Id, BatchNumber = "L1", ExpiryDate = expiry, Mrp = 50, ReceivedOn = Today.AddDays(-10) };
        batch.Receive(quantity, 40).IsSuccess.ShouldBeTrue();
        if (blocked)
        {
            batch.Block("Recall").IsSuccess.ShouldBeTrue();
        }

        return batch;
    }
}
