using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The plan a patient is priced on changes only when two people agree (maker-checker), over the real API and database: a
/// payer's terms (its plan, co-pay and category, and putting it in use), which the hospital administrator asks for, and a
/// stay billed on another plan than its payer's, which the billing desk asks for; the accountant decides both. Billing's
/// own quote is the witness: an out-patient of the payer, and the stay, are priced on the plan in use until the change is
/// approved, and an approval never overwrites a change made after it was asked for.
/// </summary>
public sealed class PayerPlanApprovalTests(HospitalServer server)
{
    private const string PayersPath = "/api/v1/insurance/payers";
    private const string AccountsPath = "/api/v1/billing/ip-accounts";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_payer_is_out_of_use_until_a_second_person_approves_its_terms()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var plan = await PlanAsync(admin, TariffType.Corporate);
        var created = await admin.PostAsync<PayerDetail>(PayersPath, Payer(plan.Id, coPay: 10m, isActive: true) with { Reason = "Tie-up signed with the company." });
        (created.IsActive, created.TariffPlanId, created.DefaultCoPayPercent).ShouldBe((false, (Guid?)null, 0m), "made out of use, with no plan or co-pay, until approved");
        var pending = created.PendingTerms.ShouldNotBeNull();
        (pending.Asked.IsActive, pending.Asked.TariffPlanId, pending.Asked.DefaultCoPayPercent).ShouldBe((true, (Guid?)plan.Id, 10m));

