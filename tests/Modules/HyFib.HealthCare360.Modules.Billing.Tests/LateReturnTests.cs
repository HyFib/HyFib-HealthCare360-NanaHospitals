using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.Modules.Billing.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A return after the final bill (an unopened ₹3,000 injection the ward sends back, say). After a self-paying final bill
/// it goes on the supplementary bill and the patient is refunded. After a final bill an insurer or a package shared, that
/// bill of returns was self-pay: the payer's share is clamped at nothing on it, so the patient was refunded what the
/// insurer or the package paid. Such a return is credited on the final bill instead, split as the bill split the sale.
/// The return of something issued after the final bill still goes with it on the supplementary bill.
/// </summary>
public sealed class LateReturnTests
{
    private static readonly Guid Medicines = Guid.CreateVersion7();
    private const string Injection = "Inj. Meropenem 1 g (batch MR2611)";

    [Fact]
    public void After_an_insured_final_bill_a_return_stays_off_the_supplementary_bill_and_after_a_self_paying_one_it_goes_on_it()
    {
        var late = new[] { Return(), Charge(500m, category: ServiceCategory.DoctorVisit) };

        // Insured: the supplementary bill takes the late visit, and the return waits for the final bill's credit note.
        ReturnCredits.ForNewBill(FinalBill(Insurer()), late, []).ShouldBe([late[1]]);

        // Self-paying: both go on the supplementary bill, and the patient is refunded the return as before.
        ReturnCredits.ForNewBill(FinalBill(null), late, []).ShouldBe(late);
        ReturnCredits.ForNewBill(null, late, []).ShouldBe(late);

        // What the self-pay supplementary bill made of the return after an insured stay: a refund to the patient.
        var supplementary = Bill.Draft(FacilityId, BillType.InPatient, false, PatientId, null, null, null, BillPayerTerms.SelfPay(PayerType.Insurance, null));
        supplementary.AddCharge(Return(), true, null).IsSuccess.ShouldBeTrue();
        supplementary.Finalize("NHIB260000009", null, Now).IsSuccess.ShouldBeTrue();
        (supplementary.PatientAmount, supplementary.RefundableAmount).ShouldBe((-3_000m, 3_000m), "the defect: kept off it now");
    }

    [Fact]
    public void A_final_bill_with_a_package_credits_its_returns_too()
    {
        var packaged = FinalBill(null, coveredByPackage: true);

        ReturnCredits.CreditedOnTheBill(packaged).ShouldBeTrue();
        ReturnCredits.CreditedOnTheBill(FinalBill(null)).ShouldBeFalse();
        ReturnCredits.ForNewBill(packaged, [Return()], []).ShouldBeEmpty();
    }

    [Fact]
    public void A_return_of_something_issued_after_the_final_bill_goes_on_the_supplementary_bill_with_it()
    {
        // After the insured final bill the ward is issued two vials of another batch and sends one back. The insurer paid
        // for none of them: the return goes with the issue on the supplementary bill, and is not credited to the insurer.
        var bill = FinalBill(Insurer());
        const string later = "Inj. Meropenem 1 g (batch MR2612)";
        var issued = Charge(3_000m, quantity: 2m, category: ServiceCategory.Consumable, serviceItemId: Medicines, serviceDate: Now.AddDays(1));
        issued.Description = later;
        var returned = Charge(3_000m, quantity: -1m, category: ServiceCategory.Consumable, serviceItemId: Medicines, serviceDate: Now.AddDays(1));
        returned.Description = "Returned: " + later;
        ChargedItem[] chargedSince = [new(Medicines, later)];

        ReturnCredits.IsCreditedOn(bill, returned, chargedSince).ShouldBeFalse();
        ReturnCredits.ForNewBill(bill, [issued, returned], chargedSince).ShouldBe([issued, returned]);

        // The vial the final bill charged is still credited on it, whatever else of the same service was issued since.
        ReturnCredits.IsCreditedOn(bill, Return(), chargedSince).ShouldBeTrue();
        ReturnCredits.ForNewBill(bill, [issued, Return()], chargedSince).ShouldBe([issued]);
    }

