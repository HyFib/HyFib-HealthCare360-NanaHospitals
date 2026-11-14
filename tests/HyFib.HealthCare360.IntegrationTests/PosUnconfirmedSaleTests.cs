using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A card/UPI sale whose upload answer was lost (INT-POS), with the training terminal set to lose it: the sale may be on
/// the terminal and the customer may still tap, so it is kept as unconfirmed and no second sale goes to that terminal
/// until the terminal's window has passed. A cashier who sees the terminal shows no sale clears it, with a note that is
/// audited. After the window the re-check lapses it and keeps it as an exception for the accountants. Billing's
/// counter and the counters that bill their own sales (the canteen) behave the same.
/// </summary>
public sealed class PosUnconfirmedSaleTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_lost_upload_answer_is_kept_as_unconfirmed_and_a_second_push_to_the_terminal_is_refused()
    {
        await using var terminal = await TrainingTerminal.UseAsync(server, approveAfterSeconds: 60, loseUploadAnswer: true);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await TrainingTerminal.OpenCounterShiftAsync(billing);
        var bill = await TrainingTerminal.FinalizedBillAsync(server, billing);
        var terminalId = "UC" + Digits(8);

        var first = await billing.PostAsync<PosSaleDto>("/api/v1/billing/pos/sales", new StartPosSaleRequest(bill.Id, null, bill.DueAmount, terminalId, PaymentMode.Card));

        // Not declined: the sale is kept, with the generated id and when it can be tried again.
        first.Status.ShouldBe(PosTransactionStatus.Unconfirmed);
        first.TransactionId.ShouldStartWith("unconfirmed-");
        first.RetryAfter.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        (await SaleAsync(first.Id)).Status.ShouldBe(PosTransactionStatus.Unconfirmed);

        // Another push to the same terminal (whatever the case of its id) is refused, and nothing is saved for it.
        var refused = await RefusalAsync(billing, "/api/v1/billing/pos/sales", new StartPosSaleRequest(bill.Id, null, bill.DueAmount, terminalId.ToLowerInvariant(), PaymentMode.Upi));
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.Code.ShouldBe("pos.terminal_unconfirmed");
        refused.Detail.ShouldNotBeNull().ShouldContain("may still be on the terminal");
        (await SalesOnAsync(terminalId)).ShouldBe(1);

        // Refreshing it asks nothing of the terminal: there is no reference to ask about.
        (await billing.GetAsync<PosSaleDto>($"/api/v1/billing/pos/sales/{first.Id}")).Status.ShouldBe(PosTransactionStatus.Unconfirmed);

        // Another terminal is not affected.
        var other = await billing.PostAsync<PosSaleDto>("/api/v1/billing/pos/sales",
            new StartPosSaleRequest(bill.Id, null, bill.DueAmount, "UC" + Digits(8), PaymentMode.Card));
        other.Status.ShouldBe(PosTransactionStatus.Unconfirmed);
    }

    [Fact]
    public async Task The_cashier_who_sees_no_sale_on_the_terminal_clears_it_with_an_audited_note()
    {
        await using var terminal = await TrainingTerminal.UseAsync(server, approveAfterSeconds: 60, loseUploadAnswer: true);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await TrainingTerminal.OpenCounterShiftAsync(billing);
        var bill = await TrainingTerminal.FinalizedBillAsync(server, billing);
        var terminalId = "UC" + Digits(8);
        var first = await billing.PostAsync<PosSaleDto>("/api/v1/billing/pos/sales", new StartPosSaleRequest(bill.Id, null, bill.DueAmount, terminalId, PaymentMode.Card));

        // A note is required: the terminal is not freed on a bare click.
        (await RefusalAsync(billing, $"/api/v1/billing/pos/sales/{first.Id}/cancel", new CancelPosSaleRequest(null))).Status.ShouldNotBe(HttpStatusCode.OK);
        (await SaleAsync(first.Id)).Status.ShouldBe(PosTransactionStatus.Unconfirmed);

        var cleared = await billing.PostAsync<PosSaleDto>($"/api/v1/billing/pos/sales/{first.Id}/cancel", new CancelPosSaleRequest("Terminal screen shows the idle menu"));

        cleared.Status.ShouldBe(PosTransactionStatus.Cancelled);
        cleared.Message.ShouldNotBeNull().ShouldContain("idle menu");
        (await AuditCountAsync("billing.pos.unconfirmed_cleared", first.Id)).ShouldBe(1);
        (await ExceptionAsync(first.TransactionId)).ShouldBeNull("the cashier saw nothing was taken");

        // The terminal is free again: the next push is accepted (and, with the training terminal still losing answers, unconfirmed).
        var second = await billing.PostAsync<PosSaleDto>("/api/v1/billing/pos/sales", new StartPosSaleRequest(bill.Id, null, bill.DueAmount, terminalId, PaymentMode.Card));
        second.Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public async Task A_sale_that_lapsed_after_the_window_is_kept_as_an_exception_once_and_frees_the_terminal()
    {
        await using var terminal = await TrainingTerminal.UseAsync(server, approveAfterSeconds: 60, loseUploadAnswer: true);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await TrainingTerminal.OpenCounterShiftAsync(billing);
        var bill = await TrainingTerminal.FinalizedBillAsync(server, billing);
        var terminalId = "UC" + Digits(8);
        var first = await billing.PostAsync<PosSaleDto>("/api/v1/billing/pos/sales", new StartPosSaleRequest(bill.Id, null, bill.DueAmount, terminalId, PaymentMode.Card));
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        // Inside the window (5 minutes plus the margin) the job leaves it alone.
        await AgeAsync(first.Id, TimeSpan.FromMinutes(4));
        await RecheckAsync();
        (await SaleAsync(first.Id)).Status.ShouldBe(PosTransactionStatus.Unconfirmed);
        (await ExceptionAsync(first.TransactionId)).ShouldBeNull();

        await AgeAsync(first.Id, TimeSpan.FromMinutes(8));
        await RecheckAsync();

        var sale = await SaleAsync(first.Id);
        sale.Status.ShouldBe(PosTransactionStatus.Lapsed);
        sale.CompletedAt.ShouldNotBeNull();
        sale.Message.ShouldNotBeNull().ShouldContain("outcome is unknown");
        (await ExceptionAsync(first.TransactionId)).ShouldNotBeNull().ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(DiscrepancyStatus.Open),
            d => d.BillId.ShouldBe(bill.Id),
            d => d.GatewayAmount.ShouldBe(bill.DueAmount));
        var alerts = await AlertsAsync(since, terminalId);
        alerts.ShouldBe(1, "the accountants are told once");
        await RecheckAsync();
        (await AlertsAsync(since, terminalId)).ShouldBe(alerts);

        // The window has passed: the terminal takes a new push again.
        var next = await billing.PostAsync<PosSaleDto>("/api/v1/billing/pos/sales", new StartPosSaleRequest(bill.Id, null, bill.DueAmount, terminalId, PaymentMode.Card));
        next.Id.ShouldNotBe(first.Id);
    }

    [Fact]
    public async Task A_counter_that_bills_its_own_sales_is_refused_a_second_push_and_its_lapsed_sale_is_kept_like_billings()
    {
        await using var terminal = await TrainingTerminal.UseAsync(server, approveAfterSeconds: 60, loseUploadAnswer: true);
        var canteen = await server.SignInAsync(HospitalServer.Users.CanteenManager);
        var terminalId = "UC" + Digits(8);
        var first = await canteen.PostAsync<CounterTerminalSale>("/api/v1/canteen/terminal-sales",
            new StartTerminalSaleRequest(70m, terminalId, CounterPaymentMode.Upi, null));
        first.Status.ShouldBe(PosTransactionStatus.Unconfirmed);
        first.RetryAfter.ShouldNotBeNull();

        var refused = await RefusalAsync(canteen, "/api/v1/canteen/terminal-sales", new StartTerminalSaleRequest(70m, terminalId, CounterPaymentMode.Upi, null));
        refused.Status.ShouldBe(HttpStatusCode.UnprocessableEntity);
        refused.Code.ShouldBe("pos.terminal_unconfirmed");

        // The counter cannot put an unconfirmed sale on a bill.
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        await AgeAsync(first.Id, TimeSpan.FromMinutes(8));
        await RecheckAsync();
        (await SaleAsync(first.Id)).Status.ShouldBe(PosTransactionStatus.Lapsed);
        (await ExceptionAsync(first.TransactionId)).ShouldNotBeNull().Status.ShouldBe(DiscrepancyStatus.Open);
        (await AlertsAsync(since, terminalId)).ShouldBe(1);
        (await canteen.GetAsync<CounterTerminalSale>($"/api/v1/canteen/terminal-sales/{first.Id}")).Status.ShouldBe(PosTransactionStatus.Lapsed);
    }

    [Fact]
    public async Task A_sale_saved_before_the_push_whose_answer_never_arrived_is_unconfirmed_after_the_server_dropped_it()
    {
        await using var terminal = await TrainingTerminal.UseAsync(server, approveAfterSeconds: 60);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await TrainingTerminal.OpenCounterShiftAsync(billing);
        var bill = await TrainingTerminal.FinalizedBillAsync(server, billing);
        var terminalId = "UC" + Digits(8);
        var started = await billing.PostAsync<PosSaleDto>("/api/v1/billing/pos/sales", new StartPosSaleRequest(bill.Id, null, bill.DueAmount, terminalId, PaymentMode.Card));

        // As a crash between the save and the answer leaves it: pending, with a placeholder id.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PosTransaction>().IgnoreQueryFilters().Where(s => s.Id == started.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, PosTransactionStatus.Initiated).SetProperty(x => x.TransactionId, PosUnconfirmed.PendingPrefix + "crashed" + Digits(6))
                    .SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-6)), Ct);
        }

        await RecheckAsync();

        var sale = await SaleAsync(started.Id);
        sale.Status.ShouldBe(PosTransactionStatus.Unconfirmed);
        sale.TransactionId.ShouldStartWith(PosUnconfirmed.IdPrefix);
    }

    private async Task<PosTransaction> SaleAsync(Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PosTransaction>().IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == id, Ct);
    }

    private async Task<int> SalesOnAsync(string terminalId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PosTransaction>().IgnoreQueryFilters().CountAsync(s => s.TerminalId == terminalId, Ct);
    }

    private async Task<GatewayDiscrepancy?> ExceptionAsync(string transactionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<GatewayDiscrepancy>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(d => d.Kind == DiscrepancyKind.UnrecordedPayment && d.Key == transactionId, Ct);
    }

    private async Task<int> AlertsAsync(DateTimeOffset since, string terminalId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserNotification>().AsNoTracking()
            .CountAsync(n => n.Title == "Card/UPI sale never confirmed" && n.CreatedAt >= since && n.Body!.Contains(terminalId), Ct);
    }

    private async Task<int> AuditCountAsync(string action, Guid saleId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .CountAsync(a => a.Action == action && a.EntityId == saleId.ToString(), Ct);
    }

    /// <summary>Moves the sale's start back, as if it were pushed <paramref name="age"/> ago.</summary>
    private async Task AgeAsync(Guid saleId, TimeSpan age)
    {
        var at = DateTimeOffset.UtcNow - age;
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PosTransaction>().IgnoreQueryFilters().Where(s => s.Id == saleId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, at), Ct);
    }

    private async Task RecheckAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PosRecheckRunner>().RecheckSalesAsync(Ct);
    }

    private static async Task<(HttpStatusCode Status, string? Code, string? Detail)> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        var root = problem.RootElement;
        return (response.StatusCode,
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var code) ? code.GetString() : null,
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(i => i == 0 ? Random.Shared.Next(1, 10) : Random.Shared.Next(10)));
}
