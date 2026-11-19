using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A bill something was recorded against is not cancelled, over the real API and PostgreSQL. A cancellation reverses the
/// whole bill in the Tally day book and leaves it out of GSTR-1, while a credit note, a write-off, a debit note or an
/// insurer's settlement or deduction recorded against it stays: the bill would be undone twice. A credit note takes back
/// what remains instead. What a rejected claim left unpaid is recovered from the patient on the same bill, by a payer
/// credit note and a patient debit note, and the debit note reaches the Tally day book. A bill with nothing recorded
/// against it is cancelled as before.
/// </summary>
public sealed class BillCancellationTests(HospitalServer server)
{
    private const string Bills = "/api/v1/billing/bills";
    private const string Claims = "/api/v1/insurance/claims";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_bill_with_a_credit_note_or_a_write_off_is_not_cancelled_and_one_with_nothing_recorded_against_it_still_is()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var billing = await server.SignInAsync(Users.Billing);
        var accountant = await server.SignInAsync(Users.Accountant);
        var service = await ServiceAsync(admin, 10_000m);

        // An OP bill of ₹10,000, paid; ₹2,000 of it credited and refunded.
        var credited = await BillAsync(null, 0m, service);
        await PayAsync(billing, credited, 10_000m);
        await MakerChecker.ApproveAsync(accountant, await AskAsync(billing, $"{Bills}/{credited.Id}/credit-notes",
            new IssueCreditNoteRequest(CreditNoteKind.CreditNote, 2_000m, "Review charged twice", [new(credited.Lines.Single().Id, 2_000m)])));
        await RefundAsync(billing, accountant, credited, 2_000m);
        credited = await BillOfAsync(billing, credited);
        (credited.Status, credited.CreditAmount, credited.RefundedAmount, credited.RefundableAmount).ShouldBe((BillStatus.Paid, 2_000m, 2_000m, 0m));

        // Cancelled, the whole ₹10,000 would leave the books and GSTR-1 again while the credit note stays in both.
        var refused = await RefusedAsync(admin, credited);
        refused.Code.ShouldBe("billing.bill_adjusted");
        refused.Detail.ShouldContain("credit notes or write-offs of ₹2,000.00");
        refused.Detail.ShouldContain("Issue a credit note for what remains instead.");
        (await BillOfAsync(billing, credited)).ShouldSatisfyAllConditions(
            b => b.Status.ShouldBe(BillStatus.Paid),
            b => b.RefundableAmount.ShouldBe(0m),
            b => b.CancelledAt.ShouldBeNull());

        // A bill partly paid and the rest written off is not cancelled either.
        var writtenOff = await BillAsync(null, 0m, service);
        await PayAsync(billing, writtenOff, 6_000m);
        await MakerChecker.ApproveAsync(accountant, await AskAsync(billing, $"{Bills}/{writtenOff.Id}/credit-notes",
            new IssueCreditNoteRequest(CreditNoteKind.WriteOff, 4_000m, "The patient cannot pay the rest")));
        (await RefusedAsync(admin, await BillOfAsync(billing, writtenOff))).Code.ShouldBe("billing.bill_adjusted");

