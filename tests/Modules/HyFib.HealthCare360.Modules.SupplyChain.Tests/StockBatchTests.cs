using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

/// <summary>Stock movement rules and the concurrency guarantees that do not need a database.</summary>
public sealed class StockBatchTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);

    private static StockBatch NewBatch(decimal quantity = 100, decimal cost = 2m, int expiresInDays = 200)
    {
        var batch = new StockBatch { BatchNumber = "B1", ExpiryDate = Today.AddDays(expiresInDays), Mrp = 3.5m, ReceivedOn = Today };
        batch.Receive(quantity, cost).IsSuccess.ShouldBeTrue();
        return batch;
    }

    [Fact]
    public void Deduct_reduces_on_hand_and_never_goes_negative()
    {
        var batch = NewBatch(10);

        batch.Deduct(4).IsSuccess.ShouldBeTrue();
        batch.QuantityOnHand.ShouldBe(6);

        var tooMuch = batch.Deduct(7);
        tooMuch.Error!.Code.ShouldBe("stock.insufficient");
        batch.QuantityOnHand.ShouldBe(6);
    }

    [Fact]
    public void Reserved_stock_cannot_be_issued_to_someone_else()
    {
        var batch = NewBatch(10);
        batch.Reserve(8).IsSuccess.ShouldBeTrue();

        batch.QuantityAvailable.ShouldBe(2);
        batch.Deduct(3).IsFailure.ShouldBeTrue();
        batch.Adjust(-3).IsFailure.ShouldBeTrue();
        batch.Deduct(2).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Consuming_a_reservation_takes_it_from_both_reserved_and_on_hand()
    {
        var batch = NewBatch(10);
        batch.Reserve(6);

        batch.ConsumeReservation(4).IsSuccess.ShouldBeTrue();

        batch.QuantityReserved.ShouldBe(2);
        batch.QuantityOnHand.ShouldBe(6);
        batch.ConsumeReservation(3).Error!.Code.ShouldBe("stock.reservation_exceeded");
        batch.ReleaseReservation(3).Error!.Code.ShouldBe("stock.reservation_exceeded");
        batch.ReleaseReservation(2).IsSuccess.ShouldBeTrue();
        batch.QuantityAvailable.ShouldBe(6);
    }

    [Fact]
    public void Receiving_the_same_batch_at_another_cost_re_averages_the_cost()
    {
        var batch = NewBatch(100, cost: 2m);

        batch.Receive(100, 3m);

        batch.QuantityOnHand.ShouldBe(200);
        batch.PurchaseRate.ShouldBe(2.5m);
    }

    [Fact]
    public void Expired_or_blocked_batches_cannot_be_issued_but_expired_stock_can_be_written_off()
    {
        var expired = NewBatch(10, expiresInDays: -1);
        expired.EnsureIssuable(Today).Error!.Code.ShouldBe("stock.batch_expired");
        expired.Adjust(-10).IsSuccess.ShouldBeTrue();

        var held = NewBatch(10);
        held.Block(" ").IsFailure.ShouldBeTrue();
        held.Block("Recall notice 12/2026").IsSuccess.ShouldBeTrue();
        held.EnsureIssuable(Today).Error!.Code.ShouldBe("stock.batch_blocked");
        held.Unblock();
        held.EnsureIssuable(Today).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Movements_need_a_positive_quantity(int quantity)
    {
        var batch = NewBatch();

        batch.Deduct(quantity).Error!.Code.ShouldBe("stock.invalid_quantity");
        batch.Reserve(quantity).Error!.Code.ShouldBe("stock.invalid_quantity");
        batch.Receive(quantity, 1).Error!.Code.ShouldBe("stock.invalid_quantity");
    }

    [Fact]
    public void A_plan_made_from_a_stale_snapshot_cannot_oversell_the_live_batch()
    {
        // Two counters plan against the same snapshot; the second deduction is refused by the batch itself
        // (and in the database the xmin concurrency token + advisory lock stop the race).
        var batch = NewBatch(10);
        var snapshot = new[] { BatchCandidate.From(batch) };
        var first = FefoAllocator.Allocate(8, snapshot, Today).Value;
        var second = FefoAllocator.Allocate(8, snapshot, Today).Value;

        batch.Deduct(first.Allocations[0].Quantity).IsSuccess.ShouldBeTrue();
        batch.Deduct(second.Allocations[0].Quantity).Error!.Code.ShouldBe("stock.insufficient");
        batch.QuantityOnHand.ShouldBe(2);
    }

    [Fact]
    public void Reservation_rows_track_what_is_still_outstanding()
    {
        var now = DateTimeOffset.UnixEpoch;
        var reservation = new StockReservation { Quantity = 10 };

        reservation.Consume(4, now).IsSuccess.ShouldBeTrue();
        reservation.Outstanding.ShouldBe(6);
        reservation.Consume(7, now).IsFailure.ShouldBeTrue();

        reservation.Release(now).ShouldBe(6);
        reservation.Status.ShouldBe(ReservationStatus.Consumed);
        reservation.Release(now).ShouldBe(0);
        reservation.Consume(1, now).Error!.Code.ShouldBe("reservation.closed");
    }

    [Fact]
    public void Lock_keys_are_stable_per_item_and_store()
    {
        var store = Guid.CreateVersion7();
        var item = Guid.CreateVersion7();

        StockService.LockKey(store, item).ShouldBe(StockService.LockKey(store, item));
        StockService.LockKey(store, item).ShouldNotBe(StockService.LockKey(item, store));
        StockService.LockKey(store, item).ShouldNotBe(StockService.LockKey(store, Guid.CreateVersion7()));
    }

    [Theory]
    [InlineData(" b12x ", "B12X")]
    [InlineData("", "NA")]
    [InlineData(null, "NA")]
    public void Batch_numbers_are_normalised(string? input, string expected) => StockService.NormalizeBatch(input).ShouldBe(expected);

    [Fact]
    public void Only_consumption_movements_feed_the_analysis()
    {
        StockLedgerEntry.IsConsumption(StockMovementType.Dispense).ShouldBeTrue();
        StockLedgerEntry.IsConsumption(StockMovementType.Return).ShouldBeTrue();
        StockLedgerEntry.IsConsumption(StockMovementType.TransferOut).ShouldBeFalse();
        StockLedgerEntry.IsConsumption(StockMovementType.ExpiryWriteOff).ShouldBeFalse();
    }

    [Fact]
    public void Register_items_announce_their_movements()
    {
        var entry = new StockLedgerEntry { BatchNumber = "M1", MovementType = StockMovementType.Grn, Quantity = 10, BalanceAfter = 10 };

        entry.AnnounceRegisterMovement();

        entry.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<ControlledStockMoved>().BalanceAfter.ShouldBe(10);
    }
}