    [Fact]
    public void A_return_named_as_nothing_charged_is_credited_on_the_bill_that_charged_its_service_and_no_other_bill_did()
    {
        var bill = FinalBill(Insurer());
        var returned = Return();
        returned.Description = "Returned: Inj. Meropenem 1 g";

        ReturnCredits.IsCreditedOn(bill, returned, []).ShouldBeTrue();
        ReturnCredits.IsCreditedOn(bill, returned, [new ChargedItem(Medicines, "Inj. Meropenem 1 g")]).ShouldBeFalse("charged since under that name");
        returned.ServiceItemId = Guid.CreateVersion7();
        ReturnCredits.IsCreditedOn(bill, returned, []).ShouldBeFalse("a service the bill did not charge");
        ReturnCredits.IsCreditedOn(FinalBill(null), Return(), []).ShouldBeFalse("a self-paying bill");
    }

    [Fact]
    public void The_insurers_share_of_a_return_goes_back_to_the_insurer_and_nothing_to_the_patient()
    {
        var bill = FinalBill(Insurer());
        (bill.PayerAmount, bill.PatientAmount).ShouldBe((50_000m, 0m));

        var credit = Split(bill, Return()).ShouldNotBeNull();

        (credit.Payer, credit.Patient, credit.Amount).ShouldBe((3_000m, 0m, 3_000m));
        credit.Lines.ShouldHaveSingleItem().ShouldBe(new ReturnCreditLine(InjectionLine(bill).Id, 3_000m, 0m), "the line that charged the injection");
    }

    [Fact]
    public void With_a_co_pay_the_return_is_split_as_the_sale_was()
    {
        var bill = FinalBill(Insurer(coPay: 10m));
        (bill.PayerAmount, bill.PatientAmount).ShouldBe((45_000m, 5_000m));

        var credit = Split(bill, Return()).ShouldNotBeNull();

        (credit.Payer, credit.Patient).ShouldBe((2_700m, 300m));
        credit.Returns.ShouldHaveSingleItem().ShouldSatisfyAllConditions(r => r.Payer.ShouldBe(2_700m), r => r.Patient.ShouldBe(300m));
    }

    [Fact]
    public void A_payer_capped_at_its_approved_amount_still_pays_it_so_the_return_is_the_patients()
    {
        var bill = FinalBill(Insurer(cap: 40_000m));
        (bill.PayerAmount, bill.PatientAmount).ShouldBe((40_000m, 10_000m));

        var credit = Split(bill, Return()).ShouldNotBeNull();

        (credit.Payer, credit.Patient).ShouldBe((0m, 3_000m));
    }

    [Fact]
    public void A_return_the_package_absorbed_gives_its_allowance_back_and_nobody_money()
    {
        var bill = FinalBill(Insurer(), coveredByPackage: true);
        InjectionLine(bill).NetAmount.ShouldBe(0m, "the package absorbed the injection");

        var credit = Split(bill, Return(), packageCovered: -3_000m).ShouldNotBeNull();

        (credit.Payer, credit.Patient).ShouldBe((0m, 0m));
        credit.Lines.ShouldBeEmpty();
    }

    [Fact]
    public void A_return_of_a_service_not_on_the_bill_is_not_credited_on_it()
    {
        var bill = FinalBill(Insurer());
        var other = Return();
        other.ServiceItemId = Guid.CreateVersion7();

        var refused = ReturnCredits.Split(bill, [Late(other, 0m, true)], new Dictionary<Guid, decimal>());

        refused.Error!.Code.ShouldBe("billing.return_not_on_bill");
    }

    [Fact]
    public void A_line_credited_already_takes_no_more_than_is_left_of_it()
    {
        var bill = FinalBill(Insurer());
        var injection = InjectionLine(bill);

        var refused = ReturnCredits.Split(bill, [Late(Return(), 0m, true)], new Dictionary<Guid, decimal> { [injection.Id] = 1_000m });

        refused.Error!.Code.ShouldBe("billing.return_not_on_bill");
    }

    [Fact]
    public void The_payers_share_comes_off_what_it_owes_and_counts_with_what_it_will_not_pay()
    {
        var bill = FinalBill(Insurer());

        bill.ApplyPayerCredit(3_000m).IsSuccess.ShouldBeTrue();

        (bill.PayerOutstanding, bill.PayerDisallowedAmount, bill.RefundableAmount).ShouldBe((47_000m, 3_000m, 0m));
        bill.ApplyPayerCredit(47_000.01m).Error!.Code.ShouldBe("billing.payer_credit_exceeds_owed");
        bill.ApplyPayerCredit(0m).Error!.Code.ShouldBe("billing.amount_invalid");
        FinalBill(null).ApplyPayerCredit(10m).Error!.Code.ShouldBe("billing.no_payer");
    }

