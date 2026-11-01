using HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;
using static HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys.JourneyClock;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Domain.Journeys;

/// <summary>
/// When a bill was settled: the moment its due last came down to nothing, whatever brought it there (a payment, a credit
/// note or a write-off), and not a payment or the finalization from before it.
/// </summary>
public sealed class BillSettlementTests
{
    [Fact]
    public void A_bill_the_payer_takes_whole_is_settled_when_it_is_finalized() =>
        BillSettlement.At(At(9, 0), 0m, []).ShouldBe(At(9, 0));

    [Fact]
    public void A_bill_paid_in_one_go_is_settled_at_the_payment() =>
        BillSettlement.At(At(9, 0), 600m, [(At(9, 5), -600m)]).ShouldBe(At(9, 5));

    [Fact]
    public void A_bill_part_paid_and_written_off_is_settled_at_the_write_off_not_the_payment() =>
        BillSettlement.At(At(9, 0), 600m, [(At(9, 5), -400m), (At(14, 0), -200m)]).ShouldBe(At(14, 0));

    [Fact]
    public void A_credit_note_before_the_payment_leaves_the_payment_to_settle_the_bill() =>
        BillSettlement.At(At(9, 0), 600m, [(At(9, 30), -250m), (At(9, 45), -350m)]).ShouldBe(At(9, 45));

    [Fact]
    public void A_credit_note_on_a_bill_already_paid_and_its_refund_settle_nothing_again()
    {
        // Paid at 9:05; ₹100 credited at 15:00 and refunded at 15:10: the bill was settled at 9:05 all along.
        var changes = new[] { (At(9, 5), -600m), (At(15, 0), -100m), (At(15, 10), 100m) };

        BillSettlement.At(At(9, 0), 600m, changes).ShouldBe(At(9, 5));
    }

    [Fact]
    public void A_bill_due_again_after_a_refund_is_settled_when_it_is_paid_again()
    {
        // Paid at 9:05, ₹200 of it refunded at 10:00 with nothing credited (owed again), paid again at 12:30.
        var changes = new[] { (At(12, 30), -200m), (At(9, 5), -600m), (At(10, 0), 200m) };

        BillSettlement.At(At(9, 0), 600m, changes).ShouldBe(At(12, 30), "the changes are replayed in the order they happened");
        BillSettlement.At(At(9, 0), 600m, changes[1..]).ShouldBeNull("₹200 is due again");
    }

    [Fact]
    public void A_bill_not_finalized_was_never_settled() =>
        BillSettlement.At(null, 0m, []).ShouldBeNull();
}
