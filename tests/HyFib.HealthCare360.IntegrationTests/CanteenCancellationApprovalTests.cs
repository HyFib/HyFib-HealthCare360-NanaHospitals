using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Features.Canteen;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A canteen bill cancelled after it was paid waits for a second person (maker-checker, Support services), over the real
/// API: the cancellation is asked for (202) and nothing moves until someone who approves canteen cancellations, never the
/// cashier who sold it nor the one who asked, approves it. The money then goes back the way it was paid (a card bill to
/// the card, never from the till), the stock comes back, and both names are kept.
/// </summary>
public sealed class CanteenCancellationApprovalTests(HospitalServer server)
{
    private const string Approvals = "/api/v1/approvals";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_card_bill_cancelled_waits_for_a_second_person_and_goes_back_to_the_card_with_the_stock()
    {
        var canteen = await server.SignInAsync(HospitalServer.Users.CanteenManager);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var item = await StockedItemAsync(canteen, 60m, stock: 10);
        var bill = await canteen.PostAsync<CanteenSaleDto>("/api/v1/canteen/sales", new CreateSaleRequest(
            CanteenCustomerType.Visitor, "Ramesh", null, null, null, CanteenPaymentMode.Card, "RRN778899", [new SaleLineInput(item.Id, 2)]));
        (await StockAsync(canteen, item.Id)).ShouldBe(8);

        // The cashier asks: the request comes back (202), saying how the money goes back; nothing has moved.
        using var response = await canteen.SendRawAsync(HttpMethod.Post, Cancel(bill.Id), new CancelSaleRequest("Billed twice by mistake"));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, Cancel(bill.Id));
        asked.ShouldSatisfyAllConditions(
            a => a.Kind.ShouldBe("canteen.sale_cancel"),
            a => Detail(a, "Paid back").ShouldBe($"₹{bill.NetAmount:N2} back to the card (reference RRN778899), not from the till"),
            a => Detail(a, "Bill").ShouldStartWith(bill.BillNumber));
        var waiting = await canteen.GetAsync<CanteenSaleDto>($"/api/v1/canteen/sales/{bill.Id}");
        waiting.ShouldSatisfyAllConditions(
            s => s.Status.ShouldBe(CanteenSaleStatus.Completed),
            s => s.OwnSale.ShouldBeTrue(),
            s => s.PendingCancellation.ShouldNotBeNull().AskedByYou.ShouldBeTrue());
        (await StockAsync(canteen, item.Id)).ShouldBe(8);

        // The cashier who asked does not approve it.
        (await RefusalAsync(canteen, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));

        // The accountant approves: the card is paid back under its reference, the stock is back, and both names are kept.
        var approved = await MakerChecker.ApproveAsync(accountant, asked);
        approved.Outcome.ShouldNotBeNull().ShouldContain("back to the card");
        var cancelled = await canteen.GetAsync<CanteenSaleDto>($"/api/v1/canteen/sales/{bill.Id}");
        cancelled.ShouldSatisfyAllConditions(
            s => s.Status.ShouldBe(CanteenSaleStatus.Cancelled),
            s => (s.RefundMode, s.RefundAmount).ShouldBe((CanteenPaymentMode.Card, bill.NetAmount)),
            s => s.CancelRequestedByName.ShouldBe(asked.RequestedByName),
            s => s.CancelledByName.ShouldBe(approved.DecidedByName),
            s => s.PendingCancellation.ShouldBeNull());
        (await StockAsync(canteen, item.Id)).ShouldBe(10);
        var audited = await AuditAsync("canteen.sale_cancelled", bill.Id);
        audited.Summary.ShouldNotBeNull().ShouldContain($"Asked for by {asked.RequestedByName}, approved by {approved.DecidedByName}");

