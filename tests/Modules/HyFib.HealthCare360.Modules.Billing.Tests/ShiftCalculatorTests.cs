using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class ShiftCalculatorTests
{
    private static readonly ShiftMovement[] Day =
    [
        new(PaymentMode.Cash, 1_500m, false),
        new(PaymentMode.Cash, 2_000m, false),
        new(PaymentMode.Card, 3_200m, false),
        new(PaymentMode.Upi, 800m, false),
        new(PaymentMode.Cash, 300m, true),
        new(PaymentMode.Advance, 10_000m, false),
        new(PaymentMode.Insurance, 45_000m, false),
    ];

    [Fact]
    public void Expected_cash_is_float_plus_cash_in_minus_cash_out()
    {
        var reconciliation = ShiftCalculator.Reconcile(1_000m, Day, declaredCash: 4_200m);

        reconciliation.ExpectedCash.ShouldBe(4_200m);
        reconciliation.Variance.ShouldBe(0m);
    }

    [Fact]
    public void A_short_drawer_shows_a_negative_variance_and_an_excess_a_positive_one()
    {
        ShiftCalculator.Reconcile(1_000m, Day, declaredCash: 4_150m).Variance.ShouldBe(-50m);
        ShiftCalculator.Reconcile(1_000m, Day, declaredCash: 4_210.50m).Variance.ShouldBe(10.50m);
    }

    [Fact]
    public void Totals_are_per_mode_and_exclude_advance_adjustments_and_insurer_settlements()
    {
        var totals = ShiftCalculator.Reconcile(0m, Day, 0m).Totals;

        totals.Select(t => t.Mode).ShouldBe(new[] { PaymentMode.Cash, PaymentMode.Card, PaymentMode.Upi });
        var cash = totals.Single(t => t.Mode == PaymentMode.Cash);
        cash.Collected.ShouldBe(3_500m);
        cash.Refunded.ShouldBe(300m);
        cash.Net.ShouldBe(3_200m);
        ShiftCalculator.Reconcile(0m, Day, 0m).NetCollection.ShouldBe(7_200m);
    }

    [Fact]
    public void Closing_freezes_the_reconciliation_and_a_shift_closes_only_once()
    {
        var shift = CashierShift.Open(TestData.FacilityId, Guid.CreateVersion7(), "Anita", "  OP Counter 1 ", 1_000.004m, TestData.Now);
        var reconciliation = ShiftCalculator.Reconcile(shift.OpeningFloat, Day, 4_150m);

        shift.Close(reconciliation, "Short by 50", TestData.Now.AddHours(8)).IsSuccess.ShouldBeTrue();

        shift.Counter.ShouldBe("OP Counter 1");
        shift.OpeningFloat.ShouldBe(1_000m);
        shift.Status.ShouldBe(ShiftStatus.Closed);
        shift.Variance.ShouldBe(-50m);
        shift.Totals.Count.ShouldBe(3);
        shift.Close(reconciliation, null, TestData.Now).Error!.Code.ShouldBe("billing.shift_closed");
    }

    [Theory]
    [InlineData(-100, 0, true)]
    [InlineData(-0.01, 0, true)]
    [InlineData(-50, 50, false)]
    [InlineData(-50.01, 50, true)]
    [InlineData(0, 0, false)]
    [InlineData(20, 0, false)]
    [InlineData(-10, -5, true)]
    public void Only_a_shortage_beyond_the_tolerance_waits_for_a_supervisor_and_an_excess_never_does(double variance, double tolerance, bool waits)
    {
        ShiftCalculator.ShortageNeedsSupervisor((decimal)variance, (decimal)tolerance).ShouldBe(waits);
    }

    [Fact]
    public void A_shift_closed_short_awaits_a_supervisor_who_is_not_its_cashier()
    {
        var cashier = Guid.CreateVersion7();
        var shift = CashierShift.Open(TestData.FacilityId, cashier, "Asha", "OPD-1", 1_000m, TestData.Now);
        shift.Close(ShiftCalculator.Reconcile(1_000m, [], declaredCash: 900m), "Note missing", TestData.Now, shortageTolerance: 0m).IsSuccess.ShouldBeTrue();

        (shift.Status, shift.ShortageAwaiting, shift.Shortage).ShouldBe((ShiftStatus.Closed, true, 100m));
        shift.AcceptShortage(cashier, "Asha", TestData.Now).Error!.Code.ShouldBe(ShiftCalculator.OwnShortageCode);
        shift.ShortageAwaiting.ShouldBeTrue();

        var supervisor = Guid.CreateVersion7();
        shift.AcceptShortage(supervisor, "Ravi", TestData.Now).IsSuccess.ShouldBeTrue();
        (shift.ShortageAwaiting, shift.ShortageAcceptedBy, shift.ShortageAcceptedByName).ShouldBe((false, supervisor, "Ravi"));
        shift.AcceptShortage(Guid.CreateVersion7(), "Meena", TestData.Now).Error!.Code.ShouldBe("billing.shift_shortage_not_awaiting");
    }

    [Fact]
    public void A_shift_closed_over_or_within_the_tolerance_awaits_no_one()
    {
        var over = CashierShift.Open(TestData.FacilityId, Guid.CreateVersion7(), "Asha", "OPD-1", 1_000m, TestData.Now);
        over.Close(ShiftCalculator.Reconcile(1_000m, [], declaredCash: 1_020m), null, TestData.Now).IsSuccess.ShouldBeTrue();
        var within = CashierShift.Open(TestData.FacilityId, Guid.CreateVersion7(), "Asha", "OPD-1", 1_000m, TestData.Now);
        within.Close(ShiftCalculator.Reconcile(1_000m, [], declaredCash: 990m), null, TestData.Now, shortageTolerance: 10m).IsSuccess.ShouldBeTrue();

        (over.ShortageAwaiting, over.Shortage, within.ShortageAwaiting, within.Shortage).ShouldBe((false, 0m, false, 10m));
    }
}
