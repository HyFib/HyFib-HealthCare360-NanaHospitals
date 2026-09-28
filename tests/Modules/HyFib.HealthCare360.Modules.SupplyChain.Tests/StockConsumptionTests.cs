using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

/// <summary>OT implants and consumables are consumed by the lot number on the pack and reversed by their record.</summary>
public sealed class StockConsumptionTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static BatchCandidate Lot(string number, int? expiresInDays, decimal available, bool blocked = false, int receivedDaysAgo = 30) =>
        new(Guid.CreateVersion7(), number, expiresInDays is { } days ? Today.AddDays(days) : null, Today.AddDays(-receivedDaysAgo), available, blocked);

    [Fact]
    public void Uses_the_lot_on_the_pack_even_when_another_lot_expires_sooner()
    {
        var sooner = Lot("MESH-A1", 30, 10);
        var used = Lot("MESH-B7", 400, 2);

        var plan = FefoAllocator.AllocateLot(1, "mesh-b7", null, [sooner, used], Today).Value;

        plan.Allocations.ShouldHaveSingleItem().BatchId.ShouldBe(used.BatchId);
    }

    [Fact]
    public void Rows_sharing_a_lot_number_are_used_first_expiry_first_unless_the_expiry_is_given()
    {
        var repriced = Lot("K9", 200, 1);
        var original = Lot("K9", 100, 1);

        FefoAllocator.AllocateLot(2, "K9", null, [repriced, original], Today).Value
            .Allocations.Select(a => a.BatchId).ShouldBe([original.BatchId, repriced.BatchId]);
        FefoAllocator.AllocateLot(1, "K9", Today.AddDays(200), [repriced, original], Today).Value
            .Allocations.ShouldHaveSingleItem().BatchId.ShouldBe(repriced.BatchId);
    }

    [Fact]
    public void A_lot_that_is_not_in_the_store_is_reported_as_such()
    {
        FefoAllocator.AllocateLot(1, "NOPE", null, [Lot("K9", 100, 5)], Today).Error!.Code.ShouldBe("stock.batch_not_found");
    }

    [Fact]
    public void Expired_and_blocked_lots_say_why_they_cannot_be_used()
    {
        FefoAllocator.AllocateLot(1, "OLD", null, [Lot("OLD", -1, 5)], Today).Error!.Code.ShouldBe("stock.batch_expired");
        FefoAllocator.AllocateLot(1, "HOLD", null, [Lot("HOLD", 100, 5, blocked: true)], Today).Error!.Code.ShouldBe("stock.batch_blocked");
    }

    [Fact]
    public void A_short_lot_fails_as_insufficient_and_is_never_partly_consumed()
    {
        var error = FefoAllocator.AllocateLot(3, "K9", null, [Lot("K9", 100, 2)], Today, "Knee implant").Error!;

        error.Code.ShouldBe("stock.insufficient");
        error.Message.ShouldContain("Knee implant (batch K9)");
        error.Message.ShouldContain("2 available, 3 requested");
    }

    [Fact]
    public void Reversal_puts_back_only_what_is_still_consumed_per_batch()
    {
        var (a, b) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var consumedA = new ConsumptionEntry(Guid.CreateVersion7(), a, -2);
        ConsumptionEntry[] entries =
        [
            consumedA,
            new(Guid.CreateVersion7(), b, -1),
            new(Guid.CreateVersion7(), a, 1),

            // The same row seen both in the database and in the change tracker counts once.
            consumedA,
        ];

        StockService.Outstanding(entries).ShouldBe([(a, 1m), (b, 1m)], ignoreOrder: true);
    }

    [Fact]
    public void A_fully_reversed_record_has_nothing_outstanding()
    {
        var batch = Guid.CreateVersion7();

        StockService.Outstanding([new(Guid.CreateVersion7(), batch, -2), new(Guid.CreateVersion7(), batch, 2)]).ShouldBeEmpty();
    }
}
