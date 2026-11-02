using System.Net;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A price changes only when two people agree (maker-checker), over the real API and database. A self-pay patient pays the
/// service's own price and GST, so a change to either, to whether the counter may type another price, to whether it may
/// be discounted, or putting it in use, waits for someone who may approve prices; what only narrows the terms applies at
/// once. A new service is out of use until its price is approved, and an approval never overwrites a change made after it
/// was asked for. Billing's own quote is the witness: it charges the terms in use until the new ones are approved.
/// </summary>
public sealed class ServicePriceApprovalTests(HospitalServer server)
{
    private const string ServicesPath = "/api/v1/masters/services";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_service_goes_live_at_the_price_a_second_person_approves()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var created = await hospitalAdmin.PostAsync<Created>(ServicesPath, New("Wound dressing", 450m) with { Reason = "New dressing room." });
        var approval = created.ApprovalId.ShouldNotBeNull("a new service's price is asked for");

        var waiting = await hospitalAdmin.GetAsync<ServiceItemDto>($"{ServicesPath}/{created.Id}");
        (waiting.IsActive, waiting.BasePrice).ShouldBe((false, 0m), "out of use, at no price, until it is approved");
        var pending = waiting.PendingPrice.ShouldNotBeNull();
        (pending.ApprovalId, pending.Asked).ShouldBe((approval, new ServicePriceTerms(450m, 0m, false, true, true)));
        (await QuoteAsync(hospitalAdmin, created.Id)).UnitPrice.ShouldBe(0m, "billing quotes the service's price as it stands");

