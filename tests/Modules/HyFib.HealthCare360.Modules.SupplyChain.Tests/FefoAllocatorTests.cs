namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

public sealed class FefoAllocatorTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static BatchCandidate Batch(string number, int? expiresInDays, decimal available, int receivedDaysAgo = 30, bool blocked = false) =>
        new(Guid.CreateVersion7(), number, expiresInDays is { } days ? Today.AddDays(days) : null, Today.AddDays(-receivedDaysAgo), available, blocked);

    [Fact]
    public void Allocates_the_earliest_expiry_first()
    {
        var late = Batch("LATE", 300, 50);
        var early = Batch("EARLY", 60, 50);

        var plan = FefoAllocator.Allocate(30, [late, early], Today).Value;

        plan.Allocations.Count.ShouldBe(1);
        plan.Allocations[0].BatchNumber.ShouldBe("EARLY");
        plan.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void Splits_across_batches_in_expiry_order_when_one_is_not_enough()
    {
        var plan = FefoAllocator.Allocate(70, [Batch("B2", 200, 40), Batch("B1", 100, 40), Batch("B3", 400, 40)], Today).Value;

        plan.Allocations.Select(a => (a.BatchNumber, a.Quantity)).ShouldBe([("B1", 40m), ("B2", 30m)]);
    }

    [Fact]
    public void Never_allocates_expired_batches_even_when_they_hold_the_most_stock()
    {
        var expired = Batch("EXPIRED", -1, 1000);
        var good = Batch("GOOD", 90, 10);

        var plan = FefoAllocator.Allocate(10, [expired, good], Today).Value;
        plan.Allocations.ShouldHaveSingleItem().BatchNumber.ShouldBe("GOOD");

        var onlyExpired = FefoAllocator.Allocate(1, [expired], Today);
        onlyExpired.IsFailure.ShouldBeTrue();
        onlyExpired.Error!.Code.ShouldBe("stock.insufficient");
    }

    [Fact]
    public void The_expiry_day_itself_is_still_usable()
    {
        var plan = FefoAllocator.Allocate(5, [Batch("TODAY", 0, 5)], Today);

        plan.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Skips_blocked_and_empty_batches()
    {
        var plan = FefoAllocator.Allocate(5, [Batch("BLOCKED", 10, 100, blocked: true), Batch("EMPTY", 20, 0), Batch("OK", 400, 5)], Today).Value;

        plan.Allocations.ShouldHaveSingleItem().BatchNumber.ShouldBe("OK");
    }

    [Fact]
    public void Fails_with_a_clear_insufficient_stock_error_when_short()
    {
        var result = FefoAllocator.Allocate(25, [Batch("A", 100, 10), Batch("B", 200, 5)], Today);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("stock.insufficient");
        result.Error.Message.ShouldContain("15 available");
        result.Error.Message.ShouldContain("25 requested");
    }

    [Fact]
    public void Partial_allocation_returns_what_is_available_and_reports_the_shortfall()
    {
        var plan = FefoAllocator.Allocate(25, [Batch("A", 100, 10), Batch("B", 200, 5)], Today, new AllocationOptions(AllowPartial: true)).Value;

        plan.Allocated.ShouldBe(15);
        plan.Shortfall.ShouldBe(10);
        plan.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void Minimum_shelf_life_skips_batches_that_would_expire_during_the_course()
    {
        var soon = Batch("SOON", 10, 100);
        var later = Batch("LATER", 60, 100);

        var plan = FefoAllocator.Allocate(30, [soon, later], Today, new AllocationOptions(MinimumShelfLifeDays: 30)).Value;

        plan.Allocations.ShouldHaveSingleItem().BatchNumber.ShouldBe("LATER");
    }

    [Fact]
    public void Non_expiring_stock_is_used_after_dated_stock()
    {
        var plan = FefoAllocator.Allocate(15, [Batch("NOEXPIRY", null, 100), Batch("DATED", 700, 10)], Today).Value;

        plan.Allocations.Select(a => a.BatchNumber).ShouldBe(["DATED", "NOEXPIRY"]);
    }

    [Fact]
    public void Same_expiry_is_broken_by_receipt_date_then_batch_number()
    {
        var newer = Batch("N1", 100, 10, receivedDaysAgo: 5);
        var olderB = Batch("O2", 100, 10, receivedDaysAgo: 50);
        var olderA = Batch("O1", 100, 10, receivedDaysAgo: 50);

        var plan = FefoAllocator.Allocate(30, [newer, olderB, olderA], Today).Value;

        plan.Allocations.Select(a => a.BatchNumber).ShouldBe(["O1", "O2", "N1"]);
    }

    [Fact]
    public void A_scanned_batch_is_used_first_but_an_expired_one_never()
    {
        var early = Batch("EARLY", 30, 10);
        var scanned = Batch("SCANNED", 300, 10);
        var expired = Batch("EXPIRED", -5, 10);

        FefoAllocator.Allocate(5, [early, scanned], Today, new AllocationOptions(PreferredBatchId: scanned.BatchId)).Value
            .Allocations.ShouldHaveSingleItem().BatchNumber.ShouldBe("SCANNED");
        FefoAllocator.Allocate(5, [early, expired], Today, new AllocationOptions(PreferredBatchId: expired.BatchId)).Value
            .Allocations.ShouldHaveSingleItem().BatchNumber.ShouldBe("EARLY");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Rejects_zero_or_negative_quantities(int quantity)
    {
        FefoAllocator.Allocate(quantity, [Batch("A", 100, 10)], Today).Error!.Code.ShouldBe("stock.invalid_quantity");
    }

    [Fact]
    public void Available_counts_only_usable_stock()
    {
        var total = FefoAllocator.Available([Batch("OK", 100, 10), Batch("EXP", -1, 50), Batch("BLK", 100, 20, blocked: true), Batch("SHORT", 5, 7)], Today, 30);

        total.ShouldBe(10);
    }
}
