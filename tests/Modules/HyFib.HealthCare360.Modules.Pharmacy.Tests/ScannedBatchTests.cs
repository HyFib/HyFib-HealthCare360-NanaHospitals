using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

/// <summary>Which of a store's batches a scanned pharmacy tag names, and why none can be handed out.</summary>
public sealed class ScannedBatchTests
{
    private static readonly Guid Store = Guid.NewGuid();
    private static readonly Guid Item = Guid.NewGuid();

    private static BatchStock Batch(
        string number, DateOnly? expiry, decimal onHand, decimal reserved = 0, bool expired = false, bool blocked = false, decimal mrp = 25m) =>
        new(Guid.NewGuid(), Store, Item, number, expiry, mrp, 18m, 12m, onHand, reserved, expired || blocked ? 0 : onHand - reserved, expired, blocked,
            blocked ? "Recalled by the maker" : null, null);

    [Fact]
    public void The_tag_names_the_usable_batch_that_expires_first_whatever_the_case()
    {
        var later = Batch("AB1234", new DateOnly(2028, 3, 31), 20, mrp: 27m);
        var sooner = Batch("AB1234", new DateOnly(2027, 9, 30), 5);
        var other = Batch("XY9", new DateOnly(2026, 12, 31), 50);

        var scanned = SaleEndpoints.Scanned("ab1234", [other, later, sooner]);

        scanned.ShouldBe(new ScannedBatch("AB1234", sooner.BatchId, new DateOnly(2027, 9, 30), 25m, 5, null));
    }

    [Fact]
    public void A_batch_that_cannot_be_handed_out_says_why()
    {
        SaleEndpoints.Scanned("AB1234", [Batch("XY9", null, 10)]).ShouldSatisfyAllConditions(
            s => s.BatchId.ShouldBeNull(), s => s.Problem.ShouldBe("Batch AB1234 is not in this store."));
        SaleEndpoints.Scanned("AB1234", [Batch("AB1234", new DateOnly(2026, 1, 31), 10, expired: true)])
            .Problem.ShouldBe("Batch AB1234 has expired.");
        SaleEndpoints.Scanned("AB1234", [Batch("AB1234", new DateOnly(2027, 9, 30), 10, blocked: true)])
            .Problem.ShouldBe("Batch AB1234 is blocked: Recalled by the maker.");
        SaleEndpoints.Scanned("AB1234", [Batch("AB1234", new DateOnly(2027, 9, 30), 4, reserved: 4)])
            .Problem.ShouldBe("Nothing of batch AB1234 is left to sell here.");
    }
}
