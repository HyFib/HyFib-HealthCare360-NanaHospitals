using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A settled transaction matched with a payment the pharmacy or the canteen took by card or UPI: it names the counter
/// and its invoice or bill, tells an amount that differs, and frees the payment when its report is withdrawn or it is
/// set aside.
/// </summary>
public sealed class PosSettlementCounterMatchTests
{
    private static readonly DateOnly Day = new(2026, 10, 7);
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static PosSettlementLine Line(decimal amount, PosSettlementLineType type = PosSettlementLineType.Sale)
    {
        var row = new PosSettlementRow(
            1, type, PaymentMode.Card, "PH000001", null, null, amount, 0m, 0m, amount, "62791" + Random.Shared.Next(1_000_000, 9_999_999), null, null, Day, "AXISN1");
        return PosSettlementLine.From(Guid.CreateVersion7(), Guid.CreateVersion7(), "HDFC Bank", row);
    }

    [Fact]
    public void A_counters_payment_is_matched_with_its_invoice_and_what_the_counter_recorded()
    {
        var line = Line(240m);
        var payment = Guid.CreateVersion7();

        line.MatchCounter("pharmacy", payment, "PH/2026/000042", 240m, PosMatchBasis.Rrn, Now);

        line.ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.Matched),
            l => l.Counter.ShouldBe("pharmacy"),
            l => l.CounterPaymentId.ShouldBe(payment),
            l => l.CounterDocument.ShouldBe("PH/2026/000042"),
            l => l.RecordedAmount.ShouldBe(240m),
            l => l.MatchBasis.ShouldBe(PosMatchBasis.Rrn),
            l => l.MatchedAt.ShouldBe(Now),
            l => l.ReceiptId.ShouldBeNull(),
            l => l.NeedsAttention.ShouldBeFalse());
    }

    [Fact]
    public void Another_amount_than_the_counter_recorded_is_to_look_at()
    {
        var line = Line(241m);

        line.MatchCounter("canteen", Guid.CreateVersion7(), "CB-17", 240m, PosMatchBasis.ApprovalCode, Now);

        line.ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.AmountDiffers),
            l => l.RecordedAmount.ShouldBe(240m),
            l => l.NeedsAttention.ShouldBeTrue());
    }

    [Fact]
    public void Withdrawn_with_its_report_it_frees_the_counters_payment()
    {
        var line = Line(240m);
        line.MatchCounter("pharmacy", Guid.CreateVersion7(), "PH/2026/000042", 240m, PosMatchBasis.Rrn, Now);

        line.Withdraw(Day.AddDays(1));

        line.ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.Withdrawn),
            l => l.Counter.ShouldBeNull(),
            l => l.CounterPaymentId.ShouldBeNull(),
            l => l.CounterDocument.ShouldBeNull(),
            l => l.RecordedAmount.ShouldBeNull(),
            l => l.MatchBasis.ShouldBeNull());
    }

    [Fact]
    public void Matched_with_another_payment_or_set_aside_it_keeps_nothing_of_the_one_before()
    {
        var line = Line(90m, PosSettlementLineType.Refund);
        line.MatchCounter("pharmacy", Guid.CreateVersion7(), "PH/2026/000042", 90m, PosMatchBasis.Rrn, Now);
        var canteen = Guid.CreateVersion7();

        line.MatchCounter("canteen", canteen, "CB-17", 90m, PosMatchBasis.ApprovalCode, Now);
        (line.Counter, line.CounterPaymentId, line.CounterDocument, line.MatchBasis).ShouldBe(("canteen", (Guid?)canteen, "CB-17", (PosMatchBasis?)PosMatchBasis.ApprovalCode));

        line.MarkOtherCounter();
        line.ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.OtherCounter),
            l => l.Counter.ShouldBeNull(),
            l => l.CounterPaymentId.ShouldBeNull(),
            l => l.CounterDocument.ShouldBeNull());

        line.Unmatch();
        line.Match.ShouldBe(PosSettlementMatch.NoRefund);
    }
}
