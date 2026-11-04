using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.DoctorFees;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Money recorded as moved that did not move, over the real API and database (audit P6): a receipt whose money never
/// arrived (a payment the payment simulator took, a cheque returned unpaid) and a refund recorded as paid out that never
/// reached the patient. One person asks, another approves (maker-checker); then the bill owes again, or the advance holds
/// less, or the refund's money is back, and the day's Tally day book and day-end carry the reversal.
/// </summary>
public sealed class ReceiptReversalTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_payment_the_simulator_took_is_reversed_only_once_someone_else_approves_and_the_bill_owes_it_again()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var today = server.Today;
        GatewayOrderDto order;
        await using (await PaymentsToSimulatorAsync(admin))
        {
            order = await billing.PostAsync<GatewayOrderDto>($"/api/v1/billing/payment-links/{(await LinkAsync(billing)).Id}/refresh");
        }

        var bill = await BillAsync(billing, order.BillId!.Value);
        bill.Status.ShouldBe(BillStatus.Paid);
        var receipt = await billing.GetAsync<ReceiptDto>($"/api/v1/billing/receipts/{order.ReceiptId}");
        var line = receipt.Payments.ShouldHaveSingleItem();
        line.Mode.ShouldBe(PaymentMode.PaymentGateway);
        var dayEndBefore = await DayEndAsync(billing, today);

        // The billing desk asks: nothing changes until someone else approves.
        var asked = await AskAsync(billing, receipt, [line.Id], ReversalReason.TrainingSimulator, "Taken by the payment simulator; no money reached the bank.");
        asked.Status.ShouldBe(ApprovalStatus.Pending);
        asked.Kind.ShouldBe("billing.receipt_reversal");
        asked.Summary.ShouldBe($"₹{bill.PaidAmount:N2} of receipt {receipt.ReceiptNumber} not received");
        asked.Details.ShouldContain(new ApprovalDetail("What changes", $"Bill {bill.BillNumber} owes ₹{bill.PaidAmount:N2}"));
        asked.Details.ShouldContain(new ApprovalDetail("Why", "Recorded by a training simulator"));
        (await BillAsync(billing, bill.Id)).Status.ShouldBe(BillStatus.Paid);
        (await RefusedAsync(billing, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version), HttpStatusCode.Forbidden))
            .ShouldBe("approval.not_permitted");

        // The accountant approves: the bill owes the money again, and the receipt shows what was taken back.
        var approved = await MakerChecker.ApproveAsync(accountant, asked);
        var owing = await BillAsync(billing, bill.Id);
        (owing.Status, owing.PaidAmount, owing.DueAmount).ShouldBe((BillStatus.Finalized, 0m, bill.PaidAmount));
        var reversed = await billing.GetAsync<ReceiptDto>($"/api/v1/billing/receipts/{receipt.Id}");
        reversed.ReversedAmount.ShouldBe(receipt.Amount);
        reversed.Payments.Single().Reversed.ShouldBe(line.Amount);
        var reversal = reversed.Reversals.ShouldNotBeNull().ShouldHaveSingleItem();
        (reversal.Number, reversal.Reason, reversal.CausedById).ShouldBe((approved.Outcome, ReversalReason.TrainingSimulator, null));
        reversal.RequestedByName.ShouldBe((await billing.GetAsync<AuthUser>("/api/v1/auth/me")).DisplayName);
        reversal.ApprovedByName.ShouldBe((await accountant.GetAsync<AuthUser>("/api/v1/auth/me")).DisplayName);
        (await billing.GetAsync<PagedResult<ReceiptListItem>>($"/api/v1/billing/receipts?billId={bill.Id}")).Items.Single(r => r.Id == receipt.Id)
            .ReversedAmount.ShouldBe(receipt.Amount);

        // Taken back once only.
        (await RefusedAsync(billing, Reversal(receipt), new ReverseReceiptRequest([line.Id], ReversalReason.TrainingSimulator, "Asked a second time by mistake."),
            HttpStatusCode.UnprocessableEntity)).ShouldBe("billing.reversal_done");

        // The day's books carry it: the day-end has it as not received, and the day book turns the receipt round.
        var dayEnd = await DayEndAsync(billing, today);
        (NotReceived(dayEnd, PaymentMode.PaymentGateway) - NotReceived(dayEndBefore, PaymentMode.PaymentGateway)).ShouldBe(receipt.Amount);
        (dayEnd.NetAfterReversals - dayEndBefore.NetAfterReversals).ShouldBe(-receipt.Amount);
        var (vouchers, ledgers) = await DayBookAsync(owing.FacilityId, today);
        var journal = vouchers.Single(v => v.Number == approved.Outcome);
        journal.VoucherType.ShouldBe(TallyVoucherBuilder.JournalType);
        journal.Narration.ShouldBe($"Receipt {receipt.ReceiptNumber} reversed, {approved.Outcome}: money not received");
        journal.Entries.Select(e => (e.LedgerName, e.Amount, e.IsDebit)).Order().ToArray().ShouldBe(new[]
        {
            (ledgers.PatientDebtors, receipt.Amount, true),
            (ledgers.ModeLedger(PaymentMode.PaymentGateway), receipt.Amount, false),
        }.Order().ToArray());

        // The patient's spend no longer counts it, and the audit trail of the receipt and of the bill say who did what.
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        (await db.Set<PatientSpendEntry>().AsNoTracking().SingleAsync(e => e.Kind == SpendKind.PaymentReversed && e.SourceId == reversal.Id, Ct)).Amount
            .ShouldBe(receipt.Amount);
        var actions = await db.Set<AuditEvent>().AsNoTracking().Where(a => a.PatientId == bill.PatientId).Select(a => new { a.Action, a.EntityType }).ToListAsync(Ct);
        actions.ShouldContain(a => a.Action == "billing.receipt.reversed" && a.EntityType == nameof(Receipt));
        actions.ShouldContain(a => a.Action == "billing.bill.payment_reversed" && a.EntityType == nameof(Bill));
        actions.ShouldContain(a => a.Action == "approval.approved");
    }

    [Fact]
    public async Task Money_refunded_from_a_cheque_must_be_recorded_as_not_paid_out_before_the_cheque_is_reversed_and_cash_is_never_reversed()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var today = server.Today;
        await OpenShiftAsync(billing);

        // ₹600 paid by cheque; the bill is cancelled, which asks for the ₹600 back, and it is refunded by bank transfer.
        var bill = await MakerCheckerTests.FinalizedBillAsync(frontOffice, billing);
        var receipt = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(bill.Id, [Cheque(600m, today)], null));
        bill = await BillAsync(billing, bill.Id);
        bill = await hospitalAdmin.PostAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}/cancel", new CancelBillRequest("Billed to the wrong patient", bill.Version));
        var refund = await PayRefundAsync(billing, accountant, bill.Refunds.ShouldHaveSingleItem().Id);
        (await BillAsync(billing, bill.Id)).Status.ShouldBe(BillStatus.Refunded);

        // The cheque bounces. The bill holds nothing of it any more: the refund must be undone first, if it was not paid.
        var cheque = receipt.Payments.ShouldHaveSingleItem().Id;
        (await RefusedAsync(billing, Reversal(receipt), new ReverseReceiptRequest([cheque], ReversalReason.ChequeReturned, "Returned by the bank: funds insufficient."),
            HttpStatusCode.UnprocessableEntity)).ShouldBe("billing.reversal_exceeds_held");

        // The bank returned the transfer too: the account was closed. Recorded as not paid out, the money is back on the bill.
        var dayEndBefore = await DayEndAsync(billing, today);
        var notPaid = await billing.PostAsync<ApprovalRequestDto>($"/api/v1/billing/refunds/{refund.Id}/not-paid",
            new ReverseRefundRequest(ReversalReason.NotSettled, "The bank returned the transfer: the account is closed."));
        notPaid.Kind.ShouldBe("billing.refund_reversal");
        (await billing.GetAsync<RefundDto>($"/api/v1/billing/refunds/{refund.Id}")).NotPaidOut.ShouldBeNull();
        var approved = await MakerChecker.ApproveAsync(accountant, notPaid);
        var undone = await billing.GetAsync<RefundDto>($"/api/v1/billing/refunds/{refund.Id}");
        undone.Status.ShouldBe(RefundStatus.Paid, "the refund stays as it was recorded; a new refund pays the patient");
        (undone.NotPaidOut.ShouldNotBeNull().Number, undone.NotPaidOut.Amount).ShouldBe((approved.Outcome, 600m));
        var restored = await BillAsync(billing, bill.Id);
        (restored.Status, restored.RefundedAmount, restored.RefundableAmount).ShouldBe((BillStatus.Cancelled, 0m, 600m));
        (await RefusedAsync(billing, $"/api/v1/billing/refunds/{refund.Id}/not-paid", new ReverseRefundRequest(ReversalReason.NotSettled, "Asked a second time by mistake."),
            HttpStatusCode.UnprocessableEntity)).ShouldBe("billing.refund_reversal_done");
        var dayEnd = await DayEndAsync(billing, today);
        (NotPaidOut(dayEnd, PaymentMode.NetBanking) - NotPaidOut(dayEndBefore, PaymentMode.NetBanking)).ShouldBe(600m);
        var (vouchers, ledgers) = await DayBookAsync(restored.FacilityId, today);
        vouchers.Single(v => v.Number == approved.Outcome).Entries.Select(e => (e.LedgerName, e.Amount, e.IsDebit)).Order().ToArray().ShouldBe(new[]
        {
            (ledgers.ModeLedger(PaymentMode.NetBanking), 600m, true),
            (ledgers.PatientDebtors, 600m, false),
        }.Order().ToArray());

        // Now the cheque can be reversed: the cancelled bill has nothing left to refund.
        var reversal = await AskAsync(billing, receipt, [cheque], ReversalReason.ChequeReturned, "Returned by the bank: funds insufficient.");
        await MakerChecker.ApproveAsync(accountant, reversal);
        var settled = await BillAsync(billing, bill.Id);
        (settled.Status, settled.PaidAmount, settled.RefundableAmount, settled.DueAmount).ShouldBe((BillStatus.Cancelled, 0m, 0m, 0m));

        // Cash was counted when the shift closed: a shortfall is the shift's, never a reversal.
        var cashBill = await MakerCheckerTests.FinalizedBillAsync(frontOffice, billing);
        var cash = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(cashBill.Id, [new(PaymentMode.Cash, 600m, null, null, null, null, null, null)], null));
        (await RefusedAsync(billing, Reversal(cash), new ReverseReceiptRequest([cash.Payments.Single().Id], ReversalReason.Other, "The patient says it was not paid."),
            HttpStatusCode.UnprocessableEntity)).ShouldBe("billing.reversal_cash");
    }

    [Fact]
    public async Task An_advance_whose_money_did_not_arrive_takes_back_the_bill_it_paid_and_closes_the_refund_it_could_no_longer_pay()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var today = server.Today;

        // ₹800 prepaid online for an appointment, as the gateway's confirmation records it; ₹100 of it pays the registration
        // bill, and a refund of the other ₹700 waits for approval.
        var (patientId, bill) = await RegistrationBillAsync(frontOffice, billing);
        var advance = await PrepaidAdvanceAsync(bill.FacilityId, patientId, 800m);
        await billing.PostAsync<ReceiptDto>($"/api/v1/billing/bills/{bill.Id}/adjust-advance", new AdjustAdvanceRequest(advance.Id, null, bill.Version));
        bill = await BillAsync(billing, bill.Id);
        bill.Status.ShouldBe(BillStatus.Paid);
        var refundId = await billing.PostAsync<CreatedId>("/api/v1/billing/refunds", new RequestRefundRequest(null, null, 700m, "Appointment cancelled by the patient", advance.Id));

        // The prepayment never reached the bank. Undoing it takes the ₹100 back from the bill and closes the refund.
        var receipt = await billing.GetAsync<ReceiptDto>($"/api/v1/billing/receipts/{advance.ReceiptId}");
        var asked = await AskAsync(billing, receipt, [receipt.Payments.Single().Id], ReversalReason.NotSettled, "The gateway reports the payment was never captured.");
        var refundNumber = (await billing.GetAsync<RefundDto>($"/api/v1/billing/refunds/{refundId.Id}")).RefundNumber;
        asked.Details.ShouldContain(new ApprovalDetail("What changes", $"Advance {advance.ReceiptNumber} holds ₹0.00"));
        asked.Details.ShouldContain(new ApprovalDetail("Paid from it, owes again", $"Bill {bill.BillNumber}: ₹100.00, then owes ₹100.00"));
        asked.Details.ShouldContain(new ApprovalDetail("Refunds closed unpaid", $"{refundNumber} (₹700.00)"));
        var approved = await MakerChecker.ApproveAsync(accountant, asked);

        var owing = await BillAsync(billing, bill.Id);
        (owing.Status, owing.DueAmount).ShouldBe((BillStatus.Finalized, 100m));
        var held = (await billing.GetAsync<List<PatientAdvanceDto>>($"/api/v1/billing/patients/{patientId}/advances")).Single(a => a.Id == advance.Id);
        (held.Amount, held.AdjustedAmount, held.Balance, held.RefundPending).ShouldBe((0m, 0m, 0m, 0m));
        var closed = await billing.GetAsync<RefundDto>($"/api/v1/billing/refunds/{refundId.Id}");
        closed.Status.ShouldBe(RefundStatus.Rejected);
        closed.DecisionRemarks.ShouldBe($"Closed by {approved.Outcome}: the money it would return was not received.");

        // The adjustment's own receipt is reversed too, by the reversal of the money, and each has its journal.
        var main = (await billing.GetAsync<ReceiptDto>($"/api/v1/billing/receipts/{receipt.Id}")).Reversals.ShouldNotBeNull().ShouldHaveSingleItem();
        var adjustment = (await billing.GetAsync<PagedResult<ReceiptListItem>>($"/api/v1/billing/receipts?billId={bill.Id}")).Items.ShouldHaveSingleItem();
        adjustment.ReversedAmount.ShouldBe(100m);
        var undo = (await billing.GetAsync<ReceiptDto>($"/api/v1/billing/receipts/{adjustment.Id}")).Reversals.ShouldNotBeNull().ShouldHaveSingleItem();
        undo.CausedById.ShouldBe(main.Id);
        var (vouchers, ledgers) = await DayBookAsync(bill.FacilityId, today);
        vouchers.Single(v => v.Number == main.Number).Entries.Select(e => (e.LedgerName, e.Amount, e.IsDebit)).Order().ToArray().ShouldBe(new[]
        {
            (ledgers.PatientAdvances, 800m, true),
            (ledgers.ModeLedger(PaymentMode.PaymentGateway), 800m, false),
        }.Order().ToArray());
        vouchers.Single(v => v.Number == undo.Number).Entries.Select(e => (e.LedgerName, e.Amount, e.IsDebit)).Order().ToArray().ShouldBe(new[]
        {
            (ledgers.PatientDebtors, 100m, true),
            (ledgers.ModeLedger(PaymentMode.Advance), 100m, false),
        }.Order().ToArray());
    }

    [Fact]
    public async Task A_reversal_approved_after_the_advance_paid_a_bill_is_not_made_until_asked_again()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);

        // Asked while the ₹800 advance held all its money; then ₹100 of it paid the registration bill.
        var (patientId, bill) = await RegistrationBillAsync(frontOffice, billing);
        var advance = await PrepaidAdvanceAsync(bill.FacilityId, patientId, 800m);
        var receipt = await billing.GetAsync<ReceiptDto>($"/api/v1/billing/receipts/{advance.ReceiptId}");
        var asked = await AskAsync(billing, receipt, [receipt.Payments.Single().Id], ReversalReason.NotSettled, "The gateway reports the payment was never captured.");
        asked.Details.ShouldNotContain(d => d.Label == "Paid from it, owes again");
        await billing.PostAsync<ReceiptDto>($"/api/v1/billing/bills/{bill.Id}/adjust-advance", new AdjustAdvanceRequest(advance.Id, null, bill.Version));

        // The checker approved a reversal that touched no bill: it is not made, and nothing changes.
        (await RefusedAsync(accountant, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version), HttpStatusCode.UnprocessableEntity))
            .ShouldBe("approval.stale");
        (await BillAsync(billing, bill.Id)).Status.ShouldBe(BillStatus.Paid);
        (await billing.GetAsync<ReceiptDto>($"/api/v1/billing/receipts/{receipt.Id}")).ReversedAmount.ShouldBe(0m);

        // Asked again, the checker sees the bill it takes back, and it is made.
        var again = await AskAsync(billing, receipt, [receipt.Payments.Single().Id], ReversalReason.NotSettled, "The gateway reports the payment was never captured.");
        again.Details.ShouldContain(new ApprovalDetail("Paid from it, owes again", $"Bill {bill.BillNumber}: ₹100.00, then owes ₹100.00"));
        await MakerChecker.ApproveAsync(accountant, again);
        (await BillAsync(billing, bill.Id)).DueAmount.ShouldBe(100m);
    }

    [Fact]
    public async Task A_reversal_approved_after_the_bill_changed_is_not_made_until_asked_again()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        await OpenShiftAsync(billing);

        // ₹400 of a ₹600 bill paid by UPI, and asked to be reversed: the checker is shown the bill owing ₹600 again.
        var bill = await MakerCheckerTests.FinalizedBillAsync(frontOffice, billing);
        var upi = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts",
            new CollectPaymentRequest(bill.Id, [new(PaymentMode.Upi, 400m, "UTR" + Digits(9), null, null, null, null, null)], null));
        var asked = await AskAsync(billing, upi, [upi.Payments.Single().Id], ReversalReason.NotSettled, "The bank reports the UPI payment was never credited.");
        asked.Details.ShouldContain(new ApprovalDetail("What changes", $"Bill {bill.BillNumber} owes ₹600.00"));

        // Then ₹100 is paid at the counter. The lines are the same, but the bill would owe ₹500, not what the checker saw:
        // it is not made, and nothing changes.
        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts",
            new CollectPaymentRequest(bill.Id, [new(PaymentMode.Cash, 100m, null, null, null, null, null, null)], null));
        (await RefusedAsync(accountant, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version), HttpStatusCode.UnprocessableEntity))
            .ShouldBe("approval.stale");
        (await BillAsync(billing, bill.Id)).DueAmount.ShouldBe(100m);
        (await billing.GetAsync<ReceiptDto>($"/api/v1/billing/receipts/{upi.Id}")).ReversedAmount.ShouldBe(0m);

        // Asked again, the checker sees what the bill will owe now, and it is made.
        var again = await AskAsync(billing, upi, [upi.Payments.Single().Id], ReversalReason.NotSettled, "The bank reports the UPI payment was never credited.");
        again.Details.ShouldContain(new ApprovalDetail("What changes", $"Bill {bill.BillNumber} owes ₹500.00"));
        await MakerChecker.ApproveAsync(accountant, again);
        (await BillAsync(billing, bill.Id)).DueAmount.ShouldBe(500m);
    }

    [Fact]
    public async Task Doctors_fees_accrued_on_payment_are_reversed_with_the_money_and_accrue_again_when_the_bill_is_paid()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var today = server.Today;
        await OpenShiftAsync(billing);

        await using (await FeesOnPaymentAsync(admin))
        {
            // A doctor earning 40 % of procedures, on a ₹2,000 bill: nothing accrues until it is paid.
            var doctor = await DoctorFeeStatementTests.DoctorAsync(hospitalAdmin);
            var debridement = await DoctorFeeStatementTests.ServiceAsync(server, hospitalAdmin, "Wound debridement", 2000m);
            await DoctorFeeStatementTests.RuleAsync(server, accountant, doctor.Id, null, ServiceCategory.Procedure, ShareMethod.Percentage, 40m, today);
            var patient = await RegisterAsync(frontOffice);
            var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, patient, null));
            draft = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/lines",
                new AddBillLineRequest(debridement.Id, 1m, null, null, doctor.Id, null, draft.Version));
            var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
            (await FeesAsync(bill.Id)).ShouldBeEmpty();

            // Paid by cheque: the fee accrues. The cheque bounces: the fee is reversed with it.
            var cheque = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(bill.Id, [Cheque(2000m, today)], null));
            (await FeesAsync(bill.Id)).ShouldBe([(800m, false, 1)]);
            await MakerChecker.ApproveAsync(accountant,
                await AskAsync(billing, cheque, [cheque.Payments.Single().Id], ReversalReason.ChequeReturned, "Returned by the bank: funds insufficient."));
            (await FeesAsync(bill.Id)).ShouldBe([(-800m, true, 1), (800m, false, 1)]);

            // Paid again by UPI: the fee accrues again, once, as the line's second accrual.
            await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts",
                new CollectPaymentRequest(bill.Id, [new(PaymentMode.Upi, 2000m, "UTR" + Digits(9), null, null, null, null, null)], null));
            (await FeesAsync(bill.Id)).ShouldBe([(-800m, true, 1), (800m, false, 1), (800m, false, 2)]);
            (await BillAsync(billing, bill.Id)).Status.ShouldBe(BillStatus.Paid);
        }
    }

    /// <summary>The doctors' fee entries of a bill: (share, is a reversal, the line's occurrence), in order.</summary>
    private async Task<(decimal Share, bool Reversal, int Occurrence)[]> FeesAsync(Guid billId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return (await db.Set<DoctorFeeAccrual>().IgnoreQueryFilters().AsNoTracking().Where(a => a.BillId == billId).ToListAsync(Ct))
            .Select(a => (a.ShareAmount, a.ReversalOfId != null, a.Occurrence))
            .Order()
            .ToArray();
    }

    /// <summary>Doctors' fees accrue when a bill is paid, until disposed; then the setting is put back.</summary>
    private static async Task<IAsyncDisposable> FeesOnPaymentAsync(ApiClient admin)
    {
        var key = DoctorFeeService.TriggerSetting;
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}"))
            .FirstOrDefault(s => s.Key == key && s.FacilityId is null)?.Value;
        await admin.SaveSettingAsync(key, new SaveSettingRequest(JsonValue.Create("paid"), null, false, null));
        return new Restore(() => admin.SaveSettingAsync(key, new SaveSettingRequest(before?.DeepClone() ?? JsonValue.Create("finalized"), null, false, null)));
    }

    private static string Reversal(ReceiptDto receipt) => $"/api/v1/billing/receipts/{receipt.Id}/reversal";

    private static async Task<ApprovalRequestDto> AskAsync(ApiClient maker, ReceiptDto receipt, IReadOnlyList<Guid> lines, ReversalReason reason, string note)
    {
        using var response = await maker.SendRawAsync(HttpMethod.Post, Reversal(receipt), new ReverseReceiptRequest(lines, reason, note));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
    }

    private static Task<BillDetail> BillAsync(ApiClient client, Guid billId) => client.GetAsync<BillDetail>($"/api/v1/billing/bills/{billId}");

    private static Task<DayEndReport> DayEndAsync(ApiClient client, DateOnly day) => client.GetAsync<DayEndReport>($"/api/v1/billing/reports/day-end?date={day:yyyy-MM-dd}");

    private static decimal NotReceived(DayEndReport report, PaymentMode mode) => (report.NotReceived ?? []).Where(t => t.Mode == mode).Sum(t => t.Amount);

    private static decimal NotPaidOut(DayEndReport report, PaymentMode mode) => (report.NotPaidOut ?? []).Where(t => t.Mode == mode).Sum(t => t.Amount);

    /// <summary>The day's Tally vouchers as the day book builds them, and the ledgers they post to.</summary>
    private async Task<(IReadOnlyList<AccountingVoucher> Vouchers, TallyLedgerMap Ledgers)> DayBookAsync(Guid facilityId, DateOnly day)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var tally = scope.ServiceProvider.GetRequiredService<TallyService>();
        return (await tally.BuildVouchersAsync(facilityId, day, Ct), await tally.LedgersAsync(facilityId, Ct));
    }

    /// <summary>A refund someone else asked for, approved by the accountant and paid out by bank transfer.</summary>
    private static async Task<RefundDto> PayRefundAsync(ApiClient billing, ApiClient accountant, Guid refundId)
    {
        using (var approve = await accountant.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/refunds/{refundId}/approve", new ApproveRefundRequest(null)))
        {
            approve.StatusCode.ShouldBe(HttpStatusCode.NoContent, await approve.Content.ReadAsStringAsync(Ct));
        }

        using (var pay = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/refunds/{refundId}/pay", new PayRefundRequest(PaymentMode.NetBanking, "NEFT" + Digits(10))))
        {
            pay.StatusCode.ShouldBe(HttpStatusCode.NoContent, await pay.Content.ReadAsStringAsync(Ct));
        }

        return await billing.GetAsync<RefundDto>($"/api/v1/billing/refunds/{refundId}");
    }

    /// <summary>A patient advance of an online appointment prepayment, recorded as the gateway's confirmation records one.</summary>
    private async Task<PatientAdvance> PrepaidAdvanceAsync(Guid facilityId, Guid patientId, decimal amount)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var advances = scope.ServiceProvider.GetRequiredService<AdvanceService>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        var advance = await advances.RecordOnlinePaymentAsync(
            facilityId, patientId, amount, "pay_test" + Digits(12), "order_test" + Digits(12), "razorpay", "Online prepayment for an appointment (test)", DateTimeOffset.UtcNow,
            null, null, Ct);
        await db.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
        return advance;
    }

    /// <summary>A new patient's registration bill (₹100, REG-NEW), finalized.</summary>
    private async Task<(Guid PatientId, BillDetail Bill)> RegistrationBillAsync(ApiClient frontOffice, ApiClient billing)
    {
        var patient = await RegisterAsync(frontOffice);
        var service = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=REG-NEW")).Single(s => s.Code == "REG-NEW");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(patient, null, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, patient, null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        bill.DueAmount.ShouldBe(100m);
        return (patient, bill);
    }

    /// <summary>A payment link for a new patient's registration bill (the simulator takes it at once; refreshing the link records it).</summary>
    private async Task<GatewayOrderDto> LinkAsync(ApiClient billing)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (_, bill) = await RegistrationBillAsync(frontOffice, billing);
        return await billing.PostAsync<GatewayOrderDto>("/api/v1/billing/payment-links", new CreatePaymentLinkRequest(bill.Id, null, null, false, false));
    }

    private static async Task<Guid> RegisterAsync(ApiClient frontOffice) =>
        (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Ravi"), MiddleName: null, LastName: "Reversal" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(365 * 30)), AgeYears: null, AgeMonths: null,
            Phone: "7" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false))).Id;

    /// <summary>
    /// Online payments go to the training gateway until disposed; then the setting is put back, because the server is
    /// shared by every test of the run.
    /// </summary>
    private static async Task<IAsyncDisposable> PaymentsToSimulatorAsync(ApiClient admin)
    {
        const string key = "integrations.payment";
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}"))
            .FirstOrDefault(s => s.Key == key && s.FacilityId is null)?.Value;
        await admin.SaveSettingAsync(key, new SaveSettingRequest(new JsonObject { ["enabled"] = true, ["provider"] = "simulator" }, null, false, null));
        return new Restore(() => admin.SaveSettingAsync(key, new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null)));
    }

    /// <summary>The billing desk's counter shift, opened if it is not.</summary>
    private static async Task OpenShiftAsync(ApiClient billing)
    {
        using var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        current.EnsureSuccessStatusCode();
        if (current.StatusCode == HttpStatusCode.NoContent)
        {
            await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP billing", 0m));
        }
    }

    private static async Task<string?> RefusedAsync(ApiClient client, string path, object body, HttpStatusCode status)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    private static PaymentLineInput Cheque(decimal amount, DateOnly date) => new(PaymentMode.Cheque, amount, null, null, null, Digits(6), "State Bank of India", date);

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => Random.Shared.Next(10)));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record CreatedId(Guid Id);

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