        // Not the one who asked, nor someone who may not approve prices.
        var request = await hospitalAdmin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval}");
        (request.Kind, request.CanDecide).ShouldBe((ServicePrices.Kind, false));
        using (var own = await hospitalAdmin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approval}/approve", new ApproveRequest(null, request.Version)))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        using (var notPermitted = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approval}/approve", new ApproveRequest(null, request.Version)))
        {
            notPermitted.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        (await admin.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval}/approve", new ApproveRequest("Dressing rate agreed.", request.Version)))
            .Status.ShouldBe(ApprovalStatus.Approved);
        var live = await hospitalAdmin.GetAsync<ServiceItemDto>($"{ServicesPath}/{created.Id}");
        (live.IsActive, live.BasePrice, live.IsDiscountAllowed, live.PendingPrice).ShouldBe((true, 450m, true, (PendingServicePrice?)null));
        (await QuoteAsync(hospitalAdmin, created.Id)).UnitPrice.ShouldBe(450m);
        (await AuditCountAsync("service.price_changed", created.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_new_price_waits_while_what_only_narrows_the_terms_is_made_at_once()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var service = await server.CreateServiceAsync(admin, New("Plaster cast", 300m) with { IsPriceEditable = true });
        (service.BasePrice, service.IsDiscountAllowed, service.IsPriceEditable).ShouldBe((300m, true, true));

        // Dearer with 5% GST, no discounts, and no price typed at the counter: the price and GST wait, the rest is done at
        // once.
        using (var changed = await admin.SendRawAsync(HttpMethod.Put, $"{ServicesPath}/{service.Id}",
                   Edit(service, 350m) with { TaxRate = 5m, IsDiscountAllowed = false, IsPriceEditable = false }))
        {
            changed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var waiting = await admin.GetAsync<ServiceItemDto>($"{ServicesPath}/{service.Id}");
        (waiting.BasePrice, waiting.TaxRate, waiting.IsDiscountAllowed, waiting.IsPriceEditable).ShouldBe((300m, 0m, false, false));
        waiting.PendingPrice.ShouldNotBeNull().Asked.ShouldBe(new ServicePriceTerms(350m, 5m, false, false, true));
        (await QuoteAsync(admin, service.Id)).ShouldBe(new Quote(300m, 0m, false, false), "billing charges the terms in use, narrowed at once");

        // Saved again as the form shows it, nothing is asked for twice.
        var asWaiting = Edit(waiting, 350m) with { TaxRate = 5m, IsDiscountAllowed = false, IsPriceEditable = false };
        using (var again = await admin.SendRawAsync(HttpMethod.Put, $"{ServicesPath}/{service.Id}", asWaiting))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Any other save while it waits is refused: another price, and the price in use put back, which would otherwise be
        // saved while ₹350 still waits to be approved over it.
        var still = await admin.GetAsync<ServiceItemDto>($"{ServicesPath}/{service.Id}");
        foreach (var save in new[] { asWaiting with { BasePrice = 375m, Version = still.Version }, Edit(still, 300m) })
        {
            using var refused = await admin.SendRawAsync(HttpMethod.Put, $"{ServicesPath}/{service.Id}", save);
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain(ServicePrices.WaitingCode);
        }

        await server.ApprovePriceAsync(waiting.PendingPrice.ApprovalId);
        var priced = await admin.GetAsync<ServiceItemDto>($"{ServicesPath}/{service.Id}");
        (priced.BasePrice, priced.TaxRate, priced.IsDiscountAllowed, priced.IsPriceEditable).ShouldBe((350m, 5m, false, false));
        (await QuoteAsync(admin, service.Id)).ShouldBe(new Quote(350m, 5m, false, false));
    }

    [Fact]
    public async Task Taking_a_service_out_of_use_is_at_once_and_a_price_asked_before_it_is_not_approved_over_it()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var service = await server.CreateServiceAsync(admin, New("Nebulisation", 120m));
        using (var asked = await admin.SendRawAsync(HttpMethod.Put, $"{ServicesPath}/{service.Id}", Edit(service, 150m)))
        {
            asked.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        // Taken out of use while the new price waits: done at once, not refused as a second request.
        var waiting = await admin.GetAsync<ServiceItemDto>($"{ServicesPath}/{service.Id}");
        using (var retired = await admin.SendRawAsync(HttpMethod.Put, $"{ServicesPath}/{service.Id}", Edit(waiting, 150m) with { IsActive = false }))
        {
            retired.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await admin.GetAsync<ServiceItemDto>($"{ServicesPath}/{service.Id}")).IsActive.ShouldBeFalse();

        // Approving the price asked before would put it back in use: refused, and nothing is set.
        var approval = waiting.PendingPrice.ShouldNotBeNull().ApprovalId;
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var request = await hospitalAdmin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval}");
        using (var approved = await hospitalAdmin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approval}/approve", new ApproveRequest(null, request.Version)))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await approved.Content.ReadAsStringAsync(Ct)).ShouldContain(ServicePrices.ChangedCode);
        }

        (await hospitalAdmin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval}")).Status.ShouldBe(ApprovalStatus.Failed);
        var after = await admin.GetAsync<ServiceItemDto>($"{ServicesPath}/{service.Id}");
        (after.IsActive, after.BasePrice).ShouldBe((false, 120m));

        // Putting it back in use is asked for, like a new service.
        using (var back = await admin.SendRawAsync(HttpMethod.Put, $"{ServicesPath}/{service.Id}", Edit(after, 120m) with { IsActive = true }))
        {
            back.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        (await admin.GetAsync<ServiceItemDto>($"{ServicesPath}/{service.Id}")).IsActive.ShouldBeFalse("until someone approves it");
    }

    private static SaveServiceItemRequest New(string name, decimal price) => new(
        "SV-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), $"{name} (price test)", null, ServiceCategory.Procedure, null, price, 0m, "999312",
        IsDiscountAllowed: true, IsPriceEditable: false, Description: null, IsActive: true, Version: null);

    private static SaveServiceItemRequest Edit(ServiceItemDto s, decimal price) => new(
        s.Code, s.Name, s.ShortName, s.Category, s.DepartmentId, price, s.TaxRate, s.SacCode, s.IsDiscountAllowed, s.IsPriceEditable, s.Description,
        s.IsActive, s.Version, s.LocalNames.ToDictionary());

    /// <summary>What billing quotes a self-pay patient for the service today.</summary>
    private static Task<Quote> QuoteAsync(ApiClient client, Guid serviceId) =>
        client.GetAsync<Quote>($"/api/v1/billing/tariffs/quote?serviceItemId={serviceId}");

    private async Task<int> AuditCountAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = entityId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().CountAsync(e => e.Action == action && e.EntityId == id, Ct);
    }

    private sealed record Created(Guid Id, Guid? ApprovalId);

    private sealed record Quote(decimal UnitPrice, decimal TaxRate, bool IsPriceEditable, bool IsDiscountAllowed);
}
