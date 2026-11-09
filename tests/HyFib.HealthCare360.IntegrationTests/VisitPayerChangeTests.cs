using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A visit's payer changed after registration (BIL-OP; MAKER-CHECKER.md, gap B): the visit's unbilled services are priced
/// again on the new payer's plan. The change is made at once when the payer stays of the same kind and no service gets
/// cheaper; to another kind of payer, or to a plan that prices a service lower, it waits for someone who approves
/// discounts, never the asker. A visit whose bill is drafted or final keeps its payer. The service, the two companies and
/// their plans are this test's own: the dear company's plan prices the service at ₹1,200, the cheap one's at ₹800.
/// </summary>
public sealed class VisitPayerChangeTests(HospitalServer server)
{
    private const decimal Dear = 1_200m;
    private const decimal Cheap = 800m;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_visits_services_are_priced_again_at_once_when_the_payer_stays_of_its_kind_and_nothing_gets_cheaper()
    {
        var billing = await server.SignInAsync(Users.Billing);
        var (serviceId, cheap, dear) = await CompaniesAsync();
        var (visitId, chargeId) = await VisitAsync(cheap, PayerType.Corporate, serviceId, policiesWith: [dear]);
        (await NetAsync(chargeId)).ShouldBe(Cheap);

        var view = await billing.GetAsync<VisitPayerView>(PayerPath(visitId));
        (view.Terms, view.Pending).ShouldBe((new VisitPayerTerms(PayerType.Corporate, cheap), null));

        using var changed = await billing.SendRawAsync(HttpMethod.Post, PayerPath(visitId), new ChangeVisitPayerRequest(PayerType.Corporate, dear, "Employer changed", view.Version));
        changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Ct));
        (await billing.GetAsync<VisitPayerView>(PayerPath(visitId))).Terms.ShouldBe(new VisitPayerTerms(PayerType.Corporate, dear));
        (await NetAsync(chargeId)).ShouldBe(Dear);
        var audit = await AuditAsync(visitId);
        audit.Summary.ShouldContain("1 unbilled service(s) priced again (₹800.00 → ₹1,200.00)");
        audit.Details.ShouldContain("Employer changed");
    }

    [Fact]
    public async Task A_payer_that_prices_lower_or_is_of_another_kind_waits_for_a_second_person()
    {
        var billing = await server.SignInAsync(Users.Billing);
        var accountant = await server.SignInAsync(Users.Accountant);
        var (serviceId, cheap, dear) = await CompaniesAsync();
        var (visitId, chargeId) = await VisitAsync(dear, PayerType.Corporate, serviceId, policiesWith: [cheap]);

        // To the cheaper company: asked for, nothing changes yet.
        var view = await billing.GetAsync<VisitPayerView>(PayerPath(visitId));
        var asked = await AskedAsync(billing, visitId, new ChangeVisitPayerRequest(PayerType.Corporate, cheap, "Wrong company at the desk", view.Version));
        (asked.Kind, asked.SubjectId, asked.Status).ShouldBe((VisitPayerChanges.Kind, visitId, ApprovalStatus.Pending));
        asked.Details.Single(d => d.Label == "Why it waits").Value.ShouldBe("1 service(s) get cheaper on the new payer's plan");
        asked.Details.Single(d => d.Label == "On the new payer's plan").Value.ShouldStartWith("₹800.00: −₹400.00");
        var waiting = await billing.GetAsync<VisitPayerView>(PayerPath(visitId));
        (waiting.Terms.PayerId, waiting.Pending!.Asked.PayerId).ShouldBe((dear, cheap));
        (await NetAsync(chargeId)).ShouldBe(Dear);

        // A second change waits behind the first; the asker cannot approve it; the accountant does.
        (await RefusalAsync(billing, visitId, new ChangeVisitPayerRequest(PayerType.Self, null, null, waiting.Version))).ShouldBe((HttpStatusCode.Conflict, "billing.visit_payer_pending"));
        await ApproveAsync(accountant, asked.Id);
        (await billing.GetAsync<VisitPayerView>(PayerPath(visitId))).Terms.PayerId.ShouldBe(cheap);
        (await NetAsync(chargeId)).ShouldBe(Cheap);
        (await AuditAsync(visitId)).Summary.ShouldContain("approved by");

        // To self-pay, another kind of payer: waits too, though the service is dearer on the general plan.
        var now = await billing.GetAsync<VisitPayerView>(PayerPath(visitId));
        var toSelf = await AskedAsync(billing, visitId, new ChangeVisitPayerRequest(PayerType.Self, null, "Patient pays", now.Version));
        toSelf.Details.Single(d => d.Label == "Why it waits").Value.ShouldBe("The payer changes kind (company → self-pay)");
    }

    [Fact]
    public async Task A_visit_whose_bill_is_drafted_or_final_keeps_its_payer()
    {
        var billing = await server.SignInAsync(Users.Billing);
        var (serviceId, cheap, dear) = await CompaniesAsync();
        var (visitId, _) = await VisitAsync(cheap, PayerType.Corporate, serviceId, policiesWith: [dear]);
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visitId, null, null));

        var view = await billing.GetAsync<VisitPayerView>(PayerPath(visitId));
        (await RefusalAsync(billing, visitId, new ChangeVisitPayerRequest(PayerType.Corporate, dear, null, view.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "billing.draft_bill_exists"));

        await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        (await RefusalAsync(billing, visitId, new ChangeVisitPayerRequest(PayerType.Corporate, dear, null, view.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "billing.visit_bill_final"));
        (await billing.GetAsync<VisitPayerView>(PayerPath(visitId))).Terms.PayerId.ShouldBe(cheap);

        // Nor does a third party the patient has no verified cover with pay.
        var (otherVisit, _) = await VisitAsync(cheap, PayerType.Corporate, serviceId, policiesWith: []);
        var other = await billing.GetAsync<VisitPayerView>(PayerPath(otherVisit));
        (await RefusalAsync(billing, otherVisit, new ChangeVisitPayerRequest(PayerType.Corporate, dear, null, other.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "payer.no_cover"));

        // Only those who bill a visit change its payer here.
        var nurse = await server.SignInAsync(Users.Nurse);
        using var forbidden = await nurse.SendRawAsync(HttpMethod.Post, PayerPath(otherVisit), new ChangeVisitPayerRequest(PayerType.Self, null, null, other.Version));
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static string PayerPath(Guid visitId) => $"/api/v1/billing/visits/{visitId}/payer";

    /// <summary>A service of this test's own, and two companies whose own plans price it at ₹800 and ₹1,200.</summary>
    private async Task<(Guid ServiceId, Guid Cheap, Guid Dear)> CompaniesAsync()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var service = await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            $"SV-{Letters(8)}", "Company health review", null, ServiceCategory.Miscellaneous, null, 1_000m, 0m, null, true, false, null, true, null));
        var from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        return (service.Id, await CompanyAsync(admin, service.Id, Cheap, from), await CompanyAsync(admin, service.Id, Dear, from));
    }

    private async Task<Guid> CompanyAsync(ApiClient admin, Guid serviceId, decimal price, DateOnly from)
    {
        var plan = await server.SaveTariffPlanAsync(admin, new SaveTariffPlanRequest(
            "VP" + Letters(8).ToUpperInvariant(), $"Company plan {Letters(6)}", TariffType.Corporate, null, null, 0m, false, true, null, null, null));
        await server.SaveTariffPricesAsync(admin, plan.Id, new SaveTariffPriceRequest(serviceId, price, from, null));
        var payer = await server.CreatePayerAsync(admin, new SavePayerRequest(
            "CO" + Letters(8).ToUpperInvariant(), $"Godavari Mills {Letters(5).ToUpperInvariant()}", PayerCategory.Corporate, "Meena Iyer", "9876543210",
            null, null, null, plan.Id, CreditDays: 30, DefaultCoPayPercent: 0m, PortalUrl: null, IsActive: true, Notes: null, Version: null));
        return payer.Id;
    }

    /// <summary>A health check of a new patient on <paramref name="payerId"/>, with one charge of the service; policies verified with the payers named.</summary>
    private async Task<(Guid VisitId, Guid ChargeId)> VisitAsync(Guid payerId, PayerType payerType, Guid serviceId, Guid[] policiesWith)
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var billing = await server.SignInAsync(Users.Billing);
        var patientId = (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Arun"), MiddleName: null, LastName: "Payer" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1972, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false))).Id;
        await server.VerifiedPolicyAsync(patientId, payerId);
        foreach (var other in policiesWith)
        {
            await server.VerifiedPolicyAsync(patientId, other);
        }

        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.HealthCheckup, null, null, null, payerType, payerId, ReferralSource.Corporate,
            null, null, false, null, null, TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
        var charge = await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(patientId, visit.Id, null, serviceId, 1m, null, null, null, null, null));
        return (visit.Id, charge.Id);
    }

    private async Task<decimal> NetAsync(Guid chargeId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Charge>().AsNoTracking()
            .Where(c => c.Id == chargeId).Select(c => c.NetAmount).SingleAsync(Ct);
    }

    private async Task<AuditEvent> AuditAsync(Guid visitId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.Action == "billing.visit.payer_changed" && a.EntityId == visitId.ToString()).OrderByDescending(a => a.Id).FirstAsync(Ct);
    }

    private static async Task<ApprovalRequestDto> AskedAsync(ApiClient api, Guid visitId, ChangeVisitPayerRequest body)
    {
        var path = PayerPath(visitId);
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, path);
    }

    private static async Task ApproveAsync(ApiClient approver, Guid approvalId)
    {
        var request = await approver.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        await approver.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}/approve", new ApproveRequest("Checked the company letter.", request.Version));
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, Guid visitId, ChangeVisitPayerRequest body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, PayerPath(visitId), body);
        response.IsSuccessStatusCode.ShouldBeFalse();
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (response.StatusCode, problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private static string Letters(int count) => new([.. Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26)))]);
}