        // The day's report leaves the cancelled bill out of the takings.
        var day = await accountant.GetAsync<DailySalesSummary>("/api/v1/canteen/reports/daily");
        day.CancelledBills.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task The_cashier_who_sold_a_bill_does_not_approve_its_cancellation_whoever_asked()
    {
        var canteen = await server.SignInAsync(HospitalServer.Users.CanteenManager);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var item = await StockedItemAsync(canteen, 40m, stock: 0);
        var bill = await canteen.PostAsync<CanteenSaleDto>("/api/v1/canteen/sales", new CreateSaleRequest(
            CanteenCustomerType.Attendant, null, null, null, null, CanteenPaymentMode.Cash, null, [new SaleLineInput(item.Id, 1)]));

        // The hospital administrator asks; the canteen manager, who may approve canteen cancellations, sold this bill.
        var asked = await admin.PostAsync<ApprovalRequestDto>(Cancel(bill.Id), new CancelSaleRequest("Customer changed their mind"));
        (await RefusalAsync(canteen, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "canteen.cancel_own_sale"));
        (await accountant.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}")).Status.ShouldBe(ApprovalStatus.Pending);
        (await canteen.GetAsync<CanteenSaleDto>($"/api/v1/canteen/sales/{bill.Id}")).Status.ShouldBe(CanteenSaleStatus.Completed);

        // Someone else approves: the cash goes back from the till.
        await MakerChecker.ApproveAsync(accountant, await accountant.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}"));
        var cancelled = await canteen.GetAsync<CanteenSaleDto>($"/api/v1/canteen/sales/{bill.Id}");
        (cancelled.Status, cancelled.RefundMode).ShouldBe((CanteenSaleStatus.Cancelled, CanteenPaymentMode.Cash));

        // A cancelled bill is not asked for again.
        (await RefusalAsync(canteen, Cancel(bill.Id), new CancelSaleRequest("Asked again by mistake"))).ShouldBe((HttpStatusCode.UnprocessableEntity, "canteen.sale_cancelled"));
    }

    [Fact]
    public async Task A_cancellation_approved_after_the_bill_changed_changes_nothing()
    {
        var canteen = await server.SignInAsync(HospitalServer.Users.CanteenManager);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var item = await StockedItemAsync(canteen, 25m, stock: 0);
        var bill = await canteen.PostAsync<CanteenSaleDto>("/api/v1/canteen/sales", new CreateSaleRequest(
            CanteenCustomerType.Visitor, null, null, null, null, CanteenPaymentMode.Upi, "UPI123456", [new SaleLineInput(item.Id, 1)]));
        var asked = await canteen.PostAsync<ApprovalRequestDto>(Cancel(bill.Id), new CancelSaleRequest("Wrong item billed"));

        // The bill is touched after the request (its reference corrected): the approval fails as stale and nothing moves.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await db.Set<CanteenSale>().IgnoreQueryFilters().Where(s => s.Id == bill.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.PaymentReference, "UPI654321"), Ct);
        }

        (await RefusalAsync(accountant, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "approval.stale"));
        (await canteen.GetAsync<CanteenSaleDto>($"/api/v1/canteen/sales/{bill.Id}")).Status.ShouldBe(CanteenSaleStatus.Completed);
    }

    private static string Cancel(Guid id) => $"/api/v1/canteen/sales/{id}/cancel";

    private static string Detail(ApprovalRequestDto request, string label) => request.Details.Single(d => d.Label == label).Value;

    private static async Task<MenuItemDto> StockedItemAsync(ApiClient canteen, decimal price, int stock)
    {
        var item = await canteen.PostAsync<MenuItemDto>("/api/v1/canteen/menu", new SaveMenuItemRequest(
            $"CX{Random.Shared.Next(10_000_000, 99_999_999)}", $"Lemon rice {Random.Shared.Next(1000, 9999)}", MenuCategory.Meals, price, 5m, null, true, true, true,
            stock > 0, 0, null, null));
        if (stock > 0)
        {
            item = await canteen.PostAsync<MenuItemDto>($"/api/v1/canteen/menu/{item.Id}/stock", new StockMovementRequest(StockMovementKind.Receipt, stock, "Morning delivery"));
        }

        return item;
    }

    private static async Task<int> StockAsync(ApiClient canteen, Guid itemId) =>
        (await canteen.GetAsync<List<MenuItemDto>>("/api/v1/canteen/menu")).Single(m => m.Id == itemId).StockOnHand;

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (response.StatusCode, problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private async Task<AuditEvent> AuditAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .SingleAsync(a => a.Action == action && a.EntityId == entityId.ToString(), Ct);
    }
}
