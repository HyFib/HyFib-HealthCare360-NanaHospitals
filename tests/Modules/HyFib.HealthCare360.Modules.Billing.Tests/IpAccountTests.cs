using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class IpAccountTests
{
    private static readonly Guid PrivateRoom = Guid.CreateVersion7();

    private static IpAccount Admit() => IpAccount.Open(
        new PatientAdmitted(
            Guid.CreateVersion7(), "NHIP260000123", TestData.PatientId, Guid.CreateVersion7(), TestData.FacilityId, Guid.CreateVersion7(), Guid.CreateVersion7(),
            PrivateRoom, Guid.CreateVersion7(), PayerType.Insurance, TestData.Now),
        payerId: Guid.CreateVersion7(),
        tariffPlanId: Guid.CreateVersion7(),
        policyId: null);

    private static AdmissionBillingPosition Position(
        int unbilledCount = 0, decimal unbilled = 0m, bool hasCharges = true, bool hasFinalBill = true, decimal due = 0m, decimal deposit = 0m, bool approved = false) =>
        new(unbilledCount, unbilled, hasCharges, hasFinalBill, due, deposit, approved);

    [Fact]
    public void An_admission_opens_an_account_with_its_payer_and_bed_class()
    {
        var account = Admit();

        account.Status.ShouldBe(IpAccountStatus.Open);
        account.AdmissionNumber.ShouldBe("NHIP260000123");
        account.PayerType.ShouldBe(PayerType.Insurance);
        account.BedServiceItemId.ShouldBe(PrivateRoom);
        account.DepositBalance.ShouldBe(0m);
    }

    [Fact]
    public void The_advance_balance_follows_deposits_adjustments_and_refunds()
    {
        var account = Admit();

        account.RecordDeposit(10_000m).IsSuccess.ShouldBeTrue();
        account.AdjustDeposit(6_000m).IsSuccess.ShouldBeTrue();
        account.RefundDeposit(4_000.01m).Error!.Code.ShouldBe("billing.deposit_insufficient");
        account.RefundDeposit(4_000m).IsSuccess.ShouldBeTrue();
        account.DepositBalance.ShouldBe(0m);

        account.ReverseAdjustment(1_000m);
        account.DepositBalance.ShouldBe(1_000m);
    }

    [Fact]
    public void Deposits_must_be_positive_and_a_closed_account_takes_none()
    {
        var account = Admit();

        account.RecordDeposit(0m).IsFailure.ShouldBeTrue();
        account.MarkDischarged(TestData.Now, "Normal");
        account.Close();
        account.RecordDeposit(100m).Error!.Code.ShouldBe("billing.ip_account_closed");
    }

    [Fact]
    public void The_account_follows_the_discharge_and_closes_only_after_it()
    {
        var account = Admit();
        account.Close();
        account.Status.ShouldBe(IpAccountStatus.Open);

        account.MarkDischargeInitiated(TestData.Now);
        account.Status.ShouldBe(IpAccountStatus.DischargeInitiated);
        account.MarkDischarged(TestData.Now.AddHours(3), "Normal");
        account.Status.ShouldBe(IpAccountStatus.Discharged);
        account.Close();
        account.Status.ShouldBe(IpAccountStatus.Closed);
    }

    /// <summary>A revoked discharge opens the account again: it is not settled while the patient is still in a bed.</summary>
    [Fact]
    public void A_revoked_discharge_opens_the_account_again()
    {
        var account = Admit();
        account.MarkDischargeInitiated(TestData.Now);

        account.ReopenAfterRevokedDischarge();
        (account.Status, account.DischargeInitiatedAt).ShouldBe((IpAccountStatus.Open, (DateTimeOffset?)null));

        // Initiated again later, and discharged: a late revocation changes nothing any more.
        account.MarkDischargeInitiated(TestData.Now.AddDays(1));
        account.DischargeInitiatedAt.ShouldBe(TestData.Now.AddDays(1));
        account.MarkDischarged(TestData.Now.AddDays(1).AddHours(2), "Normal");
        account.ReopenAfterRevokedDischarge();
        account.Status.ShouldBe(IpAccountStatus.Discharged);
    }

    [Fact]
    public void Packages_change_only_during_the_stay()
    {
        var account = Admit();

        account.AssignPackage(Guid.CreateVersion7(), TestData.Now).IsSuccess.ShouldBeTrue();
        account.PackageAssignedAt.ShouldBe(TestData.Now);
        account.MarkDischarged(TestData.Now, "Normal");
        account.AssignPackage(null, TestData.Now).Error!.Code.ShouldBe("billing.ip_account_inactive");
    }

    [Fact]
    public void A_transfer_keeps_the_known_bed_when_the_new_bed_class_is_unknown()
    {
        var account = Admit();

        account.MoveToBed(Guid.CreateVersion7(), null);

        account.BedServiceItemId.ShouldBe(PrivateRoom);
    }

    [Fact]
    public void Discharge_with_dues_is_recorded_against_the_approver()
    {
        var account = Admit();
        var approver = Guid.CreateVersion7();

        account.ApproveDischargeWithDues(approver, " Patient to pay after claim settlement ", TestData.Now).IsSuccess.ShouldBeTrue();

        account.DischargeWithDuesApproved.ShouldBeTrue();
        account.DischargeApprovedBy.ShouldBe(approver);
        account.DischargeApprovalReason.ShouldBe("Patient to pay after claim settlement");
    }

    [Fact]
    public void A_cancelled_admission_takes_no_further_advances_or_packages()
    {
        var account = Admit();

        account.CancelAdmission(" Admitted to the wrong UHID ", TestData.Now);

        account.Status.ShouldBe(IpAccountStatus.Cancelled);
        account.IsActive.ShouldBeFalse();
        account.CancelReason.ShouldBe("Admitted to the wrong UHID");
        account.RecordDeposit(1_000m).Error!.Code.ShouldBe("billing.ip_account_closed");
        account.AssignPackage(Guid.CreateVersion7(), TestData.Now).Error!.Code.ShouldBe("billing.ip_account_inactive");
    }

    [Fact]
    public void A_cancelled_admission_stays_cancelled_and_a_closed_account_is_not_reopened()
    {
        var cancelled = Admit();
        cancelled.CancelAdmission("Duplicate admission", TestData.Now);
        cancelled.MarkDischarged(TestData.Now.AddHours(1), "Normal");
        cancelled.Status.ShouldBe(IpAccountStatus.Cancelled);

        var closed = Admit();
        closed.MarkDischarged(TestData.Now, "Normal");
        closed.Close();
        closed.CancelAdmission("Too late", TestData.Now);
        closed.Status.ShouldBe(IpAccountStatus.Closed);
        closed.CancelledAt.ShouldBeNull();
    }

    [Fact]
    public void An_admission_without_services_is_cleared()
    {
        DischargeClearanceRules.Evaluate(Position(hasCharges: false, hasFinalBill: false)).Cleared.ShouldBeTrue();
    }

    [Fact]
    public void Unbilled_services_block_the_discharge()
    {
        var clearance = DischargeClearanceRules.Evaluate(Position(unbilledCount: 2, unbilled: 1_250m));

        clearance.Cleared.ShouldBeFalse();
        clearance.Outstanding.ShouldBe(1_250m);
        clearance.Reason!.ShouldContain("2 service(s)");
    }

    /// <summary>
    /// A self-paying stay billed in parts whose interim bills took every service needs no final bill, which would hold
    /// nothing: it is cleared once the patient owes nothing on those bills. Its clearance waited for a final bill that
    /// could never be finalized, so the patient could leave only with management's approval of a discharge with dues.
    /// </summary>
    [Fact]
    public void A_stay_billed_in_full_on_interim_bills_needs_no_final_bill()
    {
        var billed = Position(hasFinalBill: false);
        billed.BilledOnInterimBills.ShouldBeTrue();
        DischargeClearanceRules.Evaluate(billed).ShouldBe(new DischargeClearance(true, 0m, null));

        var owing = DischargeClearanceRules.Evaluate(Position(hasFinalBill: false, due: 1_200m, deposit: 5_000m));
        owing.Cleared.ShouldBeFalse();
        owing.Outstanding.ShouldBe(1_200m);
        owing.Reason!.ShouldContain("is due from the patient");

        // Not billed in parts: services not on a finalized bill, a final bill, or no services at all.
        Position(hasFinalBill: false, unbilledCount: 1, unbilled: 300m).BilledOnInterimBills.ShouldBeFalse();
        Position().BilledOnInterimBills.ShouldBeFalse();
        Position(hasCharges: false, hasFinalBill: false).BilledOnInterimBills.ShouldBeFalse();
    }

    [Fact]
    public void Patient_dues_block_the_discharge_and_mention_an_unused_advance()
    {
        var clearance = DischargeClearanceRules.Evaluate(Position(due: 3_000m, deposit: 5_000m));

        clearance.Cleared.ShouldBeFalse();
        clearance.Outstanding.ShouldBe(3_000m);
        clearance.Reason!.ShouldContain("advance");
    }

    [Fact]
    public void Management_approval_clears_a_discharge_with_dues_but_keeps_the_outstanding()
    {
        var clearance = DischargeClearanceRules.Evaluate(Position(unbilledCount: 1, unbilled: 500m, due: 2_000m, approved: true));

        clearance.Cleared.ShouldBeTrue();
        clearance.Outstanding.ShouldBe(2_500m);
    }

    [Fact]
    public void A_settled_admission_is_cleared()
    {
        var clearance = DischargeClearanceRules.Evaluate(Position());

        clearance.ShouldBe(new DischargeClearance(true, 0m, null));
    }
}
