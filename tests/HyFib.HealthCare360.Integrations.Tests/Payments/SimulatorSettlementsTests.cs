using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Payments;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Integrations.Tests.Payments;

/// <summary>The training gateway settles like a real one, so the reconciliation can be practised end to end.</summary>
public sealed class SimulatorSettlementsTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private static readonly DateOnly Day = new(2026, 10, 1);

    [Fact]
    public void Next_day_settlements_happen_at_ten_and_take_what_was_paid_before_the_day()
    {
        var run = SimulatorSettlements.Plan(Day, delayDays: 1, now: new DateTimeOffset(2026, 10, 1, 11, 0, 0, Ist)).ShouldNotBeNull();

        run.SettleAt.ShouldBe(new DateTimeOffset(2026, 10, 1, 10, 0, 0, Ist));
        run.CoverBefore.ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, Ist));
    }

    [Fact]
    public void Nothing_is_settled_before_ten_or_for_a_day_still_to_come()
    {
        SimulatorSettlements.Plan(Day, 1, new DateTimeOffset(2026, 10, 1, 9, 59, 0, Ist)).ShouldBeNull();
        SimulatorSettlements.Plan(Day.AddDays(1), 0, new DateTimeOffset(2026, 10, 1, 23, 0, 0, Ist)).ShouldBeNull();
    }

    [Fact]
    public void Two_day_settlements_take_what_was_paid_two_days_earlier()
    {
        var run = SimulatorSettlements.Plan(Day, delayDays: 2, now: new DateTimeOffset(2026, 10, 2, 8, 0, 0, Ist)).ShouldNotBeNull();

        run.CoverBefore.ShouldBe(new DateTimeOffset(2026, 9, 30, 0, 0, 0, Ist));
    }

    [Fact]
    public void Settling_at_once_takes_everything_paid_so_far()
    {
        var now = new DateTimeOffset(2026, 10, 1, 15, 30, 0, Ist);

        SimulatorSettlements.Plan(Day, 0, now).ShouldBe(new SimulatorSettlementRun(now, now));
        SimulatorSettlements.Plan(Day.AddDays(-1), 0, now)!.SettleAt.ShouldBe(new DateTimeOffset(2026, 9, 30, 23, 59, 59, Ist));
    }

    [Theory]
    [InlineData(1000, 23.60, 3.60)]
    [InlineData(1500.50, 35.41, 5.40)]
    [InlineData(1, 0.02, 0.00)]
    public void The_fee_is_two_percent_plus_gst_on_it(decimal amount, decimal fee, decimal tax) =>
        SimulatorSettlements.Fee(amount).ShouldBe((fee, tax));

    [Fact]
    public void A_settlement_pays_the_payments_less_fees_and_refunds_to_the_bank()
    {
        var settledAt = new DateTimeOffset(2026, 10, 1, 10, 0, 0, Ist);
        SimulatorTransaction[] settled =
        [
            new() { Kind = SimulatorTransactionKind.PaymentLink, ExternalId = "plink_sim1", PaymentId = "pay_sim1", Amount = 1000m, CreatedAt = settledAt.AddDays(-1), SettlementId = "setl_simAAAA0000BBBB1111", SettledAt = settledAt },
            new() { Kind = SimulatorTransactionKind.PaymentRefund, ExternalId = "rfnd_sim1", Reference = "pay_sim0", Amount = 200m, CreatedAt = settledAt.AddHours(-20), SettlementId = "setl_simAAAA0000BBBB1111", SettledAt = settledAt },
            new() { Kind = SimulatorTransactionKind.PaymentOrder, ExternalId = "order_sim2", PaymentId = "pay_sim2", Amount = 500m, CreatedAt = settledAt.AddHours(-30) },
        ];

        var settlement = SimulatorSettlements.Build(settled).ShouldHaveSingleItem();

        settlement.SettlementId.ShouldBe("setl_simAAAA0000BBBB1111");
        settlement.Status.ShouldBe(PaymentSettlementStatus.Processed);
        settlement.Utr.ShouldBe("SIMUTR00BBBB1111");
        settlement.Amount.ShouldBe(1000m - 23.60m - 200m);
        settlement.Items.Count.ShouldBe(2);
        settlement.Items.Single(i => i.Type == PaymentTransactionType.Payment)
            .ShouldBe(new PaymentSettlementItem("pay_sim1", PaymentTransactionType.Payment, null, "plink_sim1", 1000m, 23.60m, 3.60m, 976.40m, 0m, "simulator", settledAt.AddDays(-1)));
        settlement.Items.Single(i => i.Type == PaymentTransactionType.Refund).ShouldSatisfyAllConditions(
            r => r.EntityId.ShouldBe("rfnd_sim1"),
            r => r.PaymentId.ShouldBe("pay_sim0"),
            r => r.Debit.ShouldBe(200m));
    }
}
