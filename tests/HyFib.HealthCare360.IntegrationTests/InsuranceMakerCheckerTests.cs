using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Maker-checker on insurance money (MAKER-CHECKER.md, slice 12). What the insurer will not pay (its deductions, or the
/// whole claim when it rejects it) is written off only once a second person approves it, unless it is within the amount
/// the facility lets the desk write off alone (none by default); loosening that amount waits for a second administrator.
/// The amount the insurer pre-authorises (or raises, or finally approves) moves the bill from the patient to the insurer
/// only once a second insurance desk user has checked it against the insurer's letter; until then the patient pays it.
/// </summary>
public sealed class InsuranceMakerCheckerTests(HospitalServer server)
{
    private const string Claims = "/api/v1/insurance/claims";
    private const string PreAuths = "/api/v1/insurance/preauths";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_insurers_deductions_are_written_off_only_when_a_second_person_approves_and_the_money_received_is_posted_at_once()
    {
        var desk = await server.SignInAsync(Users.InsuranceDesk);
        var secondDesk = await server.SignInAsync(Users.SecondInsuranceDesk);
        var accountant = await server.SignInAsync(Users.Accountant);
        var bill = await CompanyBillAsync();
        var total = bill.PayerAmount;
        var claim = await ApprovedClaimAsync(desk, bill.Id, total);

        // A settlement over a claim that changed since the desk looked is refused.
        var stale = claim with { Version = claim.Version - 1 };
        (await RefusalAsync(desk, $"{Claims}/{claim.Id}/settle", Settlement(stale, total - 200m, 200m))).Status.ShouldBe(HttpStatusCode.Conflict);

        // All but ₹200 received, ₹200 deducted: the money is posted now, the deduction waits for someone else.
        var asked = await AskedAsync(desk, $"{Claims}/{claim.Id}/settle", Settlement(claim, total - 200m, 200m));
        (asked.Kind, asked.SubjectId, asked.Status).ShouldBe((ClaimService.WriteOffKind, claim.Id, ApprovalStatus.Pending));
        asked.Details.Single(d => d.Label == "Deduction").Value.ShouldBe("₹200.00: Non-medical items");
        asked.Details.Single(d => d.Label == "Why it waits").Value.ShouldBe("Every insurance write-off is approved by a second person");
        var waiting = await desk.GetAsync<ClaimDetail>($"{Claims}/{claim.Id}");
        (waiting.Status, waiting.SettledAmount, waiting.DeductionAmount, waiting.Outstanding).ShouldBe((ClaimStatus.PartiallySettled, total - 200m, 0m, 200m));
        waiting.WriteOffAskedAt.ShouldNotBeNull();
        waiting.Settlements.ShouldHaveSingleItem().ReceiptId.ShouldNotBeNull("the money received is a payer receipt at once");
        var billing = await server.SignInAsync(Users.Billing);
        var owed = await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}");
        (owed.PayerSettledAmount, owed.PayerDisallowedAmount).ShouldBe((total - 200m, 0m), "the insurer still owes the deduction until it is approved");

        // Not the insurance desk, which records claims but may not approve write-offs (the one who asked, nor another).
        (await RefusalAsync(desk, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
        (await RefusalAsync(secondDesk, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
        (await desk.GetAsync<ClaimDetail>($"{Claims}/{claim.Id}")).DeductionAmount.ShouldBe(0m);

        // The accountant approves: the deduction is written off, the claim settled, the audit names both.
        await MakerChecker.ApproveAsync(accountant, asked);
        var settled = await desk.GetAsync<ClaimDetail>($"{Claims}/{claim.Id}");
        (settled.Status, settled.SettledAmount, settled.DeductionAmount, settled.Outstanding).ShouldBe((ClaimStatus.Settled, total - 200m, 200m, 0m));
        settled.WriteOffAskedAt.ShouldBeNull();
        settled.Deductions.ShouldHaveSingleItem().Reason.ShouldBe("Non-medical items");
        (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}")).PayerDisallowedAmount.ShouldBe(200m);
        var written = await AuditAsync("insurance.claim.written_off", claim.Id);
        written.Summary.ShouldNotBeNull().ShouldContain("asked for by Farah Qureshi, approved by");
        written.Severity.ShouldBe(AuditSeverity.Warning);
    }

    [Fact]
    public async Task A_rejection_changes_nothing_until_approved_and_an_approval_over_a_claim_that_changed_since_is_stale()
    {
        var desk = await server.SignInAsync(Users.InsuranceDesk);
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var bill = await CompanyBillAsync();
        var total = bill.PayerAmount;
        var claim = await SubmittedClaimAsync(desk, bill.Id);

        var asked = await AskedAsync(desk, $"{Claims}/{claim.Id}/reject", new RejectClaimRequest("Not covered under the policy", claim.Version));
        asked.Summary.ShouldContain($"write off the ₹{Money.FormatInr(total)} the insurer still owes");
        var waiting = await desk.GetAsync<ClaimDetail>($"{Claims}/{claim.Id}");
        (waiting.Status, waiting.Outstanding, waiting.RejectionReason).ShouldBe((ClaimStatus.Submitted, total, null));
        (await RefusalAsync(desk, $"{Claims}/{claim.Id}/reject", new RejectClaimRequest("Again", waiting.Version)))
            .ShouldBe((HttpStatusCode.Conflict, "approval.already_requested"));

        // The insurer raises a query meanwhile: the rejection asked about the claim as it was, and is not made.
        await desk.PostAsync($"{Claims}/{claim.Id}/query", new ClaimTextRequest("Send the discharge summary"));
        (await RefusalAsync(admin, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).Code.ShouldBe("approval.stale");
        (await desk.GetAsync<ClaimDetail>($"{Claims}/{claim.Id}")).Status.ShouldBe(ClaimStatus.Query);

        // Asked again over the claim as it is now, by an administrator, who may approve write-offs but not one they asked
        // for; the accountant approves: the claim is rejected and the bill's payer share disallowed.
        var current = await desk.GetAsync<ClaimDetail>($"{Claims}/{claim.Id}");
        var again = await AskedAsync(admin, $"{Claims}/{claim.Id}/reject", new RejectClaimRequest("Not covered under the policy", current.Version));
        (await RefusalAsync(admin, $"/api/v1/approvals/{again.Id}/approve", new ApproveRequest(null, again.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        await MakerChecker.ApproveAsync(await server.SignInAsync(Users.Accountant), again);
        var rejected = await desk.GetAsync<ClaimDetail>($"{Claims}/{claim.Id}");
        (rejected.Status, rejected.RejectionReason).ShouldBe((ClaimStatus.Rejected, "Not covered under the policy"));
        (await admin.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}")).PayerDisallowedAmount.ShouldBe(total);
        (await AuditAsync("insurance.claim.written_off", claim.Id)).Summary.ShouldNotBeNull().ShouldContain("(claim rejected; asked for by ");
    }

    [Fact]
    public async Task A_write_off_within_the_facilitys_amount_is_made_at_once_and_raising_that_amount_waits_for_a_second_administrator()
    {
        const string key = ClaimService.WriteOffApprovalAboveSetting;
        var desk = await server.SignInAsync(Users.InsuranceDesk);
        var maker = await server.SignInAsync(Users.HospitalAdmin);
        var checker = await server.SignInAsync(Users.Admin);
        var facilityId = (await maker.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        await using var restore = await KeepAsync(maker, key, facilityId);

        // Raising the amount the desk writes off alone waits for another administrator.
        var asked = await AskSettingAsync(maker, key, JsonValue.Create(500m), facilityId);
        asked.Kind.ShouldBe(SettingGuard.ChangeKind);
        (await RefusalAsync(maker, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        var bill = await CompanyBillAsync();
        var claim = await ApprovedClaimAsync(desk, bill.Id, bill.PayerAmount);
        claim.WriteOffApprovalAbove.ShouldBe(0m, "not raised until approved");
        await MakerChecker.ApproveAsync(checker, asked);

        // ₹500 deducted, no more than the amount: settled at once, nothing asked.
        claim = await desk.GetAsync<ClaimDetail>($"{Claims}/{claim.Id}");
        claim.WriteOffApprovalAbove.ShouldBe(500m);
        using (var atOnce = await desk.SendRawAsync(HttpMethod.Post, $"{Claims}/{claim.Id}/settle", Settlement(claim, bill.PayerAmount - 500m, 500m)))
        {
            atOnce.StatusCode.ShouldBe(HttpStatusCode.NoContent, await atOnce.Content.ReadAsStringAsync(Ct));
        }

        var settled = await desk.GetAsync<ClaimDetail>($"{Claims}/{claim.Id}");
        (settled.Status, settled.DeductionAmount, settled.WriteOffAskedAt).ShouldBe((ClaimStatus.Settled, 500m, null));

        // More than the amount still waits.
        var next = await CompanyBillAsync();
        var bigger = await ApprovedClaimAsync(desk, next.Id, next.PayerAmount);
        (await AskedAsync(desk, $"{Claims}/{bigger.Id}/settle", Settlement(bigger, next.PayerAmount - 600m, 600m))).Details
            .Single(d => d.Label == "Why it waits").Value.ShouldBe("More than ₹500.00, the most the desk writes off alone");
    }

    [Fact]
    public async Task The_pre_authorised_amount_moves_the_bill_to_the_insurer_only_once_a_second_insurance_desk_user_checks_it()
    {
        var desk = await server.SignInAsync(Users.InsuranceDesk);
        var secondDesk = await server.SignInAsync(Users.SecondInsuranceDesk);
        var billing = await server.SignInAsync(Users.Billing);
        var insurer = (await billing.GetAsync<List<PayerListItem>>("/api/v1/insurance/payers")).Single(p => p.Code == "STAR");
        var (_, visitId, policyId) = await VisitOnAsync(insurer.Id, PayerType.Insurance, 1000m);
        var preAuthId = (await desk.PostAsync<JsonElement>(PreAuths, new SavePreAuthRequest(
            policyId, null, visitId, 1000m, "L03.116", null, "Cellulitis of the left leg; day-care IV antibiotics.", null, null, null))).GetProperty("id").GetGuid();
        await desk.PostAsync($"{PreAuths}/{preAuthId}/submit");
        var submitted = await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{preAuthId}");

        // The insurer approves ₹600: asked for, not recorded; the bill made now leaves it all to the patient.
        var asked = await AskedAsync(desk, $"{PreAuths}/{preAuthId}/approve", new ApprovePreAuthRequest(600m, "SH-PA-1001", submitted.Version));
        (asked.Kind, asked.SubjectId).ShouldBe((PreAuthService.AmountKind, preAuthId));
        asked.Details.Single(d => d.Label == "Insurer pays up to").Value.ShouldStartWith("₹0.00 → ₹600.00");
        var waiting = await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{preAuthId}");
        (waiting.Status, waiting.ApprovedAmount, waiting.ApprovalReference).ShouldBe((PreAuthStatus.Submitted, 0m, null));
        waiting.AmountAskedAt.ShouldNotBeNull();
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visitId, null, null));
        (draft.PayerAmount, draft.PatientAmount).ShouldBe((0m, draft.NetAmount), "until it is checked the patient pays the pre-authorised part");
        using (var discarded = await billing.SendRawAsync(HttpMethod.Delete, $"/api/v1/billing/bills/{draft.Id}?version={draft.Version}"))
        {
            discarded.IsSuccessStatusCode.ShouldBeTrue(await discarded.Content.ReadAsStringAsync(Ct));
        }

        // The insurer raises a query meanwhile: the approval asked about the pre-authorisation as it was, and is not recorded.
        await desk.PostAsync($"{PreAuths}/{preAuthId}/query", new PreAuthTextRequest("Insurer asks for the latest culture report"));
        (await RefusalAsync(secondDesk, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).Code.ShouldBe("approval.stale");
        (await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{preAuthId}")).ApprovedAmount.ShouldBe(0m);
        await desk.PostAsync($"{PreAuths}/{preAuthId}/answer", new PreAuthTextRequest("Culture report sent"));
        var answered = await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{preAuthId}");
        asked = await AskedAsync(desk, $"{PreAuths}/{preAuthId}/approve", new ApprovePreAuthRequest(600m, "SH-PA-1001", answered.Version));

        // Not the one who recorded it; not someone who may not manage pre-authorisations.
        (await RefusalAsync(desk, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        (await RefusalAsync(billing, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));

        // Another insurance desk user checks it against the letter: recorded, and the insurer pays ₹600 of the bill.
        await MakerChecker.ApproveAsync(secondDesk, asked);
        var approved = await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{preAuthId}");
        (approved.Status, approved.ApprovedAmount, approved.ApprovalReference, approved.AmountAskedAt)
            .ShouldBe((PreAuthStatus.PartiallyApproved, 600m, "SH-PA-1001", null));
        var bill = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visitId, null, null));
        (bill.PayerAmount, bill.PatientAmount).ShouldBe((600m, bill.NetAmount - 600m));
        var audited = await AuditAsync("insurance.preauth.approved", preAuthId);
        audited.Summary.ShouldNotBeNull().ShouldContain("Farah Qureshi");
        audited.Summary.ShouldContain("Vikram Joshi");

        // An enhancement the insurer approves waits for the check too.
        await desk.PostAsync($"{PreAuths}/{preAuthId}/enhancements", new RequestEnhancementRequest(300m, "Two more days of IV antibiotics"));
        var raised = await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{preAuthId}");
        var enhancement = raised.Enhancements.ShouldHaveSingleItem();
        var more = await AskedAsync(desk, $"{PreAuths}/{preAuthId}/enhancements/{enhancement.Id}/respond",
            new RespondEnhancementRequest(true, 300m, "SH-EN-1002", raised.Version));
        (await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{preAuthId}")).EffectiveApprovedAmount.ShouldBe(600m);
        (await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{preAuthId}")).Enhancements.ShouldHaveSingleItem().Status.ShouldBe(EnhancementStatus.Pending);
        await MakerChecker.ApproveAsync(secondDesk, more);
        (await desk.GetAsync<PreAuthDetail>($"{PreAuths}/{preAuthId}")).EffectiveApprovedAmount.ShouldBe(900m);
        (await AuditAsync("insurance.preauth.enhancement", preAuthId)).Summary.ShouldNotBeNull().ShouldContain("checked by Vikram Joshi");
    }

    /// <summary>A company's new patient with a ₹1,000 charge (and whatever a first visit adds), billed and finalized: the company pays it all.</summary>
    private async Task<BillDetail> CompanyBillAsync()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var company = await server.CreatePayerAsync(admin, new SavePayerRequest(
            "CO" + Letters(8).ToUpperInvariant(), $"Kaveri Textiles {Letters(5).ToUpperInvariant()}", PayerCategory.Corporate, "Meena Iyer", "9876543210",
            null, null, null, null, CreditDays: 30, DefaultCoPayPercent: 0m, PortalUrl: null, IsActive: true, Notes: null, Version: null));
        var (patientId, visitId, _) = await VisitOnAsync(company.Id, PayerType.Corporate, 1000m);
        var billing = await server.SignInAsync(Users.Billing);
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visitId, null, null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        (bill.PatientId, bill.PayerAmount, bill.PatientAmount).ShouldBe((patientId, bill.NetAmount, 0m));
        return bill;
    }

    /// <summary>A health check of a new patient on <paramref name="payerId"/> under a verified policy, with one charge of <paramref name="price"/>.</summary>
    private async Task<(Guid PatientId, Guid VisitId, Guid PolicyId)> VisitOnAsync(Guid payerId, PayerType payerType, decimal price)
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var patientId = (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Ravi"), MiddleName: null, LastName: "Claim" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false))).Id;
        var policyId = (await server.VerifiedPolicyAsync(patientId, payerId)).Id;

        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.HealthCheckup, null, null, null, payerType, payerId, payerType == PayerType.Corporate ? ReferralSource.Corporate : ReferralSource.Self,
            null, null, false, null, null, TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
        var service = await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            $"SV-{Letters(8)}", "Day-care review", null, ServiceCategory.Miscellaneous, null, price, 0m, null, true, false, null, true, null));
        var billing = await server.SignInAsync(Users.Billing);
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(patientId, visit.Id, null, service.Id, 1m, null, null, null, null, null));
        return (patientId, visit.Id, policyId);
    }

    private static async Task<ClaimDetail> SubmittedClaimAsync(ApiClient desk, Guid billId)
    {
        var id = (await desk.PostAsync<JsonElement>(Claims, new CreateClaimRequest(billId, null, null, null, null, null))).GetProperty("id").GetGuid();
        return await desk.GetAsync<ClaimDetail>($"{Claims}/{id}");
    }

    private static async Task<ClaimDetail> ApprovedClaimAsync(ApiClient desk, Guid billId, decimal amount)
    {
        var claim = await SubmittedClaimAsync(desk, billId);
        await desk.PostAsync($"{Claims}/{claim.Id}/approve", new ApproveClaimRequest(amount));
        return await desk.GetAsync<ClaimDetail>($"{Claims}/{claim.Id}");
    }

    private static SettleClaimRequest Settlement(ClaimDetail claim, decimal received, decimal deducted) => new(
        received, deducted > 0 ? [new DeductionInput("Non-medical items", deducted)] : null, "UTR" + Digits(12), "Settlement advice", null, claim.Version);

    /// <summary>Sends a change that waits (202): the request, as the server answers it.</summary>
    private static async Task<ApprovalRequestDto> AskedAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, path);
    }

    private static async Task<ApprovalRequestDto> AskSettingAsync(ApiClient admin, string key, JsonNode value, Guid facilityId)
    {
        var path = $"/api/v1/admin/settings/{key}";
        using var response = await admin.SendRawAsync(HttpMethod.Put, path,
            new SaveSettingRequest(value, facilityId, false, null, await admin.SettingVersionAsync(key, facilityId), "Small deductions are routine at this desk."));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Put, path);
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        response.IsSuccessStatusCode.ShouldBeFalse();
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (response.StatusCode, problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private async Task<AuditEvent> AuditAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.Action == action && a.EntityId == entityId.ToString()).OrderByDescending(a => a.Id).FirstAsync(Ct);
    }

    /// <summary>Puts the facility's setting back as it was, withdrawing any change still waiting.</summary>
    private async Task<IAsyncDisposable> KeepAsync(ApiClient maker, string key, Guid facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var before = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<SystemSetting>().AsNoTracking()
            .Where(s => s.Key == key && s.FacilityId == facilityId).Select(s => s.Value).FirstOrDefaultAsync(Ct);
        return new Restore(async () =>
        {
            await using var restore = server.Services.CreateAsyncScope();
            var db = restore.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var subject = SettingGuard.SubjectId(key, facilityId);
            foreach (var waiting in await db.Set<ApprovalRequest>().IgnoreQueryFilters().AsNoTracking()
                         .Where(r => r.SubjectId == subject && r.Status == ApprovalStatus.Pending).ToListAsync(CancellationToken.None))
            {
                using var _ = await maker.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{waiting.Id}/withdraw", new WithdrawApprovalRequest(waiting.Version));
            }

            var settings = restore.ServiceProvider.GetRequiredService<ISettingsService>();
            if (before is null)
            {
                await settings.RemoveAsync(key, facilityId, CancellationToken.None);
            }
            else
            {
                await settings.SetAsync(key, JsonNode.Parse(before), facilityId, cancellationToken: CancellationToken.None);
            }
        });
    }

    private static string Letters(int count) => new(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => Random.Shared.Next(10)));

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(restore());
    }
}