        // The hospital administrator may approve prices, but not a change they asked for; the accountant decides.
        var request = await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{pending.ApprovalId}");
        (request.Kind, request.CanDecide).ShouldBe((PayerApprovals.Kind, false));
        request.Details.ShouldContain(d => d.Label == "Co-pay" && d.Value.StartsWith("0% → 10%", StringComparison.Ordinal));
        using (var own = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{pending.ApprovalId}/approve", new ApproveRequest(null, request.Version)))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        await ApproveAsAccountantAsync(pending.ApprovalId);
        var live = await admin.GetAsync<PayerDetail>($"{PayersPath}/{created.Id}");
        (live.IsActive, live.TariffPlanId, live.DefaultCoPayPercent, live.PendingTerms).ShouldBe((true, (Guid?)plan.Id, 10m, (PendingPayerTerms?)null));
        (await AuditCountAsync("payer.terms_changed", created.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_payers_plan_waits_and_its_out_patients_are_priced_on_the_plan_in_use_until_it_is_approved()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var (service, _) = await ServiceAsync(admin, 400m);
        var plan = await PlanAsync(admin, TariffType.Corporate, (service, 500m));
        var payer = await server.CreatePayerAsync(admin, Payer(null, coPay: 0m, isActive: true));
        var visit = await VisitAsync(payer.Id);
        (await QuoteAsync(billing, service, visit)).ShouldBe(400m, "no plan of its own: the hospital's default prices its patients");

        var asked = await admin.PutAsync<ApprovalRequestDto>($"{PayersPath}/{payer.Id}", Payer(plan.Id, 0m, true, payer) with { Reason = "Rate card agreed." });
        asked.Kind.ShouldBe(PayerApprovals.Kind);
        asked.Details.ShouldContain(d => d.Label == "Applies to");
        (await QuoteAsync(billing, service, visit)).ShouldBe(400m, "the plan in use until it is approved");

        // While it waits: other terms are refused; saved again as asked, nothing is asked twice and the details save at once.
        var waiting = await admin.GetAsync<PayerDetail>($"{PayersPath}/{payer.Id}");
        waiting.PendingTerms.ShouldNotBeNull().Asked.TariffPlanId.ShouldBe(plan.Id);
        using (var other = await admin.SendRawAsync(HttpMethod.Put, $"{PayersPath}/{payer.Id}", Payer(plan.Id, 20m, true, waiting)))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var renamed = await admin.PutAsync<PayerDetail>($"{PayersPath}/{payer.Id}", Payer(plan.Id, 0m, true, waiting) with { Name = waiting.Name + " Group" });
        (renamed.Name, renamed.PendingTerms.ShouldNotBeNull().ApprovalId).ShouldBe((waiting.Name + " Group", asked.Id));

        await ApproveAsAccountantAsync(asked.Id);
        (await QuoteAsync(billing, service, visit)).ShouldBe(500m, "its own plan, approved");
    }

    [Fact]
    public async Task A_stay_billed_on_another_plan_than_its_payers_waits_and_the_approver_reads_what_it_costs()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var (service, code) = await ServiceAsync(admin, 400m);
        var staff = await PlanAsync(admin, TariffType.Staff, (service, 300m));
        await using var stay = await AdmitAsync();
        var account = stay.Account;
        account.PayerPlanId.ShouldBe(account.TariffPlanId, "a stay is billed on its payer's plan");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(account.PatientId, null, account.AdmissionId, service, 1m, null, null, null, null, null));
        (await UnbilledAsync(billing, account, code)).ShouldBe(400m);

        // Billed on the staff plan: asked for, with what it comes to against the payer's plan; the estimate is saved at once.
        using (var answer = await billing.SendRawAsync(HttpMethod.Put, $"{AccountsPath}/{account.Id}", Update(account, staff.Id, estimate: 25_000m) with { Reason = "Staff member's mother." }))
        {
            answer.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            var asked = (await answer.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
            asked.Kind.ShouldBe(StayPlanApprovals.Kind);
            asked.PatientId.ShouldBe(account.PatientId);
            // What it costs: the dressing ₹100 cheaper on the staff plan than on the payer's (other services the same on both).
            asked.Details.ShouldContain(d => d.Label == "On the plan asked" && d.Value.Contains(": −₹100.00", StringComparison.Ordinal));
            asked.Details.ShouldContain(d => d.Label == "Unbilled services" && d.Value.EndsWith("as priced now", StringComparison.Ordinal));
        }

        var waiting = await billing.GetAsync<IpAccountDetail>($"{AccountsPath}/{account.Id}");
        (waiting.TariffPlanId, waiting.EstimatedAmount).ShouldBe((account.TariffPlanId, 25_000m), "the plan in use until approved; the estimate at once");
        var pending = waiting.PendingPlan.ShouldNotBeNull();
        (pending.Asked.TariffPlanId, pending.TariffPlanName).ShouldBe(((Guid?)staff.Id, staff.Name));
        (await UnbilledAsync(billing, account, code)).ShouldBe(400m);

        // While it waits another plan is refused; the billing desk may not approve discounts; the accountant does.
        var other = await PlanAsync(admin, TariffType.Staff);
        using (var refused = await billing.SendRawAsync(HttpMethod.Put, $"{AccountsPath}/{account.Id}", Update(waiting, other.Id, estimate: 25_000m)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var request = await billing.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{pending.ApprovalId}");
        request.CanDecide.ShouldBeFalse();
        await ApproveAsAccountantAsync(pending.ApprovalId);
        var approved = await billing.GetAsync<IpAccountDetail>($"{AccountsPath}/{account.Id}");
        (approved.TariffPlanId, approved.PendingPlan).ShouldBe(((Guid?)staff.Id, (PendingStayPlan?)null));
        (await UnbilledAsync(billing, account, code)).ShouldBe(300m, "its unbilled services priced again on the plan approved");
        (await QuoteAsync(billing, service, admission: account.AdmissionId)).ShouldBe(300m);
        (await AuditCountAsync("billing.ip.plan_changed", account.Id)).ShouldBe(1);

        // The plan approved is kept while the payer stays: the estimate changes at once, and nothing is asked.
        using (var kept = await billing.SendRawAsync(HttpMethod.Put, $"{AccountsPath}/{account.Id}", Update(approved, staff.Id, estimate: 30_000m)))
        {
            kept.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task A_new_payer_brings_its_own_plan_at_once_and_keeping_the_old_plan_for_it_waits()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var (service, _) = await ServiceAsync(admin, 400m);
        var corporate = await PlanAsync(admin, TariffType.Corporate, (service, 450m));
        var company = await server.CreatePayerAsync(admin, Payer(corporate.Id, coPay: 0m, isActive: true));
        await using var stay = await AdmitAsync();
        var account = stay.Account;
        var general = account.TariffPlanId;

        // The patient's membership with the company, verified by someone other than whoever recorded it, lets the stay go
        // on the company at all.
        await server.VerifiedPolicyAsync(account.PatientId, company.Id);

        // Converted to the company, keeping the self-pay plan by name: another plan than the payer's, so it waits.
        using (var kept = await billing.SendRawAsync(HttpMethod.Put, $"{AccountsPath}/{account.Id}",
                   Update(account, general, estimate: 0m) with { PayerType = PayerType.Corporate, PayerId = company.Id }))
        {
            kept.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            var asked = (await kept.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
            await billing.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/withdraw", new WithdrawApprovalRequest(asked.Version));
        }

        // Converted with the payer's own plan: made at once, and its services are priced on the company's plan from now on.
        var current = await billing.GetAsync<IpAccountDetail>($"{AccountsPath}/{account.Id}");
        using (var converted = await billing.SendRawAsync(HttpMethod.Put, $"{AccountsPath}/{account.Id}",
                   Update(current, null, estimate: 0m) with { PayerType = PayerType.Corporate, PayerId = company.Id }))
        {
            converted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var after = await billing.GetAsync<IpAccountDetail>($"{AccountsPath}/{account.Id}");
        (after.PayerId, after.TariffPlanId, after.PayerPlanId).ShouldBe(((Guid?)company.Id, (Guid?)corporate.Id, (Guid?)corporate.Id));
        (await QuoteAsync(billing, service, admission: account.AdmissionId)).ShouldBe(450m);
    }

    [Fact]
    public async Task An_approval_never_overwrites_a_payer_or_plan_changed_after_it_was_asked_for()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var (service, _) = await ServiceAsync(admin, 400m);
        var staff = await PlanAsync(admin, TariffType.Staff, (service, 300m));
        var other = await PlanAsync(admin, TariffType.Staff, (service, 350m));
        await using var stay = await AdmitAsync();
        var account = stay.Account;
        ApprovalRequestDto asked;
        using (var answer = await billing.SendRawAsync(HttpMethod.Put, $"{AccountsPath}/{account.Id}", Update(account, staff.Id, estimate: 0m)))
        {
            asked = (await answer.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
        }

        // The stay's plan changed by another way in after it was asked for: approving is refused, and it stays as it is.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<IpAccount>().SingleAsync(a => a.Id == account.Id, Ct)).TariffPlanId = other.Id;
            await db.SaveChangesAsync(Ct);
        }

        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var request = await accountant.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");
        using (var refused = await accountant.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, request.Version)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain(TariffApprovals.ChangedCode);
        }

        (await billing.GetAsync<IpAccountDetail>($"{AccountsPath}/{account.Id}")).TariffPlanId.ShouldBe(other.Id);

        // The same for a payer: its co-pay changed by another way in after a new plan was asked for.
        var payer = await server.CreatePayerAsync(admin, Payer(null, coPay: 0m, isActive: true));
        var planAsked = await admin.PutAsync<ApprovalRequestDto>($"{PayersPath}/{payer.Id}", Payer(staff.Id, 0m, true, payer));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<Payer>().SingleAsync(p => p.Id == payer.Id, Ct)).DefaultCoPayPercent = 5m;
            await db.SaveChangesAsync(Ct);
        }

        request = await accountant.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{planAsked.Id}");
        using (var refused = await accountant.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{planAsked.Id}/approve", new ApproveRequest(null, request.Version)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        (await admin.GetAsync<PayerDetail>($"{PayersPath}/{payer.Id}")).TariffPlanId.ShouldBeNull();
    }

    // ---------------------------------------------------------------------- helpers

    private static SavePayerRequest Payer(Guid? planId, decimal coPay, bool isActive, PayerDetail? payer = null) => new(
        payer?.Code ?? "PY" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), payer?.Name ?? $"Kaveri Textiles {Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}",
        PayerCategory.Corporate, "Meena Iyer", "9876543210", null, null, null, planId, CreditDays: 30, DefaultCoPayPercent: coPay, PortalUrl: null,
        IsActive: isActive, Notes: null, Version: payer?.Version);

    private static UpdateIpAccountRequest Update(IpAccountDetail account, Guid? planId, decimal estimate) => new(
        estimate, account.PayerType, account.PayerId, planId, account.PolicyId, RepriceUnbilledCharges: true, Notes: null, account.Version);

    /// <summary>A plan in use (its terms and prices approved by the other administrator), with these prices from today.</summary>
    private async Task<TariffPlanDetail> PlanAsync(ApiClient admin, TariffType type, params (Guid Service, decimal Price)[] prices)
    {
        var plan = await server.SaveTariffPlanAsync(admin, new SaveTariffPlanRequest(
            "PP" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), $"{type} plan {Guid.NewGuid().ToString("N")[..4]}", type, null, null, 0m, false, true, null, null, null));
        if (prices.Length > 0)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
            await server.SaveTariffPricesAsync(admin, plan.Id, [.. prices.Select(p => new SaveTariffPriceRequest(p.Service, p.Price, today.AddDays(-1), null))]);
        }

        return plan;
    }

