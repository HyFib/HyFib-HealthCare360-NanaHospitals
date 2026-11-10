using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Features.Canteen;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PharmacyPayment = HyFib.HealthCare360.Modules.Pharmacy.Domain.PharmacyPayment;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;
using PharmacySale = HyFib.HealthCare360.Modules.Pharmacy.Domain.PharmacySale;
using SalePayment = HyFib.HealthCare360.Modules.Pharmacy.Domain.SalePayment;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The pharmacy's and the canteen's card and UPI payments in the card and UPI settlement (INT-POS, documented gap 8): the
/// acquirer's report matched with the invoice or bill the counter recorded, by the RRN or approval code the cashier typed,
/// at the counters' own terminals or Billing's; a card refund matched with the refund, not the sale; a transaction at
/// another counter's terminal set aside until its sale is recorded; and a counter payment no report carries listed as not
/// settled once that counter's reports are imported.
/// </summary>
public sealed class CounterCardSettlementTests(HospitalServer server)
{
    private static readonly TimeSpan India = TimeSpan.FromMinutes(330);

    private static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(India).DateTime);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_pharmacys_and_the_canteens_card_and_UPI_payments_and_a_card_refund_are_matched_from_the_acquirers_report()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var canteen = await server.SignInAsync(HospitalServer.Users.CanteenManager);
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacyTid = "PH" + Digits(6);
        var canteenTid = "CN" + Digits(6);
        var billingTid = "OP" + Digits(6);
        await using var terminals = await OtherTerminalsAsync(pharmacyTid, canteenTid);
        (await accounts.GetAsync<PosSettlementSummaryDto>("/api/v1/billing/pos/settlements/summary")).OtherTerminals.ShouldContain(pharmacyTid);

        // The pharmacist sells two strips by card and types the RRN from the charge slip. One strip comes back the same day:
        // another pharmacist takes it back and the accountant approves the refund, which goes back to the card under the
        // sale's RRN (a card payment is never paid back in cash).
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var saleRrn = Digits(12);
        var sold = await CounterSaleAsync(pharmacist, medicine, 2m, PharmacyPaymentMode.Card, saleRrn);
        var detail = await pharmacist.GetAsync<PharmacySaleDetail>($"/api/v1/pharmacy/sales/{sold.Id}");
        var returned = await server.ReturnSaleAsync(await server.SignInAsync(HospitalServer.Users.SecondPharmacist), sold.Id,
            new SaleReturnRequest([new SaleReturnLine(detail.Items.Single().Id, 1m, true)], "One strip not needed"));
        returned.CashRefund.ShouldBeGreaterThan(0m);

        // The canteen bills one visitor by UPI on the hospital's QR (Billing's terminal), the UPI reference typed in groups
        // of four, and another by card, the approval code typed in lower case instead of the RRN.
        var item = await MenuItemAsync(canteen, 90m);
        var upiRrn = Digits(12);
        var upiBill = await canteen.PostAsync<CanteenSaleDto>("/api/v1/canteen/sales", new CreateSaleRequest(
            CanteenCustomerType.Visitor, null, null, null, null, CanteenPaymentMode.Upi, $"{upiRrn[..4]} {upiRrn[4..8]} {upiRrn[8..]}", [new SaleLineInput(item.Id, 2)]));
        var approval = "K" + Digits(5);
        var cardBill = await canteen.PostAsync<CanteenSaleDto>("/api/v1/canteen/sales", new CreateSaleRequest(
            CanteenCustomerType.Visitor, null, null, null, null, CanteenPaymentMode.Card, approval.ToLowerInvariant(), [new SaleLineInput(item.Id, 1)]));

        // The acquirer's report of the day. It reports the refund under the sale's RRN, as many acquirers do. It also
        // carries a sale at the pharmacy's terminal that nobody recorded yet (the terminal took the card, keyed a rupee
        // over, while HC360 was down), a canteen sale HC360 knows nothing of, and one with the card bill's approval code and
        // amount but another RRN.
        var lateRrn = Digits(12);
        var lateAmount = Math.Round(medicine.Batch.Mrp, MidpointRounding.AwayFromZero);
        var unknownRrn = Digits(12);
        var utr = "AXISN" + Digits(11);
        var day = Slashed(Today);
        var at = DateTimeOffset.UtcNow.ToOffset(India).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        var imported = await ImportAsync(accounts, $"""
            Settlement Date,UTR No,TID,Txn Date,Txn Type,Card Type,Card No,Approval Code,RRN,Txn Amount,MDR Amount,GST,Net Amount
            {day},{utr},{pharmacyTid},{at},SALE,VISA DEBIT,4718XXXXXXXX1111,A{Digits(5)},{saleRrn},{Money(sold.NetAmount)},0.00,0.00,{Money(sold.NetAmount)}
            {day},{utr},{pharmacyTid},{at},REFUND,VISA DEBIT,4718XXXXXXXX1111,,{saleRrn},{Money(returned.CashRefund)},0.00,0.00,{Money(returned.CashRefund)}
            {day},{utr},{billingTid},{at},SALE,UPI,,,{upiRrn},{Money(upiBill.NetAmount)},0.00,0.00,{Money(upiBill.NetAmount)}
            {day},{utr},{canteenTid},{at},SALE,RUPAY DEBIT,6521XXXXXXXX2222,{approval},{Digits(12)},{Money(cardBill.NetAmount)},0.00,0.00,{Money(cardBill.NetAmount)}
            {day},{utr},{pharmacyTid},{at},SALE,MASTERCARD,5412XXXXXXXX3333,B{Digits(5)},{lateRrn},{Money(lateAmount + 1m)},0.00,0.00,{Money(lateAmount + 1m)}
            {day},{utr},{canteenTid},{at},SALE,UPI,,,{unknownRrn},45.00,0.00,0.00,45.00
            {day},{utr},{canteenTid},{at},SALE,VISA CREDIT,4111XXXXXXXX4444,{approval},{Digits(12)},{Money(cardBill.NetAmount)},0.00,0.00,{Money(cardBill.NetAmount)}
            """, "HDFC Bank");
        imported.ShouldSatisfyAllConditions(
            i => i.Import.Added.ShouldBe(7),
            i => i.Matched.ShouldBe(4),
            i => i.ToLookAt.ShouldBe(0, "what no counter recorded at another counter's terminal is set aside, not looked at"));

        var lines = await LinesAsync(accounts, utr);
        lines.Single(l => l.Type == PosSettlementLineType.Sale && l.Rrn == saleRrn).ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.Matched),
            l => l.MatchBasis.ShouldBe(PosMatchBasis.Rrn),
            l => l.Counter.ShouldBe("pharmacy"),
            l => l.CounterDocument.ShouldBe(sold.InvoiceNumber),
            l => l.RecordedAmount.ShouldBe(sold.NetAmount),
            l => l.ReceiptId.ShouldBeNull());
        lines.Single(l => l.Type == PosSettlementLineType.Refund && l.Rrn == saleRrn).ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.Matched, "the refund the pharmacy paid back, not the sale"),
            l => l.Counter.ShouldBe("pharmacy"),
            l => l.CounterDocument.ShouldBe(sold.InvoiceNumber),
            l => l.RecordedAmount.ShouldBe(returned.CashRefund),
            l => l.RefundId.ShouldBeNull());
        lines.Single(l => l.Rrn == upiRrn).ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.Matched, "a counter's payment on Billing's terminal is the counter's"),
            l => l.MatchBasis.ShouldBe(PosMatchBasis.Rrn),
            l => l.Counter.ShouldBe("canteen"),
            l => l.CounterDocument.ShouldBe(upiBill.BillNumber));
        var byCode = lines.Where(l => l.ApprovalCode == approval).ToList();
        byCode.Count.ShouldBe(2);
        byCode.Single(l => l.Match == PosSettlementMatch.Matched).ShouldSatisfyAllConditions(
            l => l.MatchBasis.ShouldBe(PosMatchBasis.ApprovalCode),
            l => l.Counter.ShouldBe("canteen"),
            l => l.CounterDocument.ShouldBe(cardBill.BillNumber));
        byCode.Single(l => l.Match != PosSettlementMatch.Matched).Match.ShouldBe(PosSettlementMatch.OtherCounter, "a bill is matched by one transaction");
        lines.Single(l => l.Rrn == lateRrn).ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.OtherCounter),
            l => l.Counter.ShouldBeNull(),
            l => l.NeedsAttention.ShouldBeFalse());
        lines.Single(l => l.Rrn == unknownRrn).Match.ShouldBe(PosSettlementMatch.OtherCounter);
        (await DaysAsync(accounts, Today)).Single().Payouts.Single(p => p.Utr == utr).ToLookAt.ShouldBe(0);

        // The pharmacist records the sale from its charge slip. Matched again, the transaction set aside finds it, a rupee
        // more than the invoice: it is to look at. The one HC360 knows nothing of stays set aside.
        var late = await CounterSaleAsync(pharmacist, medicine, 1m, PharmacyPaymentMode.Card, lateRrn);
        late.NetAmount.ShouldBe(lateAmount);
        var rematched = await accounts.PostAsync<PosMatchResult>("/api/v1/billing/pos/settlements/match", new { });
        rematched.Checked.ShouldBeGreaterThanOrEqualTo(2);
        lines = await LinesAsync(accounts, utr);
        lines.Single(l => l.Rrn == lateRrn).ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.AmountDiffers),
            l => l.MatchBasis.ShouldBe(PosMatchBasis.Rrn),
            l => l.Counter.ShouldBe("pharmacy"),
            l => l.CounterDocument.ShouldBe(late.InvoiceNumber),
            l => l.RecordedAmount.ShouldBe(lateAmount),
            l => l.NeedsAttention.ShouldBeTrue());
        lines.Single(l => l.Rrn == unknownRrn).Match.ShouldBe(PosSettlementMatch.OtherCounter);
    }

    [Fact]
    public async Task A_counter_payment_no_report_carries_is_listed_as_not_settled_once_its_terminals_reports_are_imported()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var canteen = await server.SignInAsync(HospitalServer.Users.CanteenManager);
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);

        // Pharmacy sales by UPI: one eight days ago and one six days ago, part of it paid back by UPI; one six days ago
        // whose invoice was cancelled that day (its money went back by UPI). Another pharmacist takes the return and asks
        // for the cancellation, and the accountant approves both.
        var settledRrn = Digits(12);
        var settled = await CounterSaleAsync(pharmacist, medicine, 1m, PharmacyPaymentMode.Upi, settledRrn);
        await MoveBackAsync(settled.Id, 8);
        var waitingRrn = Digits(12);
        var waiting = await CounterSaleAsync(pharmacist, medicine, 2m, PharmacyPaymentMode.Upi, waitingRrn);
        var detail = await pharmacist.GetAsync<PharmacySaleDetail>($"/api/v1/pharmacy/sales/{waiting.Id}");
        var secondPharmacist = await server.SignInAsync(HospitalServer.Users.SecondPharmacist);
        await server.ReturnSaleAsync(secondPharmacist, waiting.Id, new SaleReturnRequest([new SaleReturnLine(detail.Items.Single().Id, 1m, true)], "One strip not needed"));
        await MoveBackAsync(waiting.Id, 6);
        var cancelled = await CounterSaleAsync(pharmacist, medicine, 1m, PharmacyPaymentMode.Upi, Digits(12));
        await server.CancelSaleAsync(secondPharmacist, cancelled.Id, "Billed to the wrong customer");
        await MoveBackAsync(cancelled.Id, 6);

        // A card sale six days ago (no report the hospital imported carries the pharmacy's cards that far back), and a
        // UPI sale today (not due yet).
        var byCard = await CounterSaleAsync(pharmacist, medicine, 1m, PharmacyPaymentMode.Card, Digits(12));
        await MoveBackAsync(byCard.Id, 6);
        var todays = await CounterSaleAsync(pharmacist, medicine, 1m, PharmacyPaymentMode.Upi, Digits(12));

        // A canteen bill by UPI six days ago: no report the hospital imported carries the canteen's UPI that far back.
        var bill = await canteen.PostAsync<CanteenSaleDto>("/api/v1/canteen/sales", new CreateSaleRequest(
            CanteenCustomerType.Visitor, null, null, null, null, CanteenPaymentMode.Upi, Digits(12), [new SaleLineInput((await MenuItemAsync(canteen, 60m)).Id, 1)]));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var sixDaysAgo = DateTimeOffset.UtcNow.AddDays(-6);
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<CanteenSale>().IgnoreQueryFilters()
                .Where(s => s.Id == bill.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.SoldAt, sixDaysAgo), Ct);
        }

        // The acquirer's report carries the pharmacy's UPI payment of eight days ago, settled today: the pharmacy's UPI
        // is covered from then on.
        var eightDaysAgo = Today.AddDays(-8);
        var first = await ImportAsync(accounts, $"""
            RRN,Txn Date,Card Type,TID,Txn Amount,Net Amount,Settlement Date,UTR
            {settledRrn},{Slashed(eightDaysAgo)},UPI,PH{Digits(6)},{Money(settled.NetAmount)},{Money(settled.NetAmount)},{Slashed(Today)},AXISN{Digits(11)}
            """, "HDFC Bank");
        first.Matched.ShouldBe(1);

        var unsettled = await UnsettledAsync(accounts);
        var payments = await PaymentsAsync(waiting.Id, cancelled.Id);
        var waitingPayment = payments.Single(p => p.PharmacySaleId == waiting.Id && !p.IsRefund).Id;
        var cancelledPayment = payments.Single(p => p.PharmacySaleId == cancelled.Id && p.Mode == PharmacyPaymentMode.Upi && !p.IsRefund).Id;
        payments.Single(p => p.PharmacySaleId == cancelled.Id && p.IsRefund).Mode.ShouldBe(PharmacyPaymentMode.Upi, "money that came by UPI goes back by UPI");
        var paidBack = payments.Where(p => p.IsRefund).Select(p => p.Id).ToHashSet();
        unsettled.Single(u => u.PaymentId == waitingPayment).ShouldSatisfyAllConditions(
            u => u.ReceiptId.ShouldBeNull(),
            u => u.BillId.ShouldBeNull(),
            u => u.Counter.ShouldBe("pharmacy"),
            u => u.ReceiptNumber.ShouldBe(waiting.InvoiceNumber),
            u => u.Mode.ShouldBe(PaymentMode.Upi),
            u => u.Amount.ShouldBe(waiting.NetAmount),
            u => u.Reference.ShouldBe(waitingRrn),
            u => u.DaysWaiting.ShouldBe(6),
            u => u.Cancelled.ShouldBeFalse());
        unsettled.Single(u => u.PaymentId == cancelledPayment).Cancelled.ShouldBeTrue("its invoice was cancelled, but the terminal's payment still settles");
        unsettled.ShouldNotContain(u => u.ReceiptNumber == settled.InvoiceNumber, "a report carries it");
        unsettled.ShouldNotContain(u => paidBack.Contains(u.PaymentId), "money paid back does not wait for a settlement");
        unsettled.ShouldNotContain(u => u.ReceiptNumber == bill.BillNumber, "no imported report carries the canteen's UPI of that day");
        unsettled.ShouldNotContain(u => u.ReceiptNumber == byCard.InvoiceNumber, "no imported report carries the pharmacy's cards of that day");
        unsettled.ShouldNotContain(u => u.ReceiptNumber == todays.InvoiceNumber, "not due yet");

        // A later report carries the waiting payment: it is settled, and leaves the list.
        var carried = await ImportAsync(accounts, $"""
            RRN,Txn Date,Card Type,TID,Txn Amount,Net Amount,Settlement Date,UTR
            {waitingRrn},{Slashed(Today.AddDays(-6))},UPI,PH{Digits(6)},{Money(waiting.NetAmount)},{Money(waiting.NetAmount)},{Slashed(Today)},AXISN{Digits(11)}
            """, "HDFC Bank");
        carried.Matched.ShouldBe(1);
        (await UnsettledAsync(accounts)).ShouldNotContain(u => u.ReceiptNumber == waiting.InvoiceNumber);
    }

    private static Task<PharmacySaleCreated> CounterSaleAsync(ApiClient pharmacist, OwnMedicine medicine, decimal strips, PharmacyPaymentMode mode, string reference) =>
        pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", new CounterSaleRequest(
            medicine.StoreId, null, "Walk-in customer", null, null, [new CounterSaleLine(medicine.ItemId, strips, medicine.Batch.BatchId)], 0m,
            [new SalePayment(mode, Math.Round(medicine.Batch.Mrp * strips, MidpointRounding.AwayFromZero), reference)], false));

    private static Task<MenuItemDto> MenuItemAsync(ApiClient canteen, decimal price) =>
        canteen.PostAsync<MenuItemDto>("/api/v1/canteen/menu", new SaveMenuItemRequest(
            $"IT{Digits(8)}", $"Masala dosa {Digits(4)}", MenuCategory.Breakfast, price, 5m, null, true, true, true, false, 0, null, null));

    /// <summary>A sale and its payments taken <paramref name="days"/> days ago.</summary>
    private async Task MoveBackAsync(Guid saleId, int days)
    {
        var at = DateTimeOffset.UtcNow.AddDays(-days);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await db.Set<PharmacySale>().IgnoreQueryFilters().Where(s => s.Id == saleId).ExecuteUpdateAsync(u => u.SetProperty(s => s.SoldAt, at), Ct);
        await db.Set<PharmacyPayment>().Where(p => p.PharmacySaleId == saleId).ExecuteUpdateAsync(u => u.SetProperty(p => p.ReceivedAt, at), Ct);
    }

    private async Task<List<PharmacyPayment>> PaymentsAsync(params Guid[] saleIds)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PharmacyPayment>().AsNoTracking()
            .Where(p => saleIds.Contains(p.PharmacySaleId))
            .ToListAsync(Ct);
    }

    /// <summary>
    /// The terminals of the pharmacy and the canteen, set aside from Billing's matching for the rest of the test; then the
    /// setting is put back, because the server is shared by every test of the run.
    /// </summary>
    private async Task<IAsyncDisposable> OtherTerminalsAsync(params string[] terminals)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={PosSettlementSettings.Key}"))
            .FirstOrDefault(s => s.Key == PosSettlementSettings.Key && s.FacilityId is null)?.Value;
        var value = before?.DeepClone() as JsonObject ?? new JsonObject();
        var others = new JsonArray();
        foreach (var terminal in (value["otherTerminals"] as JsonArray ?? new JsonArray()).Select(t => t?.GetValue<string>()).OfType<string>().Concat(terminals))
        {
            others.Add(terminal);
        }

        value["otherTerminals"] = others;
        await admin.SaveSettingAsync(PosSettlementSettings.Key, new SaveSettingRequest(value, null, false, null));
        return new Restore(() => admin.SaveSettingAsync(PosSettlementSettings.Key, new SaveSettingRequest(before?.DeepClone() ?? new JsonObject(), null, false, null)));
    }

    private static async Task<List<PosSettlementLineDto>> LinesAsync(ApiClient client, string utr) =>
        (await client.GetAsync<PagedResult<PosSettlementLineDto>>($"/api/v1/billing/pos/settlements/lines?settledOn={Today:yyyy-MM-dd}&utr={utr}&pageSize=200")).Items.ToList();

    private static Task<List<UnsettledPosReceiptDto>> UnsettledAsync(ApiClient client) =>
        client.GetAsync<List<UnsettledPosReceiptDto>>("/api/v1/billing/pos/settlements/unsettled");

    private static Task<List<PosSettlementDayDto>> DaysAsync(ApiClient client, DateOnly day) =>
        client.GetAsync<List<PosSettlementDayDto>>($"/api/v1/billing/pos/settlements/days?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}");

    private static async Task<PosSettlementImportResult> ImportAsync(ApiClient client, string report, string acquirer)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(report));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "settlement.csv");
        form.Add(new StringContent(acquirer), "acquirer");
        using var response = await client.Http.PostAsync("/api/v1/billing/pos/settlements/imports", form, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PosSettlementImportResult>(ApiClient.Json, Ct))!;
    }

    private static string Slashed(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(i => i == 0 ? Random.Shared.Next(1, 10) : Random.Shared.Next(10)));

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
