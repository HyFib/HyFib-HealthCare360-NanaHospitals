using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Features.Canteen;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;
using SalePayment = HyFib.HealthCare360.Modules.Pharmacy.Domain.SalePayment;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The pharmacy and the canteen take card and UPI payments on the hospital's terminal (INT-POS), with the training
/// terminal: the counter pushes the amount, the walk-in customer pays, and the invoice records the payment with the RRN,
/// approval code and card digits the terminal reported, so nothing is typed and the acquirer's settlement finds it. A
/// payment records one invoice of its own counter, as taken; one the counter never records is kept as an exception for
/// the accountants until it does, and one they close is no longer the counter's; a sale nobody pays is cancelled on the
/// terminal; and the go-live check lists the training terminal's payments until their money is paid back.
/// </summary>
public sealed class CounterTerminalTests(HospitalServer server)
{
    private static readonly TimeSpan India = TimeSpan.FromMinutes(330);

    private static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(India).DateTime);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_walk_in_paid_on_the_terminal_at_the_pharmacy_is_recorded_as_the_terminal_reported_it_and_its_settlement_finds_the_invoice()
    {
        await using var terminal = await TrainingTerminal.UseAsync(server, approveAfterSeconds: 0);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var amount = Math.Round(medicine.Batch.Mrp * 2m, MidpointRounding.AwayFromZero);

        // The pharmacist pushes the bill to the terminal by the counter and the walk-in pays by card: nothing is typed.
        var paid = await PaidAsync(pharmacist, "pharmacy", amount, CounterPaymentMode.Card);
        paid.ShouldSatisfyAllConditions(
            s => s.Counter.ShouldBe("pharmacy"),
            s => s.Rrn.ShouldNotBeNullOrEmpty(),
            s => s.ApprovalCode.ShouldNotBeNullOrEmpty(),
            s => s.CardLast4.ShouldBe("1111"),
            s => s.UsedFor.ShouldBeNull());
        (await pharmacist.GetAsync<List<CounterTerminalSale>>("/api/v1/pharmacy/terminal-sales/unused")).ShouldContain(s => s.Id == paid.Id, "paid, and on no invoice yet");

        var sold = await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter",
            CounterSale(medicine, 2m, new SalePayment(PharmacyPaymentMode.Card, amount, null, paid.Id)));
        (await pharmacist.GetAsync<PharmacySaleDetail>($"/api/v1/pharmacy/sales/{sold.Id}")).Payments.Single().ShouldSatisfyAllConditions(
            p => p.Reference.ShouldBe(paid.Rrn, "the settlement match reads the terminal's RRN"),
            p => p.ApprovalCode.ShouldBe(paid.ApprovalCode),
            p => p.CardLast4.ShouldBe("1111"),
            p => p.TerminalSaleId.ShouldBe(paid.Id));
        (await pharmacist.GetAsync<CounterTerminalSale>($"/api/v1/pharmacy/terminal-sales/{paid.Id}")).UsedFor.ShouldBe(sold.InvoiceNumber);
        (await pharmacist.GetAsync<List<CounterTerminalSale>>("/api/v1/pharmacy/terminal-sales/unused")).ShouldNotContain(s => s.Id == paid.Id);

        // The payment cannot pay for a second invoice.
        (await RefusalAsync(pharmacist, "/api/v1/pharmacy/sales/counter", CounterSale(medicine, 2m, new SalePayment(PharmacyPaymentMode.Card, amount, null, paid.Id))))
            .ShouldBe((HttpStatusCode.Conflict, "pos.sale_used"));

        // The acquirer's report carries the transaction under the terminal's RRN: the pharmacy's invoice, not a receipt.
        var utr = "SIMUTR" + Digits(10);
        var imported = await ImportAsync(accounts, $"""
            Settlement Date,UTR No,TID,Txn Date,Txn Type,Card Type,Card No,Approval Code,RRN,Txn Amount,MDR Amount,GST,Net Amount
            {Slashed(Today)},{utr},SIM{Digits(6)},{Now()},SALE,VISA DEBIT,4718XXXXXXXX1111,{paid.ApprovalCode},{paid.Rrn},{Money(amount)},0.00,0.00,{Money(amount)}
            """);
        imported.Matched.ShouldBe(1);
        (await LinesAsync(accounts, utr)).Single().ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.Matched),
            l => l.MatchBasis.ShouldBe(PosMatchBasis.Rrn),
            l => l.Counter.ShouldBe("pharmacy"),
            l => l.CounterDocument.ShouldBe(sold.InvoiceNumber),
            l => l.ReceiptId.ShouldBeNull());

        // The training terminal took no money: the go-live check lists the invoice until its money is paid back.
        (await SimulatorRecords.OpenAsync(admin, "card-sales")).ShouldContain(r => r.Entity == "PharmacySale" && r.EntityId == sold.Id && r.Amount == amount);
        await server.CancelSaleAsync(await server.SignInAsync(HospitalServer.Users.SecondPharmacist), sold.Id, "Taken on the training terminal");
        (await SimulatorRecords.OpenAsync(admin, "card-sales")).ShouldNotContain(r => r.EntityId == sold.Id, "the cancellation paid its card money back");
    }

    [Fact]
    public async Task A_canteen_bill_paid_on_a_terminal_billing_uses_is_matched_with_the_bill_not_with_a_billing_receipt_of_the_same_card_and_amount()
    {
        await using var terminal = await TrainingTerminal.UseAsync(server, approveAfterSeconds: 0);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var canteen = await server.SignInAsync(HospitalServer.Users.CanteenManager);
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);

        // Billing takes part of a bill on the terminal; the training terminal's cards all end 1111.
        await TrainingTerminal.OpenCounterShiftAsync(billing);
        var bill = await TrainingTerminal.FinalizedBillAsync(server, billing);
        var price = Math.Min(50m, Math.Floor(bill.DueAmount));
        var started = await billing.PostAsync<PosSaleDto>("/api/v1/billing/pos/sales", new StartPosSaleRequest(bill.Id, null, price, "OPD-7", PaymentMode.Card));
        var receipt = (await billing.GetAsync<PosSaleDto>($"/api/v1/billing/pos/sales/{started.Id}")).ReceiptId.ShouldNotBeNull();

        // The canteen takes a visitor's bill of the same amount by card, and another one by UPI, on terminals of its own.
        var item = await MenuItemAsync(canteen, price);
        var byCard = await PaidAsync(canteen, "canteen", price, CounterPaymentMode.Card);
        var cardBill = await canteen.PostAsync<CanteenSaleDto>("/api/v1/canteen/sales", new CreateSaleRequest(
            CanteenCustomerType.Visitor, null, null, null, null, CanteenPaymentMode.Card, null, [new SaleLineInput(item.Id, 1)], byCard.Id));
        cardBill.ShouldSatisfyAllConditions(
            b => b.NetAmount.ShouldBe(price),
            b => b.PaymentReference.ShouldBe(byCard.Rrn),
            b => b.ApprovalCode.ShouldBe(byCard.ApprovalCode),
            b => b.CardLast4.ShouldBe("1111"),
            b => b.TerminalSaleId.ShouldBe(byCard.Id));
        var byUpi = await PaidAsync(canteen, "canteen", price, CounterPaymentMode.Upi);
        var upiBill = await canteen.PostAsync<CanteenSaleDto>("/api/v1/canteen/sales", new CreateSaleRequest(
            CanteenCustomerType.Visitor, null, null, null, null, CanteenPaymentMode.Upi, null, [new SaleLineInput(item.Id, 1)], byUpi.Id));

        // The report carries the card bill at Billing's terminal with its card digits and amount, which Billing's receipt
        // shares, and the UPI bill by its approval code only. Both are the canteen's.
        var utr = "SIMUTR" + Digits(10);
        var imported = await ImportAsync(accounts, $"""
            Settlement Date,UTR No,TID,Txn Date,Txn Type,Card Type,Card No,Approval Code,RRN,Txn Amount,MDR Amount,GST,Net Amount
            {Slashed(Today)},{utr},OP{Digits(6)},{Now()},SALE,VISA DEBIT,4718XXXXXXXX1111,{byCard.ApprovalCode},{byCard.Rrn},{Money(price)},0.00,0.00,{Money(price)}
            {Slashed(Today)},{utr},OP{Digits(6)},{Now()},SALE,UPI,,{byUpi.ApprovalCode},,{Money(price)},0.00,0.00,{Money(price)}
            """);
        imported.Matched.ShouldBe(2);
        var lines = await LinesAsync(accounts, utr);
        lines.Single(l => l.Rrn == byCard.Rrn).ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.Matched),
            l => l.MatchBasis.ShouldBe(PosMatchBasis.Rrn),
            l => l.Counter.ShouldBe("canteen"),
            l => l.CounterDocument.ShouldBe(cardBill.BillNumber),
            l => l.ReceiptId.ShouldBeNull($"Billing's receipt {receipt} of the same card digits and amount is not this transaction"));
        lines.Single(l => l.Rrn is null).ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.Matched),
            l => l.MatchBasis.ShouldBe(PosMatchBasis.ApprovalCode),
            l => l.Counter.ShouldBe("canteen"),
            l => l.CounterDocument.ShouldBe(upiBill.BillNumber));

        // The go-live check lists today's training bills until they are cancelled.
        (await SimulatorRecords.OpenAsync(admin, "card-sales")).ShouldContain(r => r.Entity == "CanteenSale" && r.EntityId == cardBill.Id && r.Amount == price);
    }

    [Fact]
    public async Task A_payment_the_counter_never_records_is_kept_as_an_exception_until_it_records_it()
    {
        await using var terminal = await TrainingTerminal.UseAsync(server, approveAfterSeconds: 0);
        var canteen = await server.SignInAsync(HospitalServer.Users.CanteenManager);
        var item = await MenuItemAsync(canteen, 70m);
        var terminalId = "CN" + Digits(8);
        var paid = await PaidAsync(canteen, "canteen", 70m, CounterPaymentMode.Upi, terminalId);

        // The screen was closed after the customer paid. Within the facility's timeout nothing is raised.
        await AgeAsync(paid.Id, TimeSpan.FromMinutes(5));
        await RecheckAsync();
        (await ExceptionAsync(paid.TransactionId)).ShouldBeNull();

        // Past it, the money is kept as an exception and the accountants are told, once.
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        await AgeAsync(paid.Id, TimeSpan.FromMinutes(20));
        await RecheckAsync();
        var alerts = await AlertsAsync(since, terminalId);
        alerts.ShouldBeGreaterThan(0, "the accountants hear of money taken that no bill records");
        await RecheckAsync();
        (await AlertsAsync(since, terminalId)).ShouldBe(alerts, "a later re-check tells no one again");
        var sale = await SaleAsync(paid.Id);
        sale.Message.ShouldNotBeNull().ShouldStartWith(PosService.NotUsedPrefix);
        (await ExceptionAsync(paid.TransactionId)).ShouldNotBeNull().ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(DiscrepancyStatus.Open),
            d => d.GatewayAmount.ShouldBe(70m),
            d => d.BillId.ShouldBeNull());
        (await canteen.GetAsync<List<CounterTerminalSale>>("/api/v1/canteen/terminal-sales/unused")).ShouldContain(s => s.Id == paid.Id, "the counter can still record it");

        // The cashier records it on the visitor's bill: the exception clears.
        var bill = await canteen.PostAsync<CanteenSaleDto>("/api/v1/canteen/sales", new CreateSaleRequest(
            CanteenCustomerType.Visitor, null, null, null, null, CanteenPaymentMode.Upi, null, [new SaleLineInput(item.Id, 1)], paid.Id));
        sale = await SaleAsync(paid.Id);
        sale.ShouldSatisfyAllConditions(
            s => s.OwnerDocument.ShouldBe(bill.BillNumber),
            s => s.OwnerPaymentId.ShouldBe(bill.Id),
            s => s.Message.ShouldBeNull("the payment is on a bill now"));
        (await ExceptionAsync(paid.TransactionId)).ShouldNotBeNull().ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(DiscrepancyStatus.Resolved),
            d => d.ResolvedAutomatically.ShouldBeTrue());
    }

    [Fact]
    public async Task A_payment_the_accountants_closed_is_neither_offered_to_the_counter_nor_recorded()
    {
        await using var terminal = await TrainingTerminal.UseAsync(server, approveAfterSeconds: 0);
        var canteen = await server.SignInAsync(HospitalServer.Users.CanteenManager);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var item = await MenuItemAsync(canteen, 60m);
        var paid = await PaidAsync(canteen, "canteen", 60m, CounterPaymentMode.Card);
        await AgeAsync(paid.Id, TimeSpan.FromMinutes(20));
        await RecheckAsync();
        var raised = (await ExceptionAsync(paid.TransactionId)).ShouldNotBeNull();

        // The visitor left: the accountant refunds the card on the terminal and closes the exception with a note.
        await accountant.PostAsync<GatewayDiscrepancyDto>($"/api/v1/billing/gateway/exceptions/{raised.Id}/resolve",
            new ResolveDiscrepancyRequest("Refunded to the card on the terminal; the visitor left without the bill.", raised.Version));

        (await canteen.GetAsync<List<CounterTerminalSale>>("/api/v1/canteen/terminal-sales/unused")).ShouldNotContain(s => s.Id == paid.Id);
        (await RefusalAsync(canteen, "/api/v1/canteen/sales", new CreateSaleRequest(
                CanteenCustomerType.Visitor, null, null, null, null, CanteenPaymentMode.Card, null, [new SaleLineInput(item.Id, 1)], paid.Id)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "pos.sale_closed"), "money paid back must not pay for a bill");
    }

    [Fact]
    public async Task A_payment_is_recorded_only_by_its_own_counter_as_the_terminal_took_it_and_one_nobody_pays_times_out()
    {
        // The training terminal waits five minutes for the customer unless a sale is moved back in time.
        await using var terminal = await TrainingTerminal.UseAsync(server, approveAfterSeconds: 300);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var canteen = await server.SignInAsync(HospitalServer.Users.CanteenManager);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var amount = Math.Round(medicine.Batch.Mrp, MidpointRounding.AwayFromZero);
        var item = await MenuItemAsync(canteen, amount);

        // Still waiting for the customer: no invoice records it yet.
        var waiting = await pharmacist.PostAsync<CounterTerminalSale>("/api/v1/pharmacy/terminal-sales", new StartTerminalSaleRequest(amount, "PH-1", CounterPaymentMode.Card, null));
        waiting.Status.ShouldBe(PosTransactionStatus.AwaitingCustomer);
        (await RefusalAsync(pharmacist, "/api/v1/pharmacy/sales/counter", CounterSale(medicine, 1m, new SalePayment(PharmacyPaymentMode.Card, amount, null, waiting.Id))))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "pos.sale_pending"));

        // Cancelled on the terminal: no money was taken.
        var cancelled = await pharmacist.PostAsync<CounterTerminalSale>($"/api/v1/pharmacy/terminal-sales/{waiting.Id}/cancel");
        cancelled.Status.ShouldBe(PosTransactionStatus.Cancelled);
        (await RefusalAsync(pharmacist, "/api/v1/pharmacy/sales/counter", CounterSale(medicine, 1m, new SalePayment(PharmacyPaymentMode.Card, amount, null, waiting.Id))))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "pos.sale_not_paid"));

        // Paid: it records only the pharmacy's invoice, by card, of its amount.
        var started = await pharmacist.PostAsync<CounterTerminalSale>("/api/v1/pharmacy/terminal-sales", new StartTerminalSaleRequest(amount, "PH-1", CounterPaymentMode.Card, null));
        await AgeAsync(started.Id, TimeSpan.FromMinutes(6), terminalToo: true);
        var paid = await pharmacist.GetAsync<CounterTerminalSale>($"/api/v1/pharmacy/terminal-sales/{started.Id}");
        paid.Status.ShouldBe(PosTransactionStatus.Approved);
        using (var other = await canteen.SendRawAsync(HttpMethod.Get, $"/api/v1/canteen/terminal-sales/{paid.Id}"))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.NotFound, "another counter's terminal sale is not the canteen's to see");
        }

        (await RefusalAsync(canteen, "/api/v1/canteen/sales", new CreateSaleRequest(
                CanteenCustomerType.Visitor, null, null, null, null, CanteenPaymentMode.Card, null, [new SaleLineInput(item.Id, 1)], paid.Id)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "pos.sale_unknown"));
        (await RefusalAsync(pharmacist, "/api/v1/pharmacy/sales/counter", CounterSale(medicine, 1m,
                new SalePayment(PharmacyPaymentMode.Card, amount - 1m, null, paid.Id), new SalePayment(PharmacyPaymentMode.Cash, 1m, null))))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "pos.sale_amount"));
        (await RefusalAsync(pharmacist, "/api/v1/pharmacy/sales/counter", CounterSale(medicine, 1m, new SalePayment(PharmacyPaymentMode.Upi, amount, null, paid.Id))))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "pos.sale_mode"));
        (await RefusalAsync(pharmacist, "/api/v1/pharmacy/sales/counter", CounterSale(medicine, 1m, new SalePayment(PharmacyPaymentMode.Cash, amount, null, paid.Id))))
            .Status.ShouldBe(HttpStatusCode.BadRequest, "only a card or UPI payment is taken on the terminal");
        var sold = await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter",
            CounterSale(medicine, 1m, new SalePayment(PharmacyPaymentMode.Card, amount, null, paid.Id)));
        sold.PaidAmount.ShouldBe(amount);

        // A sale nobody pays is cancelled on the terminal by the background re-check once the facility's timeout passes.
        var abandoned = await canteen.PostAsync<CounterTerminalSale>("/api/v1/canteen/terminal-sales", new StartTerminalSaleRequest(amount, "CN-1", CounterPaymentMode.Upi, null));
        await AgeAsync(abandoned.Id, TimeSpan.FromMinutes(16));
        await RecheckAsync();
        var timedOut = await canteen.GetAsync<CounterTerminalSale>($"/api/v1/canteen/terminal-sales/{abandoned.Id}");
        timedOut.Status.ShouldBe(PosTransactionStatus.TimedOut);
        (await SimulatorStatusAsync(abandoned.TransactionId)).ShouldBe(SimulatorTransactionStatus.Cancelled, "the terminal stops waiting for the customer");
        (await ExceptionAsync(abandoned.TransactionId)).ShouldBeNull("no money was taken");
    }

    /// <summary>The counter pushes the amount and follows the terminal until the customer has paid.</summary>
    private static async Task<CounterTerminalSale> PaidAsync(ApiClient cashier, string counter, decimal amount, CounterPaymentMode mode, string? terminalId = null)
    {
        var started = await cashier.PostAsync<CounterTerminalSale>($"/api/v1/{counter}/terminal-sales",
            new StartTerminalSaleRequest(amount, terminalId ?? (counter == "pharmacy" ? "PH-1" : "CN-1"), mode, null));
        started.Status.ShouldBe(PosTransactionStatus.AwaitingCustomer);
        var paid = await cashier.GetAsync<CounterTerminalSale>($"/api/v1/{counter}/terminal-sales/{started.Id}");
        paid.Status.ShouldBe(PosTransactionStatus.Approved, paid.Message);
        paid.Amount.ShouldBe(amount);
        return paid;
    }

    private static CounterSaleRequest CounterSale(OwnMedicine medicine, decimal strips, params SalePayment[] payments) =>
        new(medicine.StoreId, null, "Walk-in customer", null, null, [new CounterSaleLine(medicine.ItemId, strips, medicine.Batch.BatchId)], 0m, payments, false);

    private static Task<MenuItemDto> MenuItemAsync(ApiClient canteen, decimal price) =>
        canteen.PostAsync<MenuItemDto>("/api/v1/canteen/menu", new SaveMenuItemRequest(
            $"IT{Digits(8)}", $"Filter coffee {Digits(4)}", MenuCategory.Beverages, price, 5m, null, true, true, true, false, 0, null, null));

    /// <summary>Moves the sale's start and approval back (and the training terminal's, which approves by its own clock).</summary>
    private async Task AgeAsync(Guid saleId, TimeSpan age, bool terminalToo = false)
    {
        var at = DateTimeOffset.UtcNow - age;
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await db.Set<PosTransaction>().IgnoreQueryFilters().Where(s => s.Id == saleId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, at).SetProperty(x => x.CompletedAt, x => x.CompletedAt == null ? null : at), Ct);
        if (terminalToo)
        {
            var transactionId = await db.Set<PosTransaction>().IgnoreQueryFilters().Where(s => s.Id == saleId).Select(s => s.TransactionId).SingleAsync(Ct);
            await db.Set<SimulatorTransaction>().Where(t => t.ExternalId == transactionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, at), Ct);
        }
    }

    private async Task RecheckAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PosRecheckRunner>().RecheckSalesAsync(Ct);
    }

    private async Task<PosTransaction> SaleAsync(Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PosTransaction>().IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == id, Ct);
    }

    private async Task<GatewayDiscrepancy?> ExceptionAsync(string transactionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<GatewayDiscrepancy>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(d => d.Kind == DiscrepancyKind.UnrecordedPayment && d.Key == transactionId, Ct);
    }

    /// <summary>The accountants' alerts about a canteen payment on the terminal <paramref name="terminalId"/> no bill records.</summary>
    private async Task<int> AlertsAsync(DateTimeOffset since, string terminalId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserNotification>().AsNoTracking()
            .CountAsync(n => n.Title == "Card/UPI payment not recorded at the canteen" && n.CreatedAt >= since && n.Body!.Contains(terminalId), Ct);
    }

    private async Task<SimulatorTransactionStatus> SimulatorStatusAsync(string transactionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<SimulatorTransaction>().AsNoTracking()
            .Where(t => t.ExternalId == transactionId).Select(t => t.Status).SingleAsync(Ct);
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (response.StatusCode, problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private static async Task<PosSettlementImportResult> ImportAsync(ApiClient client, string report)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(report));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "settlement.csv");
        form.Add(new StringContent("Pine Labs"), "acquirer");
        using var response = await client.Http.PostAsync("/api/v1/billing/pos/settlements/imports", form, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PosSettlementImportResult>(ApiClient.Json, Ct))!;
    }

    private static async Task<List<PosSettlementLineDto>> LinesAsync(ApiClient client, string utr) =>
        (await client.GetAsync<PagedResult<PosSettlementLineDto>>($"/api/v1/billing/pos/settlements/lines?settledOn={Today:yyyy-MM-dd}&utr={utr}&pageSize=200")).Items.ToList();

    private static string Now() => DateTimeOffset.UtcNow.ToOffset(India).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Slashed(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(i => i == 0 ? Random.Shared.Next(1, 10) : Random.Shared.Next(10)));
}
