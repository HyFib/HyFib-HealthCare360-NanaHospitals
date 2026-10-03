using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Scheduling;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>Payment-gateway reconciliation: settlements, their journal, exceptions, and following payments and refunds.</summary>
public sealed class GatewayReconciliationTests
{
    private static readonly Guid Main = Guid.Parse("01920000-0000-7000-8000-000000000001");
    private static readonly Guid Branch = Guid.Parse("01920000-0000-7000-8000-000000000002");
    private static readonly DateOnly Day = new(2026, 9, 25);
    private static readonly DateTimeOffset SettledAt = new(2026, 9, 25, 4, 30, 0, TimeSpan.Zero);

    private static PaymentSettlementItem Payment(string id, decimal amount, decimal fee = 0m, decimal tax = 0m, string? order = null) =>
        new(id, PaymentTransactionType.Payment, null, order, amount, fee, tax, amount - fee, 0m, "upi", SettledAt.AddDays(-1));

    private static PaymentSettlementItem Refund(string id, decimal amount, string paymentId) =>
        new(id, PaymentTransactionType.Refund, paymentId, null, amount, 0m, 0m, 0m, amount, "upi", SettledAt.AddHours(-20));

    private static GatewaySettlement Settlement(decimal amount, params PaymentSettlementItem[] items) =>
        GatewaySettlement.From(Main, "razorpay", new PaymentSettlement("setl_A1B2C3D4E5", "UTR001", PaymentSettlementStatus.Processed, amount, 0m, 0m, SettledAt, items),
            Day, SettledAt.AddHours(1));

    [Fact]
    public void A_settlement_adds_up_its_payments_refunds_and_charges()
    {
        var settlement = Settlement(
            1_000m - 23.60m + 500m - 11.80m - 200m,
            Payment("pay_A1B2C3D4E5", 1_000m, 23.60m, 3.60m),
            Payment("pay_F6G7H8I9J0", 500m, 11.80m, 1.80m),
            Refund("rfnd_K1L2M3N4O5", 200m, "pay_Z9Y8X7W6V5"));

        settlement.ShouldSatisfyAllConditions(
            s => s.PaymentsAmount.ShouldBe(1_500m),
            s => s.RefundsAmount.ShouldBe(200m),
            s => s.AdjustmentsAmount.ShouldBe(0m),
            s => s.Fees.ShouldBe(35.40m),
            s => s.Tax.ShouldBe(5.40m),
            s => s.Difference.ShouldBe(0m),
            s => s.SettledOn.ShouldBe(Day));
        settlement.Lines.Count.ShouldBe(3);
        settlement.Lines.Where(l => l.Type == PaymentTransactionType.Payment).ShouldAllBe(l => l.Match == SettlementMatch.NoReceipt);
        settlement.Lines.Single(l => l.Type == PaymentTransactionType.Refund).Match.ShouldBe(SettlementMatch.NoRefund);
        settlement.Lines.ShouldAllBe(l => l.GatewaySettlementId == settlement.Id && l.FacilityId == Main);
    }

    [Fact]
    public void Charges_on_the_settlement_itself_count_only_when_they_explain_the_payout()
    {
        var instant = GatewaySettlement.From(Main, "razorpay",
            new PaymentSettlement("setl_Instant0001", null, PaymentSettlementStatus.Processed, 976.40m - 11.80m, 11.80m, 1.80m, SettledAt,
                [Payment("pay_A1B2C3D4E5", 1_000m, 23.60m, 3.60m)]),
            Day, SettledAt);
        var repeated = GatewaySettlement.From(Main, "razorpay",
            new PaymentSettlement("setl_Repeated001", null, PaymentSettlementStatus.Processed, 976.40m, 23.60m, 3.60m, SettledAt,
                [Payment("pay_A1B2C3D4E5", 1_000m, 23.60m, 3.60m)]),
            Day, SettledAt);

        instant.ShouldSatisfyAllConditions(s => s.SettlementFees.ShouldBe(11.80m), s => s.Fees.ShouldBe(35.40m), s => s.Tax.ShouldBe(5.40m), s => s.Difference.ShouldBe(0m));
        repeated.ShouldSatisfyAllConditions(s => s.SettlementFees.ShouldBe(0m), s => s.Fees.ShouldBe(23.60m), s => s.Difference.ShouldBe(0m));
    }

