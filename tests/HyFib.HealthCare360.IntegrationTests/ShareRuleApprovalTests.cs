using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.DoctorFees;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A doctor's share of what is billed changes only when two people agree (maker-checker, DFA-01), through the real API:
/// the accountant, who keeps the sharing rules, asks; the hospital administrator, who approves prices, decides. The
/// doctor's own bills are the witness: a bill finalized while a change waits accrues at the rule in use, and one finalized
/// after the approval at the rule approved. The seed's hospital default for procedures is 30 %. The doctor is the test's own.
/// </summary>
public sealed class ShareRuleApprovalTests(HospitalServer server)
{
    private const string Rules = "/api/v1/doctor-fees/rules";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_rule_shares_nothing_until_a_second_person_approves_it()
    {
        var (admin, billing, accountant, fees) = await PeopleAsync();
        var today = server.Today;
        var doctor = await DoctorFeeStatementTests.DoctorAsync(admin);
        var debridement = await DoctorFeeStatementTests.ServiceAsync(server, admin, "Wound debridement", 2000m);

        // The accountant asks for the doctor's own rule, 20 % of procedures, from a month ago, as the doctor's contract says.
        var from = today.AddDays(-30);
        using var created = await accountant.SendRawAsync(HttpMethod.Post, Rules, new SaveShareRuleRequest(
            doctor.Id, null, ServiceCategory.Procedure, null, ShareMethod.Percentage, 20m, from, null, true, "Agreed in the contract", null, "Contract signed on the 1st"));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var saved = (await created.Content.ReadFromJsonAsync<SavedShareRule>(ApiClient.Json, Ct))!;
        var asked = saved.Asked.ShouldNotBeNull();
        (asked.Kind, asked.Reason).ShouldBe((ShareRuleApprovals.Kind, "Contract signed on the 1st"));
        asked.Details.ShouldContain(d => d.Label == "Doctor" && d.Value == doctor.Name);
        asked.Details.ShouldContain(d => d.Label == "Applies to" && d.Value == "every procedure service");
        asked.Details.ShouldContain(d => d.Label == "Share" && d.Value == "20% of the billed value");
        asked.Details.ShouldContain(d => d.Label == "Backdated" && d.Value.StartsWith($"In force from {from.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}, 30 day(s) before today", StringComparison.Ordinal));
        asked.Details.ShouldContain(d => d.Label == "Last 90 days" && d.Value == "No bill line of the last 90 days would be shared differently");

        // Listed out of use, with its terms waiting, even where rules out of use are not; the notes are saved at once.
        var listed = (await accountant.GetAsync<List<ShareRuleDto>>($"{Rules}?practitionerId={doctor.Id}")).Single(r => r.Id == saved.Id);
        (listed.IsActive, listed.Value, listed.Notes, listed.Pending?.Asked.Value, listed.Pending?.Asked.IsActive).ShouldBe((false, 20m, "Agreed in the contract", 20m, true));

        // A bill finalized meanwhile is shared at the hospital's default: the new rule shares nothing yet.
        var first = await fees.BillAsync(billing, (debridement, 1m, doctor.Id));
        (await ShareAsync(accountant, doctor.Id, today, first)).ShouldBe((30m, 600m));

        // The accountant cannot approve their own request; the hospital administrator does.
        using (var own = await ApproveAsync(accountant, asked.Id))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await own.Content.ReadAsStringAsync(Ct)).ShouldContain("approval.self");
        }

        using (var approved = await ApproveAsync(admin, asked.Id))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        }

        (await RuleAsync(accountant, doctor.Id, saved.Id)).ShouldSatisfyAllConditions(
            r => r.IsActive.ShouldBeTrue(),
            r => r.Pending.ShouldBeNull());
        var second = await fees.BillAsync(billing, (debridement, 1m, doctor.Id));
        (await ShareAsync(accountant, doctor.Id, today, second)).ShouldBe((20m, 400m));
        (await AuditCountAsync("doctorfees.rule_changed", saved.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_new_share_waits_and_the_approver_is_told_what_it_would_have_changed_on_the_last_90_days_bills()
    {
        var (admin, billing, accountant, fees) = await PeopleAsync();
        var today = server.Today;
        var doctor = await DoctorFeeStatementTests.DoctorAsync(admin);
        var debridement = await DoctorFeeStatementTests.ServiceAsync(server, admin, "Wound debridement", 2000m);
        var ruleId = await DoctorFeeStatementTests.RuleAsync(server, accountant, doctor.Id, null, ServiceCategory.Procedure, ShareMethod.Percentage, 20m, today);
        await fees.BillAsync(billing, (debridement, 1m, doctor.Id));
        await fees.BillAsync(billing, (debridement, 1m, doctor.Id));

        // The accountant asks for 25 %: the approver is told it would have shared the doctor's two bill lines ₹200 more.
        var rule = await RuleAsync(accountant, doctor.Id, ruleId);
        using var raise = await accountant.SendRawAsync(HttpMethod.Put, $"{Rules}/{ruleId}", Request(rule, value: 25m));
        raise.StatusCode.ShouldBe(HttpStatusCode.Accepted, await raise.Content.ReadAsStringAsync(Ct));
        var asked = (await raise.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
        asked.Details.ShouldContain(d => d.Label == "Share" && d.Value == "20% of the billed value → 25% of the billed value");
        asked.Details.ShouldContain(d => d.Label == "Last 90 days" && d.Value == "2 bill line(s) would be shared differently: ₹800.00 → ₹1,000.00 (+₹200.00)");
        asked.Details.ShouldNotContain(d => d.Label == "Backdated");

        // Another change meanwhile is refused; the same change with new notes saves the notes and asks nothing twice.
        using (var other = await accountant.SendRawAsync(HttpMethod.Put, $"{Rules}/{ruleId}", Request(rule, value: 30m)))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using (var notes = await accountant.SendRawAsync(HttpMethod.Put, $"{Rules}/{ruleId}", Request(rule, value: 25m, notes: "Revised contract")))
        {
            notes.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        rule = await RuleAsync(accountant, doctor.Id, ruleId);
        (rule.Value, rule.Notes, rule.Pending?.ApprovalId).ShouldBe((20m, "Revised contract", asked.Id));

        // A bill finalized meanwhile is shared at 20 %; approved, the next at 25 %.
        var waiting = await fees.BillAsync(billing, (debridement, 1m, doctor.Id));
        (await ShareAsync(accountant, doctor.Id, today, waiting)).ShouldBe((20m, 400m));
        using (var approved = await ApproveAsync(admin, asked.Id))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        }

        var after = await fees.BillAsync(billing, (debridement, 1m, doctor.Id));
        (await ShareAsync(accountant, doctor.Id, today, after)).ShouldBe((25m, 500m));
    }

    [Fact]
    public async Task Taking_a_doctors_own_rule_out_of_use_waits_because_their_lines_fall_back_to_the_hospitals_default()
    {
        var (admin, billing, accountant, fees) = await PeopleAsync();
        var today = server.Today;
        var doctor = await DoctorFeeStatementTests.DoctorAsync(admin);
        var debridement = await DoctorFeeStatementTests.ServiceAsync(server, admin, "Wound debridement", 2000m);
        var ruleId = await DoctorFeeStatementTests.RuleAsync(server, accountant, doctor.Id, null, ServiceCategory.Procedure, ShareMethod.Percentage, 20m, today);
        await fees.BillAsync(billing, (debridement, 1m, doctor.Id));

        // Out of use, the doctor's procedures would be shared at the hospital's 30 %: that waits, and the approver is told so.
        var rule = await RuleAsync(accountant, doctor.Id, ruleId);
        using var retire = await accountant.SendRawAsync(HttpMethod.Put, $"{Rules}/{ruleId}", Request(rule, active: false));
        retire.StatusCode.ShouldBe(HttpStatusCode.Accepted, await retire.Content.ReadAsStringAsync(Ct));
        var asked = (await retire.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
        asked.Details.ShouldContain(d => d.Label == "In use" && d.Value.StartsWith("Taken out of use", StringComparison.Ordinal));
        asked.Details.ShouldContain(d => d.Label == "Last 90 days" && d.Value == "1 bill line(s) would be shared differently: ₹400.00 → ₹600.00 (+₹200.00)");

        // A rule in use is not deleted; meanwhile the doctor's bills are shared at 20 %.
        using (var delete = await accountant.SendRawAsync(HttpMethod.Delete, $"{Rules}/{ruleId}"))
        {
            delete.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await delete.Content.ReadAsStringAsync(Ct)).ShouldContain("doctorfees.rule_in_use");
        }

        var waiting = await fees.BillAsync(billing, (debridement, 1m, doctor.Id));
        (await ShareAsync(accountant, doctor.Id, today, waiting)).ShouldBe((20m, 400m));

        // Approved, the doctor's procedures fall back to the default; the rule, out of use, is deleted at once.
        using (var approved = await ApproveAsync(admin, asked.Id))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.OK, await approved.Content.ReadAsStringAsync(Ct));
        }

        var after = await fees.BillAsync(billing, (debridement, 1m, doctor.Id));
        (await ShareAsync(accountant, doctor.Id, today, after)).ShouldBe((30m, 600m));
        using (var delete = await accountant.SendRawAsync(HttpMethod.Delete, $"{Rules}/{ruleId}"))
        {
            delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await accountant.GetAsync<List<ShareRuleDto>>($"{Rules}?practitionerId={doctor.Id}&includeInactive=true")).ShouldNotContain(r => r.Id == ruleId);
    }

    [Fact]
    public async Task An_approval_is_not_made_over_a_rule_changed_after_it_was_asked_for()
    {
        var (admin, _, accountant, fees) = await PeopleAsync();
        var today = server.Today;
        var doctor = await DoctorFeeStatementTests.DoctorAsync(admin);
        var ruleId = await DoctorFeeStatementTests.RuleAsync(server, accountant, doctor.Id, null, ServiceCategory.Procedure, ShareMethod.Percentage, 20m, today);
        var rule = await RuleAsync(accountant, doctor.Id, ruleId);
        var asked = await accountant.PutAsync<ApprovalRequestDto>($"{Rules}/{ruleId}", Request(rule, value: 25m));

        // The rule is changed another way after the request: the approval does not set the share asked over it.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<DoctorShareRule>().Where(r => r.Id == ruleId)
                .ExecuteUpdateAsync(r => r.SetProperty(x => x.Value, 22m), Ct)).ShouldBe(1);
        }

        using var approved = await ApproveAsync(admin, asked.Id);
        approved.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await approved.Content.ReadAsStringAsync(Ct)).ShouldContain(TariffApprovals.ChangedCode);
        (await RuleAsync(accountant, doctor.Id, ruleId)).Value.ShouldBe(22m);
    }

    private async Task<(ApiClient Admin, ApiClient Billing, ApiClient Accountant, DoctorFeeStatementTests Fees)> PeopleAsync() => (
        await server.SignInAsync(HospitalServer.Users.HospitalAdmin),
        await server.SignInAsync(HospitalServer.Users.Billing),
        await server.SignInAsync(HospitalServer.Users.Accountant),
        new DoctorFeeStatementTests(server));

    private static async Task<ShareRuleDto> RuleAsync(ApiClient client, Guid doctorId, Guid ruleId) =>
        (await client.GetAsync<List<ShareRuleDto>>($"{Rules}?practitionerId={doctorId}&includeInactive=true")).Single(r => r.Id == ruleId);

    private static SaveShareRuleRequest Request(ShareRuleDto rule, decimal? value = null, bool? active = null, string? notes = null) => new(
        rule.PractitionerId, rule.ServiceItemId, rule.Category, rule.PayerType, rule.Method, value ?? rule.Value, rule.EffectiveFrom, rule.EffectiveTo,
        active ?? rule.IsActive, notes ?? rule.Notes, rule.Version);

    private static async Task<HttpResponseMessage> ApproveAsync(ApiClient checker, Guid approvalId)
    {
        var request = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        return await checker.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approvalId}/approve", new ApproveRequest(null, request.Version));
    }

    /// <summary>The rate and share the doctor's fee on <paramref name="bill"/> accrued at.</summary>
    private static async Task<(decimal Rate, decimal Share)> ShareAsync(ApiClient accountant, Guid doctorId, DateOnly today, BillDetail bill)
    {
        var statement = await accountant.GetAsync<DoctorStatement>(DoctorFeeStatementTests.StatementPath(doctorId, today, today));
        var fee = statement.Accruals.Single(a => a.BillNumber == bill.BillNumber);
        return (fee.Rate, fee.ShareAmount);
    }

    private async Task<int> AuditCountAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = entityId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().CountAsync(e => e.Action == action && e.EntityId == id, Ct);
    }
}