    [Fact]
    public void Returns_waiting_on_the_final_bill_hold_up_no_discharge_and_say_so()
    {
        var clearance = DischargeClearanceRules.Evaluate(new AdmissionBillingPosition(0, 0m, true, true, 0m, 0m, false, ReturnsToCreditCount: 1, ReturnsToCreditAmount: 3_000m));

        clearance.Cleared.ShouldBeTrue();
        clearance.Outstanding.ShouldBe(0m);
        clearance.Reason.ShouldBe("A return worth ₹3,000.00 waits to be credited on the final bill.");
    }

    [Fact]
    public void A_payers_credit_note_for_returns_comes_off_its_ledger_against_the_bill()
    {
        var day = new TallyDayBook(new DateOnly(2026, 9, 26), [], [], [], [],
            [new TallyCreditNote("NHCN26000031", new DateOnly(2026, 9, 26), CreditNoteKind.PayerReturn, 3_000m) { PayerName = "Star Health", BillNumber = "NHIB260000001" }],
            [], []);

        var voucher = TallyVoucherBuilder.Build(day, new TallyLedgerMap()).ShouldHaveSingleItem();

        voucher.VoucherType.ShouldBe(TallyVoucherBuilder.CreditNoteType);
        var payer = voucher.Entries.Single(e => e.LedgerName == "Star Health");
        (payer.Amount, payer.IsDebit).ShouldBe((3_000m, false));
        payer.BillAllocations.ShouldBe([new AccountingBillAllocation("NHIB260000001", AccountingBillType.AgainstRef, 3_000m)]);
        voucher.Entries.Single(e => e.LedgerName == "Sales Returns & Allowances").IsDebit.ShouldBeTrue();
        voucher.Entries.ShouldNotContain(e => e.LedgerName == "Insurance Disallowances" || e.LedgerName == "Patient Receivables");
    }

    private static BillPayerTerms Insurer(decimal coPay = 0m, decimal? cap = null) =>
        new(PayerType.Insurance, Guid.CreateVersion7(), null, Guid.CreateVersion7(), Guid.CreateVersion7(), coPay, cap);

    /// <summary>
    /// A finalized final bill of ₹50,000: the ₹3,000 injection and ₹47,000 of the stay's other care, on the payer's terms
    /// (self-paying with none); with a package that absorbed the injection when <paramref name="coveredByPackage"/>.
    /// </summary>
    private static Bill FinalBill(BillPayerTerms? terms, bool coveredByPackage = false)
    {
        var bill = Bill.Draft(FacilityId, BillType.InPatient, true, PatientId, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            terms ?? BillPayerTerms.SelfPay(PayerType.Self, null));
        var injection = Charge(3_000m, category: ServiceCategory.Consumable, serviceItemId: Medicines);
        injection.Description = Injection;
        bill.AddCharge(injection, true, null).IsSuccess.ShouldBeTrue();
        bill.AddCharge(Charge(47_000m, category: ServiceCategory.Procedure), true, null).IsSuccess.ShouldBeTrue();
        if (coveredByPackage)
        {
            bill.ApplyPackageCoverage(new Dictionary<Guid, decimal> { [bill.Lines.Single(l => l.ChargeId == injection.Id).Id] = 3_000m });
        }

        bill.Recalculate();
        bill.Finalize("NHIB260000001", null, Now).IsSuccess.ShouldBeTrue();
        return bill;
    }

    private static BillLine InjectionLine(Bill bill) => bill.Lines.Single(l => l.Description == Injection);

    /// <summary>The injection sent back unopened, as the IP pharmacy posts it.</summary>
    private static Charge Return()
    {
        var returned = Charge(3_000m, quantity: -1m, category: ServiceCategory.Consumable, serviceItemId: Medicines, serviceDate: Now.AddDays(1));
        returned.Description = "Returned: " + Injection;
        return returned;
    }

    private static LateReturn Late(Charge charge, decimal packageCovered, bool payerPayable) => new(
        charge.Id, charge.ServiceItemId, charge.Description, charge.UnitPrice,
        new BillLineInput(charge.GrossAmount, packageCovered, charge.DiscountPercent, 0m, charge.IsDiscountAllowed, charge.TaxRate, payerPayable));

    private static ReturnCredit? Split(Bill bill, Charge returned, decimal packageCovered = 0m)
    {
        var split = ReturnCredits.Split(bill, [Late(returned, packageCovered, true)], new Dictionary<Guid, decimal>());
        split.IsSuccess.ShouldBeTrue(split.Error?.Message);
        return split.Value;
    }
}