    [Fact]
    public void A_payout_that_differs_from_its_transactions_shows_the_difference()
    {
        Settlement(900m, Payment("pay_A1B2C3D4E5", 1_000m, 23.60m, 3.60m)).Difference.ShouldBe(-76.40m);
    }

    [Fact]
    public void Matching_takes_the_receipts_facility_and_compares_the_amounts()
    {
        var settlement = Settlement(1_976.40m, Payment("pay_A1B2C3D4E5", 1_000m, 23.60m, 3.60m), Payment("pay_F6G7H8I9J0", 1_000m));
        var receiptId = Guid.CreateVersion7();

        settlement.Lines[0].MatchTo(receiptId, null, 1_000m, Branch);
        settlement.Lines[1].MatchTo(Guid.CreateVersion7(), null, 900m, Main);

        settlement.Lines[0].ShouldSatisfyAllConditions(l => l.Match.ShouldBe(SettlementMatch.Matched), l => l.ReceiptId.ShouldBe(receiptId), l => l.FacilityId.ShouldBe(Branch));
        settlement.Lines[1].Match.ShouldBe(SettlementMatch.AmountDiffers);

        settlement.Lines[0].Unmatched(null);
        settlement.Lines[0].ShouldSatisfyAllConditions(l => l.Match.ShouldBe(SettlementMatch.NoReceipt), l => l.ReceiptId.ShouldBeNull(), l => l.FacilityId.ShouldBe(Branch));
    }

    [Fact]
    public void A_settlement_of_one_facility_is_one_journal_portion()
    {
        var settlement = Settlement(976.40m - 200m, Payment("pay_A1B2C3D4E5", 1_000m, 23.60m, 3.60m), Refund("rfnd_K1L2M3N4O5", 200m, "pay_Z9Y8X7W6V5"));

        var portion = SettlementJournal.Portions(settlement).ShouldHaveSingleItem();

        portion.ShouldBe(new SettlementPortion(Main, true, 776.40m, 20m, 3.60m));
        portion.Gross.ShouldBe(800m);
        SettlementJournal.VoucherNumber(settlement, portion).ShouldBe("setl_A1B2C3D4E5");
    }

    [Fact]
    public void A_shared_accounts_settlement_is_split_by_the_facility_each_payment_belongs_to()
    {
        var settlement = Settlement(976.40m + 488.20m, Payment("pay_A1B2C3D4E5", 1_000m, 23.60m, 3.60m), Payment("pay_F6G7H8I9J0", 500m, 11.80m, 1.80m));
        settlement.Lines[1].MatchTo(Guid.CreateVersion7(), null, 500m, Branch);

        var portions = SettlementJournal.Portions(settlement);

        portions.Count.ShouldBe(2);
        portions.Sum(p => p.Net).ShouldBe(settlement.Amount);
        portions.Single(p => p.FacilityId == Branch).ShouldBe(new SettlementPortion(Branch, false, 488.20m, 10m, 1.80m));
        portions.Single(p => p.FacilityId == Main).Gross.ShouldBe(1_000m);
        SettlementJournal.VoucherNumber(settlement, portions.Single(p => p.FacilityId == Branch)).ShouldStartWith("setl_A1B2C3D4E5-");
    }

