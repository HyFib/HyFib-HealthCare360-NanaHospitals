using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Maker-checker over the real API: a change one person asks for is made only when another person, who may approve it,
/// approves it, and only on the record as it was when it was asked for. Credit notes and write-offs (audit finding S6)
/// and patient merges are made this way. Each step is audited, and the person who asked is told the outcome.
/// </summary>
public sealed class MakerCheckerTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_credit_note_is_issued_only_when_someone_else_approves_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var bill = await FinalizedBillAsync(frontOffice, billing);

        // The billing desk asks for ₹250 off: nothing changes on the bill yet.
        var asked = await AskForCreditAsync(billing, bill, 250m);
        asked.Status.ShouldBe(ApprovalStatus.Pending);
        asked.Kind.ShouldBe("billing.credit_note");
        asked.Summary.ShouldBe($"Credit note of ₹250.00 on bill {bill.BillNumber}");
        asked.Details.ShouldContain(new ApprovalDetail("Balance due", "₹600.00"));
        asked.CanDecide.ShouldBeFalse();
        asked.CanWithdraw.ShouldBeTrue();
        var unchanged = await BillAsync(billing, bill);
        unchanged.CreditAmount.ShouldBe(0m);
        unchanged.CreditNotes.ShouldBeEmpty();

        // The desk may ask but not approve; the administrator, who may do both, does not approve their own request.
        (await RefusedAsync(billing, Approve(asked), new ApproveRequest(null, asked.Version), HttpStatusCode.Forbidden)).ShouldBe("approval.not_permitted");
        var other = await FinalizedBillAsync(frontOffice, billing);
        var ownRequest = await AskForCreditAsync(hospitalAdmin, other, 100m);
        (await RefusedAsync(hospitalAdmin, Approve(ownRequest), new ApproveRequest(null, ownRequest.Version), HttpStatusCode.Forbidden)).ShouldBe("approval.self");

        // The accountant finds both in the inbox, oldest first, and approves the desk's.
        var inbox = await accountant.GetAsync<PagedResult<ApprovalRequestDto>>("/api/v1/approvals?pageSize=200");
        inbox.Items.Select(r => r.Id).ShouldContain(asked.Id);
        inbox.Items.Select(r => r.Id).ShouldContain(ownRequest.Id);
        inbox.Items.Single(r => r.Id == asked.Id).CanDecide.ShouldBeTrue();
        var approved = await MakerChecker.ApproveAsync(accountant, asked, "Rate corrected in the tariff");

        var credited = await BillAsync(billing, bill);
        credited.CreditAmount.ShouldBe(250m);
        credited.DueAmount.ShouldBe(350m);
        var note = credited.CreditNotes.ShouldHaveSingleItem();
        approved.Outcome.ShouldBe(note.Number);
        note.RequestedByName.ShouldBe((await billing.GetAsync<AuthUser>("/api/v1/auth/me")).DisplayName);
        note.IssuedByName.ShouldBe((await accountant.GetAsync<AuthUser>("/api/v1/auth/me")).DisplayName);

        // The desk is told, and every step is in the audit trail of the bill's patient.
        (await billing.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=200")).Items
            .ShouldContain(n => n.Title == "Credit note approved" && n.Link == $"/approvals/{asked.Id}");
        var actions = await AuditActionsAsync(bill.PatientId);
        actions.ShouldContain("approval.requested");
        actions.ShouldContain("approval.approved");
        actions.ShouldContain("billing.credit_note.issued");
    }

    [Fact]
    public async Task A_credit_note_approved_after_the_bill_changed_is_not_issued()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var cashier = await server.SignInAsync(HospitalServer.Users.Cashier);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var bill = await FinalizedBillAsync(frontOffice, billing);
        var asked = await AskForCreditAsync(cashier, bill, 250m);

        // The patient pays ₹400 by UPI while the request waits: the accountant would approve a credit against a ₹600 due.
        await OpenCounterAsync(cashier);
        await cashier.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(bill.Id, [Upi(400m)], null));
        (await RefusedAsync(accountant, Approve(asked), new ApproveRequest(null, asked.Version), HttpStatusCode.UnprocessableEntity)).ShouldBe("approval.stale");

        var failed = await cashier.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");
        failed.Status.ShouldBe(ApprovalStatus.Failed);
        failed.Outcome.ShouldNotBeNull().ShouldContain("changed after this was asked for");
        (await BillAsync(billing, bill)).CreditAmount.ShouldBe(0m);
        (await cashier.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=200")).Items
            .ShouldContain(n => n.Title == "Credit note could not be made" && n.Severity == "warning");

        // Asked again against the bill as it is now, it is issued.
        var again = await AskForCreditAsync(cashier, bill, 50m);
        await MakerChecker.ApproveAsync(accountant, again);
        (await BillAsync(billing, bill)).CreditAmount.ShouldBe(50m);
    }

    [Fact]
    public async Task A_request_is_asked_once_and_rejected_or_withdrawn_without_changing_the_bill()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var bill = await FinalizedBillAsync(frontOffice, billing);

        (await RefusedAsync(billing, CreditNotes(bill), new IssueCreditNoteRequest(CreditNoteKind.CreditNote, 700m, "Rate entered twice"), HttpStatusCode.UnprocessableEntity))
            .ShouldBe("billing.credit_exceeds");
        var asked = await AskForCreditAsync(billing, bill, 250m);
        (await RefusedAsync(billing, CreditNotes(bill), new IssueCreditNoteRequest(CreditNoteKind.CreditNote, 100m, "Rate entered twice"), HttpStatusCode.Conflict))
            .ShouldBe("approval.already_requested");

        var rejected = await accountant.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/reject", new RejectApprovalRequest("The rate charged was right", asked.Version));
        rejected.Status.ShouldBe(ApprovalStatus.Rejected);
        rejected.DecisionNote.ShouldBe("The rate charged was right");
        (await RefusedAsync(accountant, Approve(asked), new ApproveRequest(null, rejected.Version), HttpStatusCode.UnprocessableEntity)).ShouldBe("approval.not_pending");

        var second = await AskForCreditAsync(billing, bill, 100m);
        (await RefusedAsync(accountant, $"/api/v1/approvals/{second.Id}/withdraw", new WithdrawApprovalRequest(second.Version), HttpStatusCode.Forbidden))
            .ShouldBe("approval.not_maker");
        var withdrawn = await billing.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{second.Id}/withdraw", new WithdrawApprovalRequest(second.Version));
        withdrawn.Status.ShouldBe(ApprovalStatus.Withdrawn);
        (await RefusedAsync(accountant, Approve(second), new ApproveRequest(null, withdrawn.Version), HttpStatusCode.UnprocessableEntity)).ShouldBe("approval.not_pending");

        var untouched = await BillAsync(billing, bill);
        untouched.CreditAmount.ShouldBe(0m);
        untouched.CreditNotes.ShouldBeEmpty();
        var mine = await billing.GetAsync<PagedResult<ApprovalRequestDto>>($"/api/v1/approvals?view=Subject&subjectType=Bill&subjectId={bill.Id}");
        mine.Items.Select(r => r.Status).ShouldBe([ApprovalStatus.Withdrawn, ApprovalStatus.Rejected]);
    }

    [Fact]
    public async Task A_request_whose_stored_change_was_altered_is_not_approved()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var bill = await FinalizedBillAsync(frontOffice, billing);
        var asked = await AskForCreditAsync(billing, bill, 250m);

        // Someone with access to the database raises the amount after the desk asked.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Database.ExecuteSqlAsync(
                $"UPDATE platform.approval_requests SET payload = jsonb_set(payload::jsonb, '{{amount}}', '500')::text WHERE id = {asked.Id}", Ct)).ShouldBe(1);
        }

        (await RefusedAsync(accountant, Approve(asked), new ApproveRequest(null, asked.Version), HttpStatusCode.UnprocessableEntity)).ShouldBe("approval.altered");
        (await BillAsync(billing, bill)).CreditAmount.ShouldBe(0m);
        await using var check = server.Services.CreateAsyncScope();
        (await check.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
                .Where(a => a.EntityId == asked.Id.ToString() && a.Action == "approval.altered").ToListAsync(Ct))
            .ShouldHaveSingleItem().Severity.ShouldBe(AuditSeverity.Critical);
    }

    [Fact]
    public async Task A_merge_is_made_only_when_another_records_officer_approves_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var verifier = await server.SignInAsync(HospitalServer.Users.MrdVerifier);
        var kept = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var duplicate = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);

        var asked = await mrd.PostAsync<ApprovalRequestDto>($"/api/v1/patients/{duplicate.Id}/merge", new MergePatientRequest(kept.Id, "Registered twice at the counter"));
        asked.Summary.ShouldBe($"Merge {duplicate.Uhid} into {kept.Uhid}");
        asked.Details.Select(d => d.Label).ShouldBe(["Duplicate, merged away", "Record kept"]);
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{duplicate.Id}")).MergedIntoPatientId.ShouldBeNull();
        (await RefusedAsync(mrd, Approve(asked), new ApproveRequest(null, asked.Version), HttpStatusCode.Forbidden)).ShouldBe("approval.self");

        var approved = await MakerChecker.ApproveAsync(verifier, asked);

        approved.Outcome.ShouldBe($"{duplicate.Uhid} merged into {kept.Uhid}");
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{duplicate.Id}")).MergedIntoPatientId.ShouldBe(kept.Id);
        await using var scope = server.Services.CreateAsyncScope();
        var merged = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .SingleAsync(a => a.PatientId == kept.Id && a.Action == "patient.merged", Ct);
        merged.Summary.ShouldBe($"Merged {duplicate.Uhid} into {kept.Uhid}: Registered twice at the counter (asked for by "
            + $"{(await mrd.GetAsync<AuthUser>("/api/v1/auth/me")).DisplayName}, approved by {(await verifier.GetAsync<AuthUser>("/api/v1/auth/me")).DisplayName})");
    }

    [Fact]
    public async Task A_merge_asked_for_before_a_record_was_changed_is_not_made()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var verifier = await server.SignInAsync(HospitalServer.Users.MrdVerifier);
        var kept = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var duplicate = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var asked = await mrd.PostAsync<ApprovalRequestDto>($"/api/v1/patients/{kept.Id}/merge", new MergePatientRequest(duplicate.Id, "Registered twice at the counter"));

        // The record that would be kept is changed while the merge waits.
        var categories = await admin.GetAsync<SensitiveCategoriesDto>($"/api/v1/patients/{duplicate.Id}/sensitive-categories");
        await admin.PutAsync<SensitiveCategoriesDto>($"/api/v1/patients/{duplicate.Id}/sensitive-categories",
            new SetSensitiveCategoriesRequest([SensitiveCategory.MentalHealth], "Psychiatric admission recorded on paper", categories.Version));

        (await RefusedAsync(verifier, Approve(asked), new ApproveRequest(null, asked.Version), HttpStatusCode.UnprocessableEntity)).ShouldBe("approval.stale");
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{kept.Id}")).MergedIntoPatientId.ShouldBeNull();
    }

    private static async Task<ApprovalRequestDto> AskForCreditAsync(ApiClient maker, BillDetail bill, decimal amount)
    {
        using var response = await maker.SendRawAsync(HttpMethod.Post, CreditNotes(bill), new IssueCreditNoteRequest(CreditNoteKind.CreditNote, amount, "Rate charged above the tariff"));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
    }

    private static string CreditNotes(BillDetail bill) => $"/api/v1/billing/bills/{bill.Id}/credit-notes";

    private static string Approve(ApprovalRequestDto request) => $"/api/v1/approvals/{request.Id}/approve";

    private static Task<BillDetail> BillAsync(ApiClient client, BillDetail bill) => client.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}");

    /// <summary>Posts to <paramref name="path"/>, is refused with <paramref name="status"/>, and returns the refusal's code.</summary>
    private static async Task<string?> RefusedAsync(ApiClient client, string path, object body, HttpStatusCode status)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    private async Task<List<string>> AuditActionsAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.PatientId == patientId).Select(a => a.Action).ToListAsync(Ct);
    }

    /// <summary>
    /// A new patient's health check-up visit, billed and finalized: the ₹100 registration fee of a first visit (REG-NEW) and
    /// two dressings at ₹250 (no GST), so ₹600 is due.
    /// </summary>
    private static async Task<BillDetail> FinalizedBillAsync(ApiClient frontOffice, ApiClient billing)
    {
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patient.Id, VisitType.HealthCheckup, null, null, null, PayerType.Self, null, ReferralSource.Self, null, null, false, null, null,
            TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
        var dressing = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=PROC-DRESS")).Single(s => s.Code == "PROC-DRESS");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(patient.Id, visit.Id, null, dressing.Id, 2m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        bill.DueAmount.ShouldBe(600m);
        return bill;
    }

    /// <summary>The cashier's counter shift, opened if it is not: only this class signs in as the cashier.</summary>
    private static async Task OpenCounterAsync(ApiClient cashier)
    {
        using var current = await cashier.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        if (current.StatusCode == HttpStatusCode.NoContent)
        {
            await cashier.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 2", OpeningFloat: 0m));
        }
    }

    private static PaymentLineInput Upi(decimal amount) => new(PaymentMode.Upi, amount, "UTR" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), null, null, null, null, null);
}
