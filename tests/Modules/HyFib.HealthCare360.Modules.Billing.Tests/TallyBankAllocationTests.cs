using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Tally;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// The cheque or UTR on the lines of bank ledgers (T15: "Yes"), for the bank reconciliation in Tally: only ledgers the
/// facility lists as bank accounts carry them, by default the ledgers HC360 posts bank money to.
/// </summary>
public sealed class TallyBankAllocationTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);
    private static readonly TallyLedgerMap Ledgers = new();

    private static TallyDayBook Book(
        IReadOnlyList<TallyReceipt>? receipts = null,
        IReadOnlyList<TallyRefund>? refunds = null,
        IReadOnlyList<TallyPayout>? payouts = null,
        IReadOnlyList<TallyGatewaySettlement>? gateway = null,
        IReadOnlyList<TallyPosSettlement>? pos = null,
        IReadOnlyList<TallyReceiptReversal>? reversals = null) =>
        new(Day, [], receipts ?? [], [], refunds ?? [], [], [], payouts ?? [], gateway)
        {
            PosSettlements = pos ?? [],
            ReceiptReversals = reversals ?? [],
        };

    private static AccountingEntry Entry(AccountingVoucher voucher, string ledger) => voucher.Entries.Single(e => e.LedgerName == ledger);

    private static readonly TallyPayment Transfer = new(PaymentMode.NetBanking, 3_000m) { Reference = "HDFCR52026092512345" };

    private static readonly TallyPayment Cheque = new(PaymentMode.Cheque, 2_000m)
    {
        ChequeNumber = "123456",
        ChequeBank = "State Bank of India",
        ChequeDate = new DateOnly(2026, 9, 20),
    };

    private static void ShouldAddUp(IEnumerable<AccountingVoucher> vouchers)
    {
        foreach (var voucher in vouchers)
        {
            voucher.Entries.Where(e => e.IsDebit).Sum(e => e.Amount).ShouldBe(voucher.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount), voucher.Number);
            foreach (var entry in voucher.Entries.Where(e => e.BankAllocations.Count > 0))
            {
                entry.BankAllocations.Sum(b => b.Amount).ShouldBe(entry.Amount, $"{voucher.Number}: {entry.LedgerName}");
            }
        }
    }

    [Fact]
    public void The_bank_ledgers_are_by_default_those_hc360_posts_bank_money_to()
    {
        Ledgers.DefaultBankLedgers().ShouldBe(["Bank"]);
        var named = Ledgers with
        {
            ModeLedgers = new Dictionary<string, string> { ["NetBanking"] = "HDFC Current A/c", ["Cheque"] = "Cheques in Hand" },
            PayoutBank = "SBI Current A/c",
            GatewaySettlementBank = "hdfc current a/c",
            PosSettlementBank = "ICICI Current A/c",
        };

        named.DefaultBankLedgers().ShouldBe(["HDFC Current A/c", "Bank", "SBI Current A/c", "ICICI Current A/c"]);
        named.IsBankLedger("Bank").ShouldBeTrue();
        named.IsBankLedger(" icici current a/c ").ShouldBeTrue();
        named.IsBankLedger("Cheques in Hand").ShouldBeFalse();
        (named with { BankLedgers = ["Cheques in Hand"] }).IsBankLedger("Bank").ShouldBeFalse();
    }

    [Fact]
    public void A_transfer_into_a_bank_ledger_carries_its_utr_and_a_cheque_in_hand_carries_nothing()
    {
        var voucher = TallyVoucherBuilder.Build(Book(receipts:
            [new TallyReceipt("NHRC260000001", Day, ReceiptPurpose.BillPayment, null, [Transfer, Cheque, new TallyPayment(PaymentMode.Cash, 500m)])]), Ledgers).Single();

        Entry(voucher, "Bank").BankAllocations.ShouldBe([new AccountingBankAllocation(Day, AccountingBankTransaction.EFundTransfer, 3_000m, "HDFCR52026092512345")]);
        Entry(voucher, "Cheques in Hand").BankAllocations.ShouldBeEmpty();
        Entry(voucher, "Cash").BankAllocations.ShouldBeEmpty();
        Entry(voucher, "Patient Receivables").BankAllocations.ShouldBeEmpty();
        ShouldAddUp([voucher]);
    }

    [Fact]
    public void A_cheque_ledger_carries_the_cheques_number_date_and_bank_only_when_it_is_listed_as_a_bank_ledger()
    {
        var listed = Ledgers with { BankLedgers = ["Bank", "Cheques in Hand"] };

        var voucher = TallyVoucherBuilder.Build(Book(receipts:
            [new TallyReceipt("NHRC260000001", Day, ReceiptPurpose.Deposit, null, [Cheque])]), listed).Single();

        Entry(voucher, "Cheques in Hand").BankAllocations.ShouldBe(
            [new AccountingBankAllocation(Day, AccountingBankTransaction.Cheque, 2_000m, "123456", new DateOnly(2026, 9, 20), "State Bank of India")]);
    }

    [Fact]
    public void Payments_that_reach_the_same_bank_ledger_keep_one_part_each()
    {
        var oneBank = Ledgers with { ModeLedgers = new Dictionary<string, string> { ["Cheque"] = "Bank" } };

        var voucher = TallyVoucherBuilder.Build(Book(receipts:
            [new TallyReceipt("NHRC260000001", Day, ReceiptPurpose.BillPayment, null, [Transfer, Cheque])]), oneBank).Single();

        var bank = Entry(voucher, "Bank");
        bank.Amount.ShouldBe(5_000m);
        bank.BankAllocations.Select(b => (b.TransactionType, b.InstrumentNumber, b.Amount))
            .ShouldBe([(AccountingBankTransaction.EFundTransfer, "HDFCR52026092512345", 3_000m), (AccountingBankTransaction.Cheque, "123456", 2_000m)]);
    }

    [Fact]
    public void An_insurers_settlement_carries_its_utr()
    {
        var voucher = TallyVoucherBuilder.Build(Book(receipts:
        [
            new TallyReceipt("NHRC260000002", Day, ReceiptPurpose.PayerSettlement, "Star Health", [new TallyPayment(PaymentMode.Insurance, 45_000m) { Reference = "SBIN526268000123" }])
            {
                BillNumber = "NHIB260000004",
            },
        ]), Ledgers).Single();

        Entry(voucher, "Bank").BankAllocations.ShouldBe([new AccountingBankAllocation(Day, AccountingBankTransaction.EFundTransfer, 45_000m, "SBIN526268000123")]);
    }

    [Fact]
    public void A_refund_paid_from_the_bank_carries_its_reference()
    {
        var vouchers = TallyVoucherBuilder.Build(Book(refunds:
        [
            new TallyRefund("NHRF260000001", Day, RefundPurpose.Deposit, PaymentMode.NetBanking, 2_000m) { Reference = "UTR998877" },
            new TallyRefund("NHRF260000002", Day, RefundPurpose.Bill, PaymentMode.Cash, 300m) { Reference = "ignored" },
        ]), Ledgers);

        var bank = Entry(vouchers.Single(v => v.Number == "NHRF260000001"), "Bank");
        bank.IsDebit.ShouldBeFalse();
        bank.BankAllocations.ShouldBe([new AccountingBankAllocation(Day, AccountingBankTransaction.EFundTransfer, 2_000m, "UTR998877")]);
        Entry(vouchers.Single(v => v.Number == "NHRF260000002"), "Cash").BankAllocations.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(PaymentMode.NetBanking, AccountingBankTransaction.EFundTransfer)]
    [InlineData(PaymentMode.Upi, AccountingBankTransaction.EFundTransfer)]
    [InlineData(PaymentMode.Cheque, AccountingBankTransaction.Cheque)]
    [InlineData(PaymentMode.Cash, AccountingBankTransaction.Others)]
    public void A_doctors_payout_carries_how_it_was_paid(PaymentMode mode, AccountingBankTransaction transaction)
    {
        var voucher = TallyVoucherBuilder.Build(Book(payouts:
            [new TallyPayout("NHDP2600001", Day, "Dr. Arjun Mehta", 50_000m, 5_000m, 2_000m, 43_000m) { Mode = mode, Reference = "N26092500112233" }]), Ledgers).Single();

        Entry(voucher, "Bank").BankAllocations.ShouldBe([new AccountingBankAllocation(Day, transaction, 43_000m, "N26092500112233")]);
        ShouldAddUp([voucher]);
    }

    [Fact]
    public void A_settlement_into_the_bank_carries_its_utr_and_its_reversal_takes_it_back()
    {
        var settlement = new TallyGatewaySettlement("PG-NH-1", Day, "setl_9QJ", "UTIBR52026092500001", 9_764m, 200m, 36m);
        var reversal = new TallyGatewaySettlement("PG-NH-1-R1", Day, "setl_9QJ", "UTIBR52026092500001", -9_764m, -200m, -36m) { Correction = "Reverses journal PG-NH-1" };
        var noUtr = new TallyGatewaySettlement("PG-NH-2", Day, "setl_9QK", null, 1_000m, 0m, 0m);
        var pos = new TallyPosSettlement("POS-NH-1", Day, "ICICR52026092500002", 4_910m, 80m, 10m, [new TallyPosClearing(PaymentMode.Card, 5_000m)]);

        var vouchers = TallyVoucherBuilder.Build(Book(gateway: [settlement, reversal, noUtr], pos: [pos]), Ledgers);

        Entry(vouchers.Single(v => v.Number == "PG-NH-1"), "Bank").ShouldSatisfyAllConditions(
            e => e.IsDebit.ShouldBeTrue(),
            e => e.BankAllocations.ShouldBe([new AccountingBankAllocation(Day, AccountingBankTransaction.EFundTransfer, 9_764m, "UTIBR52026092500001")]));
        Entry(vouchers.Single(v => v.Number == "PG-NH-1-R1"), "Bank").ShouldSatisfyAllConditions(
            e => e.IsDebit.ShouldBeFalse(),
            e => e.BankAllocations.ShouldBe([new AccountingBankAllocation(Day, AccountingBankTransaction.EFundTransfer, 9_764m, "UTIBR52026092500001")]));
        Entry(vouchers.Single(v => v.Number == "PG-NH-2"), "Bank").BankAllocations.ShouldBe([new AccountingBankAllocation(Day, AccountingBankTransaction.Others, 1_000m)]);
        Entry(vouchers.Single(v => v.Number == "POS-NH-1"), "Bank").BankAllocations
            .ShouldBe([new AccountingBankAllocation(Day, AccountingBankTransaction.EFundTransfer, 4_910m, "ICICR52026092500002")]);
        ShouldAddUp(vouchers);
    }

    [Fact]
    public void A_reversal_of_money_not_received_carries_no_bank_details()
    {
        var voucher = TallyVoucherBuilder.Build(Book(reversals:
            [new TallyReceiptReversal("NHRR260000001", Day, "NHRC260000001", ReceiptPurpose.BillPayment, null, [new TallyPayment(PaymentMode.NetBanking, 3_000m)])]), Ledgers)
            .Single();

        Entry(voucher, "Bank").BankAllocations.ShouldBeEmpty();
    }

    [Fact]
    public void A_facility_that_lists_no_bank_ledgers_gets_no_bank_details()
    {
        var none = Ledgers with { BankLedgers = [] };

        var vouchers = TallyVoucherBuilder.Build(Book(
            receipts: [new TallyReceipt("NHRC260000001", Day, ReceiptPurpose.BillPayment, null, [Transfer])],
            payouts: [new TallyPayout("NHDP2600001", Day, "Dr. Arjun Mehta", 1_000m, 0m, 0m, 1_000m) { Mode = PaymentMode.NetBanking, Reference = "N1" }]), none);

        vouchers.SelectMany(v => v.Entries).ShouldAllBe(e => e.BankAllocations.Count == 0);
    }

    [Fact]
    public void The_bank_ledgers_come_from_the_settings_and_are_checked_and_tidied()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var validator = new TallyLedgerMapValidator();

        JsonSerializer.Deserialize<TallyLedgerMap>("{}", web)!.BankLedgers.ShouldBeNull();
        JsonSerializer.Deserialize<TallyLedgerMap>("""{"bankLedgers":["HDFC Current A/c"]}""", web)!.BankLedgers.ShouldBe(["HDFC Current A/c"]);
        validator.Validate(new TallyLedgerMap { BankLedgers = ["HDFC Current A/c", "SBI\nCurrent"] }).Errors.ShouldHaveSingleItem()
            .PropertyName.ShouldBe("BankLedgers[1]");
        validator.Validate(new TallyLedgerMap { BankLedgers = [.. Enumerable.Range(1, 51).Select(i => $"Bank {i}")] }).IsValid.ShouldBeFalse();
        validator.Validate(new TallyLedgerMap { BankLedgers = [] }).IsValid.ShouldBeTrue();
        TallyLedgerMaps.Normalize(new TallyLedgerMap { BankLedgers = [" HDFC Current A/c ", "hdfc current a/c", " "] }).BankLedgers.ShouldBe(["HDFC Current A/c"]);
        TallyLedgerMaps.Normalize(new TallyLedgerMap()).BankLedgers.ShouldBeNull();
    }

    [Fact]
    public void Bank_details_are_part_of_what_was_sent()
    {
        var voucher = TallyVoucherBuilder.Build(Book(receipts: [new TallyReceipt("NHRC260000001", Day, ReceiptPurpose.BillPayment, null, [Transfer])]), Ledgers).Single();
        var otherUtr = TallyVoucherBuilder.Build(Book(receipts:
            [new TallyReceipt("NHRC260000001", Day, ReceiptPurpose.BillPayment, null, [Transfer with { Reference = "HDFCR52026092599999" }])]), Ledgers).Single();

        TallyTracking.Fingerprint(voucher).ShouldNotBe(TallyTracking.Fingerprint(otherUtr));
    }
}
