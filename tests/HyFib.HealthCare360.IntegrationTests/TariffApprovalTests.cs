using System.Net;
using System.Net.Http.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A tariff plan's prices and terms change only when two people agree (maker-checker), over the real API and database:
/// the billing desk asks, someone else who may approve prices (the accountant) decides. A plan's patients move to another
/// plan's prices when it is taken out of use, so for a plan every change waits. Billing's own quote is the witness: it
/// charges the prices and terms in use until the new ones are approved, and an approval never overwrites a change made
/// after it was asked for.
/// </summary>
public sealed class TariffApprovalTests(HospitalServer server)
{
    private const string TariffsPath = "/api/v1/billing/tariffs";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_plan_is_out_of_use_until_a_second_person_approves_its_terms()
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var created = await billing.PostAsync<TariffPlanDetail>(TariffsPath, Plan(10m, isActive: true) with { Reason = "Corporate contract signed." });
        (created.IsActive, created.DefaultDiscountPercent).ShouldBe((false, 0m), "made out of use, without a discount, until approved");
        var pending = created.PendingTerms.ShouldNotBeNull();
        (pending.Asked.IsActive, pending.Asked.DefaultDiscountPercent).ShouldBe((true, 10m));

        // Not the one who asked; the accountant, who may approve prices, decides.
        var request = await billing.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{pending.ApprovalId}");
        (request.Kind, request.CanDecide).ShouldBe((TariffApprovals.TermsKind, false));
        using (var own = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{pending.ApprovalId}/approve", new ApproveRequest(null, request.Version)))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        await ApproveAsAccountantAsync(pending.ApprovalId);
        var live = await billing.GetAsync<TariffPlanDetail>($"{TariffsPath}/{created.Id}");
        (live.IsActive, live.DefaultDiscountPercent, live.PendingTerms).ShouldBe((true, 10m, (PendingTariffTerms?)null));
        (await AuditCountAsync("tariff.plan_changed", created.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_plan_price_waits_and_billing_quotes_the_price_in_use_until_it_is_approved()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var plan = await server.SaveTariffPlanAsync(admin, Plan(0m, isActive: true));
        var service = await ServiceAsync(admin, "Wound dressing", 400m);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

        var asked = await billing.PutAsync<ApprovalRequestDto>($"{TariffsPath}/{plan.Id}/prices", new SaveTariffPriceRequest(service, 500m, today, null) with { Reason = "Rate card 2027." });
        asked.Kind.ShouldBe(TariffApprovals.PricesKind);
        (await QuoteAsync(billing, service, plan.Id)).ShouldBe(400m, "the price in use: the service's own, as the plan has none yet");
        (await billing.GetAsync<TariffPlanDetail>($"{TariffsPath}/{plan.Id}")).PendingPrices.ShouldNotBeNull().Set.ShouldHaveSingleItem().Price.ShouldBe(500m);

        // Another price change while one waits is refused, the bulk import's included.
        using (var another = await billing.SendRawAsync(HttpMethod.Post, $"{TariffsPath}/{plan.Id}/prices/bulk",
                   new BulkTariffPricesRequest([new SaveTariffPriceRequest(service, 450m, today, null)])))
        {
            another.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await another.Content.ReadAsStringAsync(Ct)).ShouldContain("approval.already_requested");
        }

        await ApproveAsAccountantAsync(asked.Id);
        (await QuoteAsync(billing, service, plan.Id)).ShouldBe(500m);
        (await AuditCountAsync("tariff.prices_changed", plan.Id)).ShouldBe(1);

        // Removing it waits too: until approved, the plan's price is charged.
        var row = (await billing.GetAsync<PagedResult<TariffPriceDto>>($"{TariffsPath}/{plan.Id}/prices")).Items.ShouldHaveSingleItem();
        Guid removal;
        using (var removing = await billing.SendRawAsync(HttpMethod.Delete, $"{TariffsPath}/{plan.Id}/prices/{row.Id}"))
        {
            removing.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            removal = (await removing.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!.Id;
        }

        (await QuoteAsync(billing, service, plan.Id)).ShouldBe(500m);
        await ApproveAsAccountantAsync(removal);
        (await QuoteAsync(billing, service, plan.Id)).ShouldBe(400m, "back to the service's own price");
    }

    [Fact]
    public async Task A_bulk_revision_is_one_request_and_is_not_approved_over_a_price_changed_after_it_was_asked_for()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var plan = await server.SaveTariffPlanAsync(admin, Plan(0m, isActive: true));
        var dressing = await ServiceAsync(admin, "Dressing", 300m);
        var suturing = await ServiceAsync(admin, "Suturing", 1_000m);
        var splint = await ServiceAsync(admin, "Splint", 800m);
        var from = new DateOnly(2031, 1, 1);
        await server.SaveTariffPricesAsync(admin, plan.Id, new SaveTariffPriceRequest(dressing, 300m, from, null), new SaveTariffPriceRequest(suturing, 1_000m, from, null));

        // Ten per cent dearer from April, and the splint new on the plan: one request, summed up for the approver.
        var april = new DateOnly(2031, 4, 1);
        var revision = await billing.PostAsync<ApprovalRequestDto>($"{TariffsPath}/{plan.Id}/prices/bulk", new BulkTariffPricesRequest(
            [new SaveTariffPriceRequest(dressing, 330m, april, null), new SaveTariffPriceRequest(suturing, 1_100m, april, null), new SaveTariffPriceRequest(splint, 750m, april, null)],
            "Annual revision."));
        revision.Details.ShouldContain(d => d.Label == "Revision" && d.Value == "2 dearer, 0 cheaper, 1 new; largest change +10%");

        // The suturing price changed after it was asked for (by any other way in): approving it is refused, nothing is set.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var row = await db.Set<TariffPrice>().SingleAsync(p => p.TariffPlanId == plan.Id && p.ServiceItemId == suturing, Ct);
            row.Price = 1_050m;
            await db.SaveChangesAsync(Ct);
        }

        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var request = await accountant.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{revision.Id}");
        using (var refused = await accountant.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{revision.Id}/approve", new ApproveRequest(null, request.Version)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain(TariffApprovals.ChangedCode);
        }

        (await QuoteAsync(billing, dressing, plan.Id, april)).ShouldBe(300m, "nothing of the revision was set");
        (await QuoteAsync(billing, splint, plan.Id, april)).ShouldBe(800m);
        (await accountant.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{revision.Id}")).Status.ShouldBe(ApprovalStatus.Failed);
    }

    [Fact]
    public async Task A_plans_terms_wait_and_are_not_approved_over_terms_changed_after_they_were_asked_for()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var plan = await server.SaveTariffPlanAsync(admin, Plan(5m, isActive: true));

        // A larger concession waits; the name is saved at once.
        using (var asked = await billing.SendRawAsync(HttpMethod.Put, $"{TariffsPath}/{plan.Id}", Plan(15m, isActive: true, plan.Code, plan.Version) with { Name = "Renamed plan" }))
        {
            asked.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var waiting = await billing.GetAsync<TariffPlanDetail>($"{TariffsPath}/{plan.Id}");
        (waiting.Name, waiting.DefaultDiscountPercent).ShouldBe(("Renamed plan", 5m));
        var pending = waiting.PendingTerms.ShouldNotBeNull();

        // Taken out of use while it waits: refused, as for a plan nothing only narrows; saved as asked, nothing is asked twice.
        using (var other = await billing.SendRawAsync(HttpMethod.Put, $"{TariffsPath}/{plan.Id}", Plan(15m, isActive: false, plan.Code, waiting.Version) with { Name = waiting.Name }))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await billing.PutAsync<TariffPlanDetail>($"{TariffsPath}/{plan.Id}", Plan(15m, isActive: true, plan.Code, waiting.Version) with { Name = waiting.Name }))
            .PendingTerms.ShouldNotBeNull().ApprovalId.ShouldBe(pending.ApprovalId);

        // The concession changed by any other way in after it was asked for: approving is refused, and it stays as it is.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<TariffPlan>().SingleAsync(p => p.Id == plan.Id, Ct)).DefaultDiscountPercent = 12m;
            await db.SaveChangesAsync(Ct);
        }

        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var request = await accountant.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{pending.ApprovalId}");
        using (var refused = await accountant.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{pending.ApprovalId}/approve", new ApproveRequest(null, request.Version)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain(TariffApprovals.ChangedCode);
        }