        // A paid bill with nothing recorded against it is cancelled as before: the payment becomes refundable.
        var plain = await BillAsync(null, 0m, service);
        await PayAsync(billing, plain, 10_000m);
        var cancelled = await admin.PostAsync<BillDetail>($"{Bills}/{plain.Id}/cancel",
            new CancelBillRequest("Billed to the wrong patient", (await BillOfAsync(billing, plain)).Version));
        (cancelled.Status, cancelled.RefundableAmount).ShouldBe((BillStatus.Cancelled, 10_000m));
        cancelled.Refunds.ShouldHaveSingleItem().ShouldSatisfyAllConditions(r => r.Amount.ShouldBe(10_000m), r => r.Status.ShouldBe(RefundStatus.Requested));
    }

    [Fact]
    public async Task A_rejected_claims_share_is_recovered_from_the_patient_on_the_same_bill_not_by_cancelling_it()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var billing = await server.SignInAsync(Users.Billing);
        var desk = await server.SignInAsync(Users.InsuranceDesk);
        var accountant = await server.SignInAsync(Users.Accountant);
        var company = await server.CreatePayerAsync(admin, new SavePayerRequest(
            "CO" + Letters(8).ToUpperInvariant(), $"Kaveri Textiles {Letters(5).ToUpperInvariant()}", PayerCategory.Corporate, "Meena Iyer", "9876543210",
            null, null, null, null, CreditDays: 30, DefaultCoPayPercent: 0m, PortalUrl: null, IsActive: true, Notes: null, Version: null));

        // ₹60,000 of care: the company's ₹48,000 and the patient's 20% co-pay of ₹12,000, which the patient pays.
        var bill = await BillAsync(company, 20m, await ServiceAsync(admin, 60_000m));
        (bill.PayerAmount, bill.PatientAmount).ShouldBe((48_000m, 12_000m));
        await PayAsync(billing, bill, 12_000m);

        // The company rejects the claim; once the accountant approves, its ₹48,000 is a deduction on the bill and a journal.
        var claimId = (await desk.PostAsync<JsonElement>(Claims, new CreateClaimRequest(bill.Id, null, null, null, null, null))).GetProperty("id").GetGuid();
        var claim = await desk.GetAsync<ClaimDetail>($"{Claims}/{claimId}");
        await MakerChecker.ApproveAsync(accountant, await AskAsync(desk, $"{Claims}/{claimId}/reject",
            new RejectClaimRequest("Not covered under the company's scheme", claim.Version)));
        bill = await BillOfAsync(billing, bill);
        (bill.PayerDisallowedAmount, bill.Status).ShouldBe((48_000m, BillStatus.Paid));

        // Cancelling it to bill the patient again would reverse the whole ₹60,000 while the deduction's journal stays.
        var refused = await RefusedAsync(admin, bill);
        refused.Code.ShouldBe("billing.bill_adjusted");
        refused.Detail.ShouldContain("the payer's deductions of ₹48,000.00");
        refused.Detail.ShouldContain("take it off the payer's invoice with a payer credit note and charge it to the patient with a patient debit note");

        // The share comes off the company's invoice, and is charged to the patient by a debit note on the same bill.
        var line = bill.Lines.Single();
        await MakerChecker.ApproveAsync(accountant, await AskAsync(billing, $"{Bills}/{bill.Id}/credit-notes",
            new IssueCreditNoteRequest(CreditNoteKind.PayerShare, 48_000m, "Claim rejected: recovered from the patient", [new(line.Id, 48_000m)])));
        await MakerChecker.ApproveAsync(accountant, await AskAsync(billing, $"{Bills}/{bill.Id}/debit-notes",
            new IssueDebitNoteRequest(DebitNoteParty.Patient, "Claim rejected: the company's share charged to the patient", [new(line.Id, 48_000m)])));
        bill = await BillOfAsync(billing, bill);
        (bill.Status, bill.DebitAmount, bill.DueAmount, bill.CreditAmount).ShouldBe((BillStatus.PartiallyPaid, 48_000m, 48_000m, 0m));
        var debitNote = bill.DebitNotes.ShouldNotBeNull().ShouldHaveSingleItem();
        var payerNote = bill.CreditNotes.ShouldHaveSingleItem();

        // In the books: the deduction comes back out of insurance disallowances, and the patient owes the share.
        var vouchers = await accountant.GetAsync<List<AccountingVoucher>>($"/api/v1/billing/tally/day-book?date={EInvoicing.Today():yyyy-MM-dd}");
        vouchers.ShouldContain(v => v.Narration.Contains(claim.ClaimNumber) && v.Entries.Any(e => e.LedgerName == "Insurance Disallowances" && e.IsDebit && e.Amount == 48_000m));
        vouchers.Single(v => v.Number == payerNote.Number).Entries
            .ShouldContain(new AccountingEntry("Insurance Disallowances", 48_000m, IsDebit: false));
        var debited = vouchers.Single(v => v.Number == debitNote.Number);
        debited.VoucherType.ShouldBe("Debit Note");
        debited.Entries.ShouldContain(new AccountingEntry("Patient Receivables", 48_000m, IsDebit: true));
        debited.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount).ShouldBe(48_000m);
        debited.Narration.ShouldContain($"Debit note {debitNote.Number} on bill {bill.BillNumber}");

        // The patient pays it; the bill is never cancelled.
        await PayAsync(billing, bill, 48_000m);
        (await BillOfAsync(billing, bill)).ShouldSatisfyAllConditions(b => b.Status.ShouldBe(BillStatus.Paid), b => b.DueAmount.ShouldBe(0m));
        (await RefusedAsync(admin, await BillOfAsync(billing, bill))).Code.ShouldBe("billing.bill_adjusted");
    }

    private async Task<ServiceItemDto> ServiceAsync(ApiClient admin, decimal price) =>
        await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            $"SV-{Letters(8)}", "Day-care procedure", null, ServiceCategory.Procedure, null, price, 0m, "999312", true, false, null, true, null));

    /// <summary>
    /// A health check-up visit with one charge of each service, billed and finalized by the billing desk: paid by the
    /// patient, or by <paramref name="company"/> on the patient's membership with <paramref name="coPay"/> percent co-pay.
    /// </summary>
    private async Task<BillDetail> BillAsync(PayerDetail? company, decimal coPay, params ServiceItemDto[] services)
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Selvam"), MiddleName: null, LastName: "Cancel" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1975, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        if (company is not null)
        {
            await server.VerifiedPolicyAsync(patient.Id, company.Id, coPay);
        }

        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patient.Id, VisitType.HealthCheckup, null, null, null, company is null ? PayerType.Self : PayerType.Corporate, company?.Id,
            company is null ? ReferralSource.Self : ReferralSource.Corporate, null, null, false, null, null, TokenPriority.Normal, IssueToken: false,
            NotifyPatient: false));

        var billing = await server.SignInAsync(Users.Billing);
        foreach (var service in services)
        {
            await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(patient.Id, visit.Id, null, service.Id, 1m, null, null, null, null, null));
        }

        var draft = await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(visit.Id, null, null));
        return await billing.PostAsync<BillDetail>($"{Bills}/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
    }

    private static Task<BillDetail> BillOfAsync(ApiClient client, BillDetail bill) => client.GetAsync<BillDetail>($"{Bills}/{bill.Id}");

    /// <summary>The patient pays <paramref name="amount"/> on the bill by UPI.</summary>
    private static Task<ReceiptDto> PayAsync(ApiClient billing, BillDetail bill, decimal amount) =>
        billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            bill.Id, [new PaymentLineInput(PaymentMode.Upi, amount, "UTR" + Digits(9), null, null, null, null, null)], null));

    /// <summary>Refunds <paramref name="amount"/> of the bill: the billing desk asks, the accountant approves, and it is paid by UPI.</summary>
    private static async Task RefundAsync(ApiClient billing, ApiClient accountant, BillDetail bill, decimal amount)
    {
        using var requested = await billing.SendRawAsync(HttpMethod.Post, "/api/v1/billing/refunds", new RequestRefundRequest(bill.Id, null, amount, "Credit note refunded"));
        requested.StatusCode.ShouldBe(HttpStatusCode.Created, await requested.Content.ReadAsStringAsync(Ct));
        var refundId = (await requested.Content.ReadFromJsonAsync<JsonObject>(ApiClient.Json, Ct))!["id"]!.GetValue<Guid>();
        await accountant.PostAsync($"/api/v1/billing/refunds/{refundId}/approve", new ApproveRefundRequest(null));
        await billing.PostAsync($"/api/v1/billing/refunds/{refundId}/pay", new PayRefundRequest(PaymentMode.Upi, "UTR" + Digits(9)));
    }

    /// <summary>Sends a change that waits for a second person (202): the request, as the server answers it.</summary>
    private static async Task<ApprovalRequestDto> AskAsync(ApiClient maker, string path, object body)
    {
        using var response = await maker.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
    }

    /// <summary>Asks to cancel the bill as it is, is refused (422), and returns the refusal's code and text.</summary>
    private static async Task<(string? Code, string Detail)> RefusedAsync(ApiClient canceller, BillDetail bill)
    {
        using var response = await canceller.SendRawAsync(HttpMethod.Post, $"{Bills}/{bill.Id}/cancel", new CancelBillRequest("Bill the patient again", bill.Version));
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        using var problem = JsonDocument.Parse(body);
        return (problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null, problem.RootElement.GetProperty("detail").GetString() ?? string.Empty);
    }

    private static string Letters(int count) => new(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => Random.Shared.Next(10)));
}