    [Fact]
    public void The_settlement_journal_clears_the_gateway_clearing_ledger_to_the_bank_charges_and_gst()
    {
        var day = new TallyDayBook(Day, [], [], [], [], [], [], [], [new TallyGatewaySettlement("setl_A1B2C3D4E5", Day, "setl_A1B2C3D4E5", "UTR001", 776.40m, 20m, 3.60m)]);
        var ledgers = new TallyLedgerMap { ModeLedgers = new Dictionary<string, string> { ["PaymentGateway"] = "Razorpay Clearing" }, GatewaySettlementBank = "HDFC Current A/c" };

        var voucher = TallyVoucherBuilder.Build(day, ledgers).ShouldHaveSingleItem();

        voucher.VoucherType.ShouldBe(TallyVoucherBuilder.JournalType);
        voucher.Number.ShouldBe("setl_A1B2C3D4E5");
        voucher.Narration.ShouldBe("Payment gateway settlement setl_A1B2C3D4E5, UTR UTR001");
        voucher.Entries.ShouldBe(
        [
            new AccountingEntry("HDFC Current A/c", 776.40m, true),
            new AccountingEntry("Payment Gateway Charges", 20m, true),
            new AccountingEntry("Input GST", 3.60m, true),
            new AccountingEntry("Razorpay Clearing", 800m, false),
        ]);
        day.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void A_settlement_that_returned_more_than_it_paid_debits_the_clearing_ledger()
    {
        var day = new TallyDayBook(Day, [], [], [], [], [], [], [], [new TallyGatewaySettlement("setl_A1B2C3D4E5", Day, "setl_A1B2C3D4E5", null, -200m, 0m, 0m)]);

        var voucher = TallyVoucherBuilder.Build(day, new TallyLedgerMap()).ShouldHaveSingleItem();

        voucher.Entries.ShouldBe([new AccountingEntry("Bank", 200m, false), new AccountingEntry("Payment Gateway Clearing", 200m, true)]);
    }

    [Fact]
    public void A_day_book_without_settlements_builds_as_before() =>
        TallyVoucherBuilder.Build(new TallyDayBook(Day, [], [], [], [], [], [], []), new TallyLedgerMap()).ShouldBeEmpty();

    [Fact]
    public void Exceptions_rank_worst_first_in_the_order_the_hospital_set()
    {
        Enum.GetValues<DiscrepancyKind>().OrderBy(GatewayDiscrepancy.RankOf).ShouldBe(
            [DiscrepancyKind.UnrecordedPayment, DiscrepancyKind.UnsettledReceipt, DiscrepancyKind.AmountMismatch, DiscrepancyKind.RefundMismatch]);
    }

    [Fact]
    public void An_exception_cleared_by_hc360_opens_again_when_its_cause_comes_back()
    {
        var exception = GatewayDiscrepancy.Open(Main, DiscrepancyKind.UnrecordedPayment, "pay_A1B2C3D4E5", "Paid but not recorded.", TestData.Now);

        exception.Clear("Receipt NHRC260000001 recorded.", TestData.Now.AddMinutes(5)).ShouldBeTrue();
        exception.ShouldSatisfyAllConditions(e => e.Status.ShouldBe(DiscrepancyStatus.Resolved), e => e.ResolvedAutomatically.ShouldBeTrue());

        exception.SeenAgain("Paid but not recorded again.", TestData.Now.AddHours(1));
        exception.ShouldSatisfyAllConditions(
            e => e.Status.ShouldBe(DiscrepancyStatus.Open),
            e => e.Resolution.ShouldBeNull(),
            e => e.Summary.ShouldBe("Paid but not recorded again."),
            e => e.LastSeenAt.ShouldBe(TestData.Now.AddHours(1)));
    }

    [Fact]
    public void An_exception_a_person_resolved_stays_resolved()
    {
        var exception = GatewayDiscrepancy.Open(Main, DiscrepancyKind.AmountMismatch, "adj_A1B2C3D4E5", "Adjusted by the gateway.", TestData.Now);
        var accountant = Guid.CreateVersion7();

        exception.Resolve(accountant, "Accountant", "  ", TestData.Now).Error!.Code.ShouldBe("validation");
        exception.Resolve(accountant, "Accountant", "Chargeback agreed with the bank.", TestData.Now).IsSuccess.ShouldBeTrue();
        exception.Resolve(accountant, "Accountant", "Again", TestData.Now).Error!.Code.ShouldBe("billing.discrepancy_closed");
        exception.SeenAgain("Adjusted by the gateway.", TestData.Now.AddDays(1));

        exception.ShouldSatisfyAllConditions(
            e => e.Status.ShouldBe(DiscrepancyStatus.Resolved),
            e => e.ResolvedBy.ShouldBe(accountant),
            e => e.Resolution.ShouldBe("Chargeback agreed with the bank."),
            e => e.ResolvedAutomatically.ShouldBeFalse());
    }

    [Fact]
    public void Recording_the_receipt_closes_the_exception_in_the_name_of_who_did_it()
    {
        var accountant = Guid.CreateVersion7();
        var open = GatewayDiscrepancy.Open(Main, DiscrepancyKind.UnrecordedPayment, "pay_A1B2C3D4E5", "Paid but not recorded.", TestData.Now);
        var clearedMeanwhile = GatewayDiscrepancy.Open(Main, DiscrepancyKind.UnrecordedPayment, "pay_F6G7H8I9J0", "Paid but not recorded.", TestData.Now);
        clearedMeanwhile.Clear("Receipt recorded.", TestData.Now);

        open.HandledBy(accountant, "Accountant", "Receipt NHRC260000002 recorded.", TestData.Now);
        clearedMeanwhile.HandledBy(accountant, "Accountant", "Receipt NHRC260000003 recorded.", TestData.Now);

        open.ShouldSatisfyAllConditions(e => e.Status.ShouldBe(DiscrepancyStatus.Resolved), e => e.ResolvedBy.ShouldBe(accountant), e => e.ResolvedAutomatically.ShouldBeFalse());
        clearedMeanwhile.ShouldSatisfyAllConditions(e => e.ResolvedBy.ShouldBe(accountant), e => e.Resolution.ShouldBe("Receipt NHRC260000003 recorded."));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(10, true)]
    [InlineData(115, true)]
    [InlineData(3 * 60, false)]
    [InlineData(3 * 60 + 60 * 24, false)]
    [InlineData(31 * 24 * 60, false)]
    public void Open_payments_are_asked_about_often_at_first_then_less(int minutesOld, bool due)
    {
        // 10:17 IST: not the first run of the hour.
        var now = new DateTimeOffset(2026, 10, 1, 4, 47, 0, TimeSpan.Zero);

        GatewayPolling.IsDue(now.AddMinutes(-minutesOld), now).ShouldBe(due);
    }

    [Fact]
    public void Older_open_payments_are_asked_about_hourly_then_every_six_hours()
    {
        var firstRunOfHour = new DateTimeOffset(2026, 10, 1, 5, 2, 0, TimeSpan.Zero);
        var sixHourly = new DateTimeOffset(2026, 10, 1, 6, 2, 0, TimeSpan.Zero);

        GatewayPolling.IsDue(firstRunOfHour.AddHours(-5), firstRunOfHour).ShouldBeTrue();
        GatewayPolling.IsDue(firstRunOfHour.AddDays(-3), firstRunOfHour).ShouldBeFalse();
        GatewayPolling.IsDue(sixHourly.AddDays(-3), sixHourly).ShouldBeTrue();
        GatewayReconciliationRunner.IsBookingDue(firstRunOfHour.AddDays(-5), firstRunOfHour).ShouldBeTrue();
        GatewayReconciliationRunner.IsBookingDue(firstRunOfHour.AddMinutes(-1), firstRunOfHour).ShouldBeFalse();
    }

    [Fact]
    public void A_link_nobody_paid_expires_but_money_taken_is_never_marked_expired()
    {
        var unpaid = Order();
        var paidButNotRecorded = Order();
        paidButNotRecorded.RecordCaptureFailure("pay_A1B2C3D4E5", 500m, "upi", TestData.Now, "This in-patient account is closed.");

        unpaid.MarkExpired("The payment link expired before it was paid.");
        paidButNotRecorded.MarkExpired("Nobody paid within 48 hours.");
        paidButNotRecorded.MarkCancelled(null);

        unpaid.ShouldSatisfyAllConditions(o => o.Status.ShouldBe(GatewayOrderStatus.Expired), o => o.LastError.ShouldBe("The payment link expired before it was paid."));
        paidButNotRecorded.ShouldSatisfyAllConditions(
            o => o.Status.ShouldBe(GatewayOrderStatus.Created),
            o => o.GatewayPaymentId.ShouldBe("pay_A1B2C3D4E5"),
            o => o.PaidAt.ShouldBe(TestData.Now),
            o => o.LastError.ShouldBe("Paid, but the receipt could not be recorded: This in-patient account is closed."));

        paidButNotRecorded.MarkPaid("pay_A1B2C3D4E5", 500m, "upi", Guid.CreateVersion7(), TestData.Now);
        paidButNotRecorded.ShouldSatisfyAllConditions(o => o.Status.ShouldBe(GatewayOrderStatus.Paid), o => o.LastError.ShouldBeNull());
    }

    [Fact]
    public void A_cancelled_link_is_marked_so_and_a_paid_order_never_changes()
    {
        var link = Order();
        var paid = Order();
        paid.MarkPaid("pay_A1B2C3D4E5", 500m, "upi", Guid.CreateVersion7(), TestData.Now);

        link.MarkCancelled(null);
        paid.MarkCancelled(null);
        paid.MarkExpired(null);

        link.Status.ShouldBe(GatewayOrderStatus.Cancelled);
        paid.Status.ShouldBe(GatewayOrderStatus.Paid);
    }

    [Fact]
    public void A_gateway_refund_is_followed_until_the_bank_confirms_it()
    {
        var refund = PaidRefund();
        refund.StartGatewayRefund("rfnd_A1B2C3D4E5", TestData.Now);

        refund.ApplyGatewayStatus(Status(PaymentRefundStatus.Pending, error: "Could not reach Razorpay."), TestData.Now).ShouldBeFalse();
        refund.ApplyGatewayStatus(Status(PaymentRefundStatus.Pending, reference: "74836287462837"), TestData.Now).ShouldBeTrue();
        refund.ApplyGatewayStatus(Status(PaymentRefundStatus.Processed), TestData.Now.AddDays(5)).ShouldBeTrue();
        refund.ApplyGatewayStatus(Status(PaymentRefundStatus.Failed), TestData.Now.AddDays(6)).ShouldBeFalse();

        refund.ShouldSatisfyAllConditions(
            r => r.GatewayStatus.ShouldBe(PaymentRefundStatus.Processed),
            r => r.GatewayReference.ShouldBe("74836287462837"),
            r => r.GatewaySettledAt.ShouldBe(TestData.Now.AddDays(5)),
            r => r.IsGatewayRefundOpen.ShouldBeFalse());
    }

    [Fact]
    public void A_failed_gateway_refund_keeps_the_reason_and_starts_again_when_sent_again()
    {
        var refund = PaidRefund();
        refund.StartGatewayRefund("rfnd_A1B2C3D4E5", TestData.Now);

        refund.ApplyGatewayStatus(Status(PaymentRefundStatus.Failed, error: "The bank account is closed."), TestData.Now).ShouldBeTrue();
        refund.ShouldSatisfyAllConditions(r => r.GatewayStatus.ShouldBe(PaymentRefundStatus.Failed), r => r.GatewayError.ShouldBe("The bank account is closed."));
        refund.ApplyGatewayStatus(Status(PaymentRefundStatus.Processed) with { RefundId = "rfnd_Other00001" }, TestData.Now).ShouldBeFalse();

        refund.StartGatewayRefund("rfnd_Again00001", TestData.Now.AddDays(40));
        refund.ShouldSatisfyAllConditions(
            r => r.GatewayRefundId.ShouldBe("rfnd_Again00001"),
            r => r.GatewayStatus.ShouldBe(PaymentRefundStatus.Pending),
            r => r.GatewayError.ShouldBeNull(),
            r => r.GatewayRefundStartedAt.ShouldBe(TestData.Now.AddDays(40)),
            r => r.IsGatewayRefundOpen.ShouldBeTrue());
    }

    [Fact]
    public void A_failed_gateway_refund_is_sent_again_by_one_request_only()
    {
        var refund = PaidRefund();
        refund.StartGatewayRefund("rfnd_A1B2C3D4E5", TestData.Now);
        refund.ApplyGatewayStatus(Status(PaymentRefundStatus.Failed, error: "The bank account is closed."), TestData.Now).ShouldBeTrue();

        refund.ClaimGatewayRetry(TestData.Now.AddHours(1)).IsSuccess.ShouldBeTrue();

        refund.ShouldSatisfyAllConditions(
            r => r.GatewayRetryStartedAt.ShouldBe(TestData.Now.AddHours(1)),
            r => r.IsGatewayRefundOpen.ShouldBeFalse("A refund being sent again is not followed."),
            r => r.ClaimGatewayRetry(TestData.Now.AddHours(1).AddMinutes(1)).Error!.Code.ShouldBe("billing.refund_retrying"),
            r => r.CanSendGatewayRefundAgain(TestData.Now.AddHours(1).AddMinutes(4)).ShouldBeFalse(),
            r => r.CanSendGatewayRefundAgain(TestData.Now.AddHours(1) + Domain.Refund.GatewayRetryTimeout).ShouldBeTrue(
                "A retry that stopped part way can be taken over: the gateway gets the same idempotency key."));

        refund.GatewayRetryRefused("Razorpay: The payment has been fully refunded.");
        refund.ShouldSatisfyAllConditions(
            r => r.GatewayStatus.ShouldBe(PaymentRefundStatus.Failed),
            r => r.GatewayRetryStartedAt.ShouldBeNull(),
            r => r.GatewayError.ShouldBe("Razorpay: The payment has been fully refunded."),
            r => r.CanSendGatewayRefundAgain(TestData.Now.AddHours(2)).ShouldBeTrue());
    }

    [Fact]
    public void Each_gateway_attempt_of_a_refund_has_its_own_stable_idempotency_key()
    {
        var refund = PaidRefund();
        refund.GatewayRefundId = null;
        var first = refund.GatewayIdempotencyKey;
        refund.StartGatewayRefund("rfnd_A1B2C3D4E5", TestData.Now);
        var retry = refund.GatewayIdempotencyKey;
        refund.StartGatewayRefund("rfnd_Again00001", TestData.Now);

        first.ShouldBe(refund.Id.ToString("N"));
        retry.ShouldNotBe(first);
        retry.ShouldMatch("^[0-9a-f]{32}$");
        refund.GatewayIdempotencyKey.ShouldNotBe(retry);
        refund.StartGatewayRefund("rfnd_A1B2C3D4E5", TestData.Now);
        refund.GatewayIdempotencyKey.ShouldBe(retry, "The same attempt asked again gets the same key.");
    }

    [Fact]
    public void Only_a_failed_gateway_refund_can_be_sent_again()
    {
        var refund = PaidRefund();
        refund.StartGatewayRefund("rfnd_A1B2C3D4E5", TestData.Now);

        refund.CanSendGatewayRefundAgain(TestData.Now).ShouldBeFalse();
        refund.ClaimGatewayRetry(TestData.Now).Error!.Code.ShouldBe("billing.refund_not_failed");
        refund.GatewayRetryStartedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData(4, 47)]
    [InlineData(5, 2)]
    [InlineData(6, 2)]
    public void The_due_windows_follow_the_polling_schedule(int hour, int minute)
    {
        var now = new DateTimeOffset(2026, 10, 1, hour, minute, 0, TimeSpan.Zero);
        var windows = GatewayPolling.DueWindows(now);

        foreach (var minutesOld in new[] { 0, 1, 2, 10, 119, 120, 121, 60 * 24, 60 * 48 - 1, 60 * 48, 60 * 48 + 1, 60 * 24 * 10, 60 * 24 * 30 - 1, 60 * 24 * 30, 60 * 24 * 31 })
        {
            var since = now.AddMinutes(-minutesOld);
            windows.Any(w => since > w.After && since <= w.Until).ShouldBe(GatewayPolling.IsDue(since, now), $"{minutesOld} minutes old");
        }

        windows.Count.ShouldBe(hour switch { 4 => 1, 5 => 2, _ => 3 });
    }

    [Fact]
    public void A_facility_sharing_a_merchant_account_sees_its_own_part_of_a_settlement()
    {
        var settlement = Settlement(976.40m + 488.20m - 200m,
            Payment("pay_A1B2C3D4E5", 1_000m, 23.60m, 3.60m), Payment("pay_F6G7H8I9J0", 500m, 11.80m, 1.80m), Refund("rfnd_K1L2M3N4O5", 200m, "pay_Z9Y8X7W6V5"));
        settlement.Lines[1].MatchTo(Guid.CreateVersion7(), null, 500m, Branch);
        settlement.Lines[2].MatchTo(null, Guid.CreateVersion7(), 150m, Branch);

        var share = GatewaySettlementService.ShareDto(settlement, settlement.Lines.Where(l => l.FacilityId == Branch).ToList());

        share.ShouldSatisfyAllConditions(
            s => s.IsShare.ShouldBeTrue(),
            s => s.PaymentsAmount.ShouldBe(500m),
            s => s.RefundsAmount.ShouldBe(200m),
            s => s.Charges.ShouldBe(10m),
            s => s.Tax.ShouldBe(1.80m),
            s => s.Amount.ShouldBe(288.20m),
            s => s.Difference.ShouldBe(0m),
            s => s.Lines.ShouldBe(2),
            s => s.Unmatched.ShouldBe(1));
        share.Amount.ShouldBe(SettlementJournal.Portions(settlement).Single(p => p.FacilityId == Branch).Net);
        GatewaySettlementService.ToDto(settlement, 3, 1).ShouldSatisfyAllConditions(s => s.IsShare.ShouldBeFalse(), s => s.Amount.ShouldBe(settlement.Amount));
    }

    [Fact]
    public void A_webhook_yields_its_event_and_the_gateway_ids_only()
    {
        const string captured = """
            {"entity":"event","account_id":"acc_BFQ7uQEaa7j2z7","event":"payment.captured","contains":["payment"],
             "payload":{"payment":{"entity":{"id":"pay_DESlfW9H8K9uqM","order_id":"order_DESlLckIVRkHWj","amount":100,"email":"gaurav.kumar@example.com",
             "contact":"+919876543210","card":{"last4":"1111","network":"Visa"}}}},"created_at":1567674606}
            """;
        const string refund = """
            {"event":"refund.processed","payload":{"refund":{"entity":{"id":"rfnd_DGRcGzZSLyEdg1","payment_id":"pay_DESlfW9H8K9uqM"}},
             "payment":{"entity":{"id":"pay_DESlfW9H8K9uqM","order_id":"order_DESlLckIVRkHWj"}}}}
            """;

        GatewayWebhookInfo.Read(captured).ShouldBe(new GatewayWebhookInfo("payment.captured", "order_DESlLckIVRkHWj", "pay_DESlfW9H8K9uqM", null, null));
        GatewayWebhookInfo.Read(refund).ShouldBe(new GatewayWebhookInfo("refund.processed", "order_DESlLckIVRkHWj", "pay_DESlfW9H8K9uqM", "rfnd_DGRcGzZSLyEdg1", null));
        GatewayWebhookInfo.Read("""{"event":"settlement.processed","payload":{"settlement":{"entity":{"id":"setl_DGlQ1Rj8os78Ec"}}}}""").SettlementId.ShouldBe("setl_DGlQ1Rj8os78Ec");
        GatewayWebhookInfo.Read("""{"event":"<script>","payload":{"payment":{"entity":{"id":"pay_1'; DROP"}}}}""").ShouldBe(new GatewayWebhookInfo(null, null, null, null, null));
        GatewayWebhookInfo.Read("not json").ShouldBe(new GatewayWebhookInfo(null, null, null, null, null));
    }

    [Fact]
    public void A_webhook_is_known_by_its_event_id_else_by_its_body()
    {
        GatewayWebhookInfo.EventIdOf("KE2cmXtQi5JDdj", "{}").ShouldBe("KE2cmXtQi5JDdj");
        GatewayWebhookInfo.EventIdOf(null, "{\"a\":1}").ShouldBe(GatewayWebhookInfo.EventIdOf("", "{\"a\":1}"));
        GatewayWebhookInfo.EventIdOf("id with spaces", "{\"a\":1}").ShouldStartWith("sha256:");
        GatewayWebhookInfo.EventIdOf(null, "{\"a\":1}").ShouldNotBe(GatewayWebhookInfo.EventIdOf(null, "{\"a\":2}"));
    }

    [Fact]
    public void A_new_settlement_and_its_lines_are_inserted()
    {
        using var db = ModelOnlyContext();
        var settlement = Settlement(976.40m, Payment("pay_A1B2C3D4E5", 1_000m, 23.60m, 3.60m));

        db.Add(settlement);
        db.ChangeTracker.DetectChanges();

        db.Entry(settlement).State.ShouldBe(EntityState.Added);
        db.Entry(settlement.Lines.ShouldHaveSingleItem()).State.ShouldBe(EntityState.Added);
    }

    private static GatewayOrder Order() => new()
    {
        FacilityId = Main,
        Provider = "razorpay",
        Kind = GatewayOrderKind.PaymentLink,
        OrderId = "plink_A1B2C3D4E5",
        Amount = 500m,
        Status = GatewayOrderStatus.Created,
    };

    private static Refund PaidRefund()
    {
        var refund = Domain.Refund.Request(Main, "NHRF26000001", RefundPurpose.Bill, TestData.PatientId, Guid.CreateVersion7(), null, 500m, "Duplicate payment",
            Guid.CreateVersion7(), "Cashier", TestData.Now);
        refund.Approve(Guid.CreateVersion7(), null, TestData.Now).IsSuccess.ShouldBeTrue();
        refund.MarkPaid(PaymentMode.PaymentGateway, "rfnd_A1B2C3D4E5", null, null, null, TestData.Now).IsSuccess.ShouldBeTrue();
        return refund;
    }

    private static RefundStatusResult Status(PaymentRefundStatus status, string? reference = null, string? error = null) =>
        new("rfnd_A1B2C3D4E5", status, 500m, "pay_A1B2C3D4E5", reference, TestData.Now, error);

    private static HospitalDbContext ModelOnlyContext() => new(
        new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options,
        new ModuleCatalog([new PatientsModule(), new SchedulingModule(), new BillingModule()]));
}
