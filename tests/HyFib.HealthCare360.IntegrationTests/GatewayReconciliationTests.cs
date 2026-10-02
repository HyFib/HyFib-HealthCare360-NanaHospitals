using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Reconciling the payment gateway end to end with the training gateway, which settles at once in this test: a patient
/// pays a payment link, the receipt is recorded, the gateway's settlement is read and matched to that receipt, the
/// settlement journal is in the day book, and a webhook the gateway delivers twice is acted on once.
/// </summary>
public sealed class GatewayReconciliationTests(HospitalServer server)
{
    private const string SimulatorSecret = "integration-test-simulator-key";
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromMinutes(330);

    private static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IndiaOffset).DateTime);

    [Fact]
    public async Task A_paid_link_is_settled_matched_to_its_receipt_and_journalled()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var bill = await FinalizedBillAsync(billing);

        // The cashier sends a link; the training gateway takes the payment at once and the check records the receipt.
        var link = await billing.PostAsync<GatewayOrderDto>("/api/v1/billing/payment-links", new CreatePaymentLinkRequest(bill.Id, null, null, false, false));
        var paid = await billing.PostAsync<GatewayOrderDto>($"/api/v1/billing/payment-links/{link.Id}/refresh");
        paid.Status.ShouldBe(GatewayOrderStatus.Paid);
        paid.ReceiptId.ShouldNotBeNull();
        paid.PaymentId.ShouldNotBeNull();
        paid.PaidAt.ShouldNotBeNull("The receipt is dated when the gateway took the money.");
        (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}")).DueAmount.ShouldBe(0m);

        // The accountant reads today's settlements: the payment is in one, matched to its receipt.
        var fetched = await accounts.PostAsync<SettlementFetchResult>("/api/v1/billing/gateway/settlements/fetch", new FetchSettlementsRequest(Today));
        fetched.Settlements.ShouldBeGreaterThan(0);
        var days = await accounts.GetAsync<List<SettlementDayDto>>($"/api/v1/billing/gateway/settlements?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        var settlement = await FindSettlementAsync(accounts, days, paid.PaymentId!);
        var line = settlement.Lines.Single(l => l.EntityId == paid.PaymentId);
        line.ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(SettlementMatch.Matched),
            l => l.ReceiptId.ShouldBe(paid.ReceiptId),
            l => l.Amount.ShouldBe(bill.DueAmount),
            l => (l.Fee > 0m).ShouldBeTrue("The training gateway charges a fee, like a real one."));
        settlement.Settlement.Status.ShouldBe(PaymentSettlementStatus.Processed);
        settlement.Settlement.Difference.ShouldBe(0m);

        // Nothing about this payment needs attention.
        var open = await accounts.GetAsync<PagedResult<GatewayDiscrepancyDto>>("/api/v1/billing/gateway/exceptions?pageSize=200");
        open.Items.ShouldNotContain(e => e.Key == paid.PaymentId);

        // The day book carries the settlement journal: the payout to the bank, the gateway's charges and GST, and the
        // gross out of the payment-gateway clearing ledger the receipt went into.
        var vouchers = await accounts.GetAsync<List<AccountingVoucher>>($"/api/v1/billing/tally/day-book?date={Today:yyyy-MM-dd}");
        var journal = vouchers.Single(v => v.Number.StartsWith(settlement.Settlement.SettlementId, StringComparison.Ordinal));
        journal.VoucherType.ShouldBe("Journal");
        journal.Entries.Where(e => e.IsDebit).Sum(e => e.Amount).ShouldBe(journal.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount));
        journal.Entries.ShouldContain(e => e.LedgerName == "Payment Gateway Clearing" && !e.IsDebit);
        journal.Entries.ShouldContain(e => e.LedgerName == "Payment Gateway Charges" && e.IsDebit);
    }

    [Fact]
    public async Task A_webhook_the_gateway_delivers_again_is_acted_on_once()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var bill = await FinalizedBillAsync(billing);
        var link = await billing.PostAsync<GatewayOrderDto>("/api/v1/billing/payment-links", new CreatePaymentLinkRequest(bill.Id, null, null, false, false));
        var body = new JsonObject
        {
            ["event"] = "payment_link.paid",
            ["payload"] = new JsonObject { ["payment_link"] = new JsonObject { ["entity"] = new JsonObject { ["id"] = link.OrderId } } },
        }.ToJsonString();
        var eventId = "evt" + Guid.NewGuid().ToString("N")[..20];

        var first = await DeliverAsync(body, eventId);
        var again = await DeliverAsync(body, eventId);
        var forged = await DeliverAsync(body, "evt" + Guid.NewGuid().ToString("N")[..20], signature: new string('0', 64));

        first.ShouldBe(new WebhookAck(true), "The first delivery records the payment.");
        again.ShouldBe(new WebhookAck(false), "A redelivery is acknowledged and not acted on again.");
        forged.ShouldBeNull("A body without the gateway's signature is refused.");
        var order = await billing.GetAsync<PagedResult<GatewayOrderDto>>($"/api/v1/billing/payment-links?billId={bill.Id}");
        order.Items.ShouldHaveSingleItem().Status.ShouldBe(GatewayOrderStatus.Paid);
    }

    [Fact]
    public async Task Exceptions_and_settlements_are_for_accountants_and_administrators_only()
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        using var refused = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/gateway/exceptions");

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<WebhookAck?> DeliverAsync(string body, string eventId, string? signature = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/webhooks/payment-gateway")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Razorpay-Signature", signature ?? Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SimulatorSecret), Encoding.UTF8.GetBytes(body))));
        request.Headers.Add("X-Razorpay-Event-Id", eventId);
        using var response = await server.Anonymous(ownAddress: true).Http.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode == HttpStatusCode.Unauthorized ? null : await ApiClient.ReadAsync<WebhookAck>(response, HttpMethod.Post, "webhook");
    }

    private static async Task<GatewaySettlementDetail> FindSettlementAsync(ApiClient api, IEnumerable<SettlementDayDto> days, string paymentId)
    {
        foreach (var settlement in days.SelectMany(d => d.Settlements))
        {
            var detail = await api.GetAsync<GatewaySettlementDetail>($"/api/v1/billing/gateway/settlements/{settlement.Id}");
            if (detail.Lines.Any(l => l.EntityId == paymentId))
            {
                return detail;
            }
        }

        throw new ShouldAssertException($"No settlement of today carries {paymentId}.");
    }

    /// <summary>A new patient's finalized patient-level bill for a registration fee, with its whole amount due.</summary>
    private async Task<BillDetail> FinalizedBillAsync(ApiClient billing)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var surname = "Gateway" + new string(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        var registered = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: "Asha", MiddleName: null, LastName: surname, Gender: Gender.Female,
            DateOfBirth: new DateOnly(1985, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "8" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        var service = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=REG-NEW")).Single(s => s.Code == "REG-NEW");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(
            registered.Id, null, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, registered.Id, null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        bill.DueAmount.ShouldBeGreaterThan(0m);
        return bill;
    }

    /// <summary>
    /// Online payments go to the training gateway, settled at once and signed with a known key, for the rest of the
    /// test; then the settings are put back, because the server is shared by every test of the run.
    /// </summary>
    private async Task<IAsyncDisposable> PaymentsToSimulatorAsync()
    {
        const string key = "integrations.payment";
        const string secretKey = "integrations.payment.secret";
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}"))
            .FirstOrDefault(s => s.Key == key && s.FacilityId is null)?.Value;
        await admin.PutAsync($"/api/v1/admin/settings/{key}", new SaveSettingRequest(
            new JsonObject { ["enabled"] = true, ["provider"] = "simulator", ["simulatorSettlementDays"] = 0 }, null, false, null));
        await admin.PutAsync($"/api/v1/admin/settings/{secretKey}", new SaveSettingRequest(
            new JsonObject { ["simulatorSecret"] = SimulatorSecret }, null, true, null));
        return new Restore(async () =>
        {
            await admin.PutAsync($"/api/v1/admin/settings/{key}", new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null));
            await admin.PutAsync($"/api/v1/admin/settings/{secretKey}", new SaveSettingRequest(new JsonObject(), null, true, null));
        });
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