    /// <summary>A procedure at this base price, in use (its price approved by the other administrator): its id and code.</summary>
    private async Task<(Guid Id, string Code)> ServiceAsync(ApiClient admin, decimal price)
    {
        var service = await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            "SV-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), "Wound dressing (payer plan test)", null, ServiceCategory.Procedure, null, price, 0m, "999312",
            IsDiscountAllowed: true, IsPriceEditable: false, Description: null, IsActive: true, Version: null));
        return (service.Id, service.Code);
    }

    /// <summary>What billing quotes for the service for an out-patient visit or a stay, today.</summary>
    private static async Task<decimal> QuoteAsync(ApiClient client, Guid serviceId, Guid? visit = null, Guid? admission = null) =>
        (await client.GetAsync<Quote>(
            $"/api/v1/billing/tariffs/quote?serviceItemId={serviceId}{(visit is { } v ? $"&visitId={v}" : string.Empty)}{(admission is { } a ? $"&admissionId={a}" : string.Empty)}"))
        .UnitPrice;

    /// <summary>What the stay's unbilled lines of the service come to on its running bill (bed days aside).</summary>
    private static async Task<decimal> UnbilledAsync(ApiClient billing, IpAccountDetail account, string serviceCode) =>
        (await billing.GetAsync<RunningBill>($"{AccountsPath}/{account.Id}/running-bill")).Lines
        .Where(l => l.ServiceCode == serviceCode && l.Status == ChargeStatus.Unbilled)
        .Sum(l => l.NetAmount);

    private async Task ApproveAsAccountantAsync(Guid approvalId)
    {
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var request = await accountant.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        request.CanDecide.ShouldBeTrue();
        (await accountant.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}/approve", new ApproveRequest("Agreed.", request.Version)))
            .Status.ShouldBe(ApprovalStatus.Approved);
    }

    [Fact]
    public async Task The_desk_puts_a_visit_on_a_payer_only_on_the_patients_cover_verified_by_a_second_person()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var company = await server.CreatePayerAsync(admin, Payer(null, coPay: 0m, isActive: true));
        var insurer = (await billing.GetAsync<List<PayerListItem>>(PayersPath)).Single(p => p.Code == "STAR");
        var patient = await RegisterAsync(frontOffice);

        // A payer of the kind chosen, named: no "company" pricing without a company, nor an insurer billed as a company
        // (which would pay without its approval).
        using (var unnamed = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/visits", Visit(patient, PayerType.Corporate, null)))
        {
            await RefusedAsync(unnamed, HttpStatusCode.BadRequest, "Choose the company that pays.");
        }

        using (var miscast = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/visits", Visit(patient, PayerType.Corporate, insurer.Id)))
        {
            await RefusedAsync(miscast, HttpStatusCode.BadRequest, $"{insurer.Name} pays as an insurer, not as a company.");
        }

        // The company, with no membership of the patient recorded: refused, saying what to do.
        using (var uncovered = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/visits", Visit(patient, PayerType.Corporate, company.Id)))
        {
            await RefusedAsync(uncovered, HttpStatusCode.UnprocessableEntity, "payer.no_cover");
        }

        // The billing desk records the membership: not yet; it may not verify it itself, and is told so before it tries.
        var number = "EMP-" + Letters(8).ToUpperInvariant();
        var policy = await billing.PostAsync<PolicyDto>("/api/v1/insurance/policies",
            Membership(patient, company.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(6), number));
        using (var unverified = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/visits", Visit(patient, PayerType.Corporate, company.Id)))
        {
            await RefusedAsync(unverified, HttpStatusCode.UnprocessableEntity, "payer.cover_not_verified");
        }

        (await billing.GetAsync<PolicyDto>($"/api/v1/insurance/policies/{policy.Id}")).RecordedByYou.ShouldBeTrue();
        (await admin.GetAsync<PolicyDto>($"/api/v1/insurance/policies/{policy.Id}")).RecordedByYou.ShouldBeFalse();
        using (var own = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/insurance/policies/{policy.Id}/verify", new VerifyPolicyRequest("Checked")))
        {
            await RefusedAsync(own, HttpStatusCode.Forbidden, "insurance.policy_self_verify");
        }

        // Another person verifies it with the company: the visit goes on the company.
        await admin.PostAsync($"/api/v1/insurance/policies/{policy.Id}/verify", new VerifyPolicyRequest("Employee id confirmed with the company's HR"));
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", Visit(patient, PayerType.Corporate, company.Id));
        (visit.PayerType, visit.PayerId).ShouldBe((PayerType.Corporate, (Guid?)company.Id));

        // Its card typed again unchanged, or a note, keeps the verification; extending its validity withdraws it, and
        // whoever extended it may not verify it again: the desk is refused until someone else does.
        var current = await billing.GetAsync<PolicyDto>($"/api/v1/insurance/policies/{policy.Id}");
        await billing.PutAsync($"/api/v1/insurance/policies/{policy.Id}", Membership(patient, company.Id, current.ValidTo, $" {number} ", current) with { Notes = "Card seen again" });
        current = await billing.GetAsync<PolicyDto>($"/api/v1/insurance/policies/{policy.Id}");
        current.VerifiedAt.ShouldNotBeNull();
        await admin.PutAsync($"/api/v1/insurance/policies/{policy.Id}", Membership(patient, company.Id, current.ValidTo.AddYears(1), null, current));
        current = await admin.GetAsync<PolicyDto>($"/api/v1/insurance/policies/{policy.Id}");
        (current.VerifiedAt, current.RecordedByYou).ShouldBe((null, true));
        using (var withdrawn = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/visits", Visit(patient, PayerType.Corporate, company.Id)))
        {
            await RefusedAsync(withdrawn, HttpStatusCode.UnprocessableEntity, "payer.cover_not_verified");
        }

        using (var again = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/insurance/policies/{policy.Id}/verify", new VerifyPolicyRequest("Checked")))
        {
            await RefusedAsync(again, HttpStatusCode.Forbidden, "insurance.policy_self_verify");
        }

        await billing.PostAsync($"/api/v1/insurance/policies/{policy.Id}/verify", new VerifyPolicyRequest("Extension confirmed with the company's HR"));
        (await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", Visit(patient, PayerType.Corporate, company.Id))).PayerId.ShouldBe(company.Id);

        // The patient pays for a visit whatever payer is sent with it: no payer's plan without the payer.
        var paying = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", Visit(patient, PayerType.Self, company.Id));
        (paying.PayerType, paying.PayerId).ShouldBe((PayerType.Self, (Guid?)null));

        // A policy its own recorder marked verified before this rule does not count.
        var another = await RegisterAsync(frontOffice);
        var selfVerified = await billing.PostAsync<PolicyDto>("/api/v1/insurance/policies",
            Membership(another, company.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(6), "EMP-" + Letters(8).ToUpperInvariant()));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var recorder = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PatientPolicy>()
                .Where(p => p.Id == selfVerified.Id).Select(p => p.CreatedBy).SingleAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PatientPolicy>().Where(p => p.Id == selfVerified.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.VerifiedAt, DateTimeOffset.UtcNow).SetProperty(p => p.VerifiedBy, recorder), Ct);
        }

        using (var legacy = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/visits", Visit(another, PayerType.Corporate, company.Id)))
        {
            await RefusedAsync(legacy, HttpStatusCode.UnprocessableEntity, "payer.cover_not_verified");
        }
    }

    [Fact]
    public async Task A_stay_goes_on_a_payer_only_on_the_patients_cover_verified_by_a_second_person()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var company = await server.CreatePayerAsync(admin, Payer(null, coPay: 0m, isActive: true));

        // Admitted on the company with no verified membership: refused at the admission desk.
        var patient = await RegisterAsync(frontOffice);
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWM");
        var bed = await TestBeds.AddAsync(server, ward.Id);
        using (var refused = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/inpatient/admissions", new AdmitPatientRequest(
                   PatientId: patient, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
                   DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
                   ProvisionalDiagnosis: "Cellulitis of the right leg", ReferredBy: null, PayerType: PayerType.Corporate, PayerId: company.Id,
                   PolicyNumber: null, IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null,
                   AttendantPhone: null, AdmittedAt: null, Notes: null)))
        {
            await RefusedAsync(refused, HttpStatusCode.UnprocessableEntity, "payer.no_cover");
        }

        // Admitted as paying, the stay goes on the company only once a second person has verified the membership.
        await using var stay = await AdmitAsync();
        var account = stay.Account;
        using (var uncovered = await billing.SendRawAsync(HttpMethod.Put, $"{AccountsPath}/{account.Id}",
                   Update(account, null, estimate: 0m) with { PayerType = PayerType.Corporate, PayerId = company.Id }))
        {
            await RefusedAsync(uncovered, HttpStatusCode.UnprocessableEntity, "payer.no_cover");
        }

        var policy = await billing.PostAsync<PolicyDto>("/api/v1/insurance/policies",
            Membership(account.PatientId, company.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(6), "EMP-" + Letters(8).ToUpperInvariant()));
        using (var unverified = await billing.SendRawAsync(HttpMethod.Put, $"{AccountsPath}/{account.Id}",
                   Update(account, null, estimate: 0m) with { PayerType = PayerType.Corporate, PayerId = company.Id }))
        {
            await RefusedAsync(unverified, HttpStatusCode.UnprocessableEntity, "payer.cover_not_verified");
        }

        await admin.PostAsync($"/api/v1/insurance/policies/{policy.Id}/verify", new VerifyPolicyRequest("Employee id confirmed with the company's HR"));
        using (var covered = await billing.SendRawAsync(HttpMethod.Put, $"{AccountsPath}/{account.Id}",
                   Update(account, null, estimate: 0m) with { PayerType = PayerType.Corporate, PayerId = company.Id }))
        {
            covered.StatusCode.ShouldBe(HttpStatusCode.NoContent, await covered.Content.ReadAsStringAsync(Ct));
        }

        (await billing.GetAsync<IpAccountDetail>($"{AccountsPath}/{account.Id}")).PayerId.ShouldBe(company.Id);
    }

    private async Task<int> AuditCountAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = entityId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().CountAsync(e => e.Action == action && e.EntityId == id, Ct);
    }

    /// <summary>An out-patient visit of a new patient, paid for by the payer.</summary>
    private async Task<Guid> VisitAsync(Guid payerId)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await RegisterAsync(frontOffice);
        await server.VerifiedPolicyAsync(patient, payerId);
        return (await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", Visit(patient, PayerType.Corporate, payerId))).Id;
    }

    private static CreateVisitRequest Visit(Guid patient, PayerType payerType, Guid? payerId) => new(
        patient, VisitType.HealthCheckup, null, null, null, payerType, payerId, ReferralSource.Corporate, null, null, false, null, null,
        TokenPriority.Normal, IssueToken: false, NotifyPatient: false);

    private static SavePolicyRequest Membership(Guid patient, Guid payerId, DateOnly validTo, string? number, PolicyDto? policy = null) => new(
        patient, payerId, null, number, null, null, null, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30), validTo, 0m, 0m, IsActive: true,
        CardFileId: null, Notes: null, Version: policy?.Version);

    private static async Task RefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        body.ShouldContain(code);
    }

    private static async Task<Guid> RegisterAsync(ApiClient frontOffice) =>
        (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Suresh"), MiddleName: null, LastName: "Plan" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false))).Id;

    /// <summary>A new patient admitted, paying for himself, to a bed of the male general ward of the test's own.</summary>
    private async Task<Stay> AdmitAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var patient = await RegisterAsync(frontOffice);
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWM");
        var bed = await TestBeds.AddAsync(server, ward.Id);
        var admission = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Cellulitis of the right leg", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
        return new Stay(server, frontOffice, await billing.GetAsync<IpAccountDetail>($"{AccountsPath}/by-admission/{admission.Id}"), ward.Id);
    }

    /// <summary>
    /// The patient's stay, with his IP account. When the test ends the stay is cancelled and the nurse cleans the bed it
    /// left, as the shared ward needs.
    /// </summary>
    private sealed class Stay(HospitalServer server, ApiClient frontOffice, IpAccountDetail account, Guid wardId) : IAsyncDisposable
    {
        public IpAccountDetail Account => account;

        public async ValueTask DisposeAsync()
        {
            (await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/inpatient/admissions/{account.AdmissionId}/cancel",
                new CancelAdmissionRequest("Admitted for a test of the payer's plan"))).Dispose();
            var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
            foreach (var task in (await nurse.GetAsync<List<HousekeepingTaskDto>>($"/api/v1/beds/housekeeping?wardId={wardId}"))
                     .Where(t => t.AdmissionId == account.AdmissionId && t.Status == HousekeepingStatus.Pending))
            {
                await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/start");
                await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/complete", new BedNoteRequest("Cleaned after the stay"));
            }
        }
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Quote(decimal UnitPrice);
}