        (await billing.GetAsync<TariffPlanDetail>($"{TariffsPath}/{plan.Id}")).DefaultDiscountPercent.ShouldBe(12m);
    }

    private static SaveTariffPlanRequest Plan(decimal discount, bool isActive, string? code = null, uint? version = null) => new(
        code ?? "TA" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), "Approval test plan", TariffType.Corporate, null, null, discount, false, isActive, null, null, version);

    private async Task<Guid> ServiceAsync(ApiClient admin, string name, decimal price) =>
        (await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            "SV-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), $"{name} (tariff test)", null, ServiceCategory.Procedure, null, price, 0m, "999312",
            IsDiscountAllowed: true, IsPriceEditable: false, Description: null, IsActive: true, Version: null))).Id;

    private async Task ApproveAsAccountantAsync(Guid approvalId)
    {
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var request = await accountant.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        request.CanDecide.ShouldBeTrue();
        (await accountant.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}/approve", new ApproveRequest("Agreed.", request.Version)))
            .Status.ShouldBe(ApprovalStatus.Approved);
    }

    /// <summary>What billing quotes for the service on the plan, on a date (today by default).</summary>
    private static async Task<decimal> QuoteAsync(ApiClient client, Guid serviceId, Guid planId, DateOnly? on = null) =>
        (await client.GetAsync<Quote>($"{TariffsPath}/quote?serviceItemId={serviceId}&tariffPlanId={planId}{(on is { } date ? $"&date={date:yyyy-MM-dd}" : string.Empty)}")).UnitPrice;

    private async Task<int> AuditCountAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = entityId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().CountAsync(e => e.Action == action && e.EntityId == id, Ct);
    }

    private sealed record Quote(decimal UnitPrice);
}
