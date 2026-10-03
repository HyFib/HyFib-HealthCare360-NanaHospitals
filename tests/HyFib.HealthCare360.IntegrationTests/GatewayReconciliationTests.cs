using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

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

    [Fact]
    public async Task Without_a_facility_the_exceptions_are_refused_like_the_settlements()
    {
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        using var noFacility = server.Anonymous().Http;
        noFacility.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WithoutFacility(accounts));
        var exceptions = new ApiClient(noFacility);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Get, "/api/v1/billing/gateway/exceptions"),
                     (HttpMethod.Get, $"/api/v1/billing/gateway/exceptions/{Guid.NewGuid()}"),
                     (HttpMethod.Post, $"/api/v1/billing/gateway/exceptions/{Guid.NewGuid()}/retry"),
                     (HttpMethod.Post, $"/api/v1/billing/gateway/exceptions/{Guid.NewGuid()}/record-receipt"),
                     (HttpMethod.Get, "/api/v1/billing/gateway/settlements"),
                 })
        {
            using var refused = await exceptions.SendRawAsync(method, path);
            ((int)refused.StatusCode).ShouldBeInRange(400, 499, path);
            refused.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized, path);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("facility.required", customMessage: path);
        }
    }

    [Fact]
    public async Task A_failed_refund_sent_again_twice_at_once_is_refunded_once()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var order = await PaidOrderAsync();
        var failedId = "rfnd_Failed" + Letters(10);
        var refundId = await GatewayRefundAsync(order, failedId, failed: true);

        // Two accountants press "Try again" at the same moment: both read the failed refund before either acts.
        await using var first = server.Services.CreateAsyncScope();
        await using var second = server.Services.CreateAsyncScope();
        var one = await RefundAsync(first, refundId);
        var other = await RefundAsync(second, refundId);
        var sent = await first.ServiceProvider.GetRequiredService<GatewayRefundTracker>().RetryAsync(one, Ct);
        var refused = await second.ServiceProvider.GetRequiredService<GatewayRefundTracker>().RetryAsync(other, Ct);

        sent.IsSuccess.ShouldBeTrue(sent.Error?.Message);
        refused.Error!.Code.ShouldBe("billing.refund_retrying");
        (await SimulatedRefundsAsync(order.GatewayPaymentId!)).ShouldBe([sent.Value]);

        // A retry that stopped after the gateway made the refund is taken over once it times out: the gateway gets the
        // same idempotency key and answers with the refund it already made.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var stoppedAt = DateTimeOffset.UtcNow - Refund.GatewayRetryTimeout - TimeSpan.FromMinutes(1);
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Refund>().IgnoreQueryFilters()
                .Where(r => r.Id == refundId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.GatewayRefundId, failedId)
                    .SetProperty(r => r.GatewayStatus, PaymentRefundStatus.Failed)
                    .SetProperty(r => r.GatewayRetryStartedAt, stoppedAt), Ct);
        }

        await using var later = server.Services.CreateAsyncScope();
        var again = await later.ServiceProvider.GetRequiredService<GatewayRefundTracker>().RetryAsync(await RefundAsync(later, refundId), Ct);

        again.Value.ShouldBe(sent.Value);
        (await SimulatedRefundsAsync(order.GatewayPaymentId!)).ShouldBe([sent.Value]);
        await using var check = server.Services.CreateAsyncScope();
        (await RefundAsync(check, refundId)).ShouldSatisfyAllConditions(
            r => r.GatewayRefundId.ShouldBe(sent.Value),
            r => r.GatewayStatus.ShouldBe(PaymentRefundStatus.Pending),
            r => r.GatewayRetryStartedAt.ShouldBeNull());
    }

    [Fact]
    public async Task A_settled_refund_line_does_not_close_the_exception_of_a_refund_that_failed()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var order = await PaidOrderAsync();
        var failedId = "rfnd_Failed" + Letters(10);
        var refundId = await GatewayRefundAsync(order, failedId, failed: false);

        // The training gateway knows no such refund and reports it failed: the patient has not got the money back.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var refund = await RefundAsync(scope, refundId);
            (await scope.ServiceProvider.GetRequiredService<GatewayRefundTracker>().FollowAsync(refund, Ct)).ShouldBeTrue();
            refund.GatewayStatus.ShouldBe(PaymentRefundStatus.Failed);
        }

        // A settlement carries the refund with the same amount: the line matches, the exception stays.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var settlement = Settlement(order.FacilityId, Refunded(failedId, 1m, order.GatewayPaymentId!));
            await StoreAsync(scope, settlement);
            await scope.ServiceProvider.GetRequiredService<GatewaySettlementService>().RaiseExceptionsAsync([settlement], null, Ct);
            settlement.Lines.Single().Match.ShouldBe(SettlementMatch.Matched);
        }

        (await ExceptionAsync(DiscrepancyKind.RefundMismatch, failedId))!.Status.ShouldBe(DiscrepancyStatus.Open);
    }

    [Fact]
    public async Task A_clash_with_a_concurrent_run_skips_only_the_exception_it_clashed_on()
    {
        var facilityId = await FacilityAsync();
        var otherFacility = Guid.CreateVersion7();
        var clashing = "pay_Clash" + Letters(12);
        var other = "pay_Other" + Letters(12);
        var settlement = Settlement(facilityId, Paid(clashing, 300m), Paid(other, 200m));
        settlement.Lines[1].Unmatched(otherFacility);

        await using var run = server.Services.CreateAsyncScope();
        await StoreAsync(run, settlement);
        var exceptions = run.ServiceProvider.GetRequiredService<GatewayDiscrepancyService>();
        await exceptions.RaiseAsync(DiscrepancyKind.UnrecordedPayment, clashing, facilityId, "Staged by this run.", _ => { }, Ct);

        // A webhook raises the same exception and saves it first.
        await using (var webhook = server.Services.CreateAsyncScope())
        {
            await webhook.ServiceProvider.GetRequiredService<GatewayDiscrepancyService>()
                .RaiseAsync(DiscrepancyKind.UnrecordedPayment, clashing, facilityId, "Raised by a webhook.", _ => { }, Ct);
            await webhook.ServiceProvider.GetRequiredService<HospitalDbContext>().SaveChangesAsync(Ct);
        }

        var opened = await run.ServiceProvider.GetRequiredService<GatewaySettlementService>().RaiseExceptionsAsync([settlement], null, Ct);

        opened.ShouldBe(new Dictionary<Guid, int> { [otherFacility] = 1 }, "Each facility is told about its own exceptions.");
        (await ExceptionAsync(DiscrepancyKind.UnrecordedPayment, clashing)).ShouldNotBeNull().Status.ShouldBe(DiscrepancyStatus.Open);
        (await ExceptionAsync(DiscrepancyKind.UnrecordedPayment, other)).ShouldNotBeNull().FacilityId.ShouldBe(otherFacility);
        await using var check = server.Services.CreateAsyncScope();
        (await check.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<GatewaySettlement>().IgnoreQueryFilters()
            .SingleAsync(s => s.Id == settlement.Id, Ct)).ExceptionsRaised.ShouldBeTrue();
    }

    [Fact]
    public async Task A_settlement_whose_exceptions_were_not_raised_is_matched_and_raised_again_on_the_next_fetch()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var order = await PaidOrderAsync();
        await accounts.PostAsync<SettlementFetchResult>("/api/v1/billing/gateway/settlements/fetch", new FetchSettlementsRequest(Today));

        // As if the run stopped after storing the settlement: the receipt's line is not matched, nothing is raised.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var line = await db.Set<GatewaySettlementLine>().SingleAsync(l => l.EntityId == order.GatewayPaymentId, Ct);
            await db.Set<GatewaySettlementLine>().Where(l => l.Id == line.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.Match, SettlementMatch.NoReceipt).SetProperty(l => l.ReceiptId, (Guid?)null), Ct);
            await db.Set<GatewaySettlement>().IgnoreQueryFilters().Where(s => s.Id == line.GatewaySettlementId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.ExceptionsRaised, false), Ct);
            await scope.ServiceProvider.GetRequiredService<GatewayDiscrepancyService>()
                .RaiseAsync(DiscrepancyKind.UnrecordedPayment, order.GatewayPaymentId!, order.FacilityId, "Paid but not recorded.", _ => { }, Ct);
            await db.SaveChangesAsync(Ct);
        }

        await accounts.PostAsync<SettlementFetchResult>("/api/v1/billing/gateway/settlements/fetch", new FetchSettlementsRequest(Today));

        await using var check = server.Services.CreateAsyncScope();
        var checkDb = check.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var matched = await checkDb.Set<GatewaySettlementLine>().SingleAsync(l => l.EntityId == order.GatewayPaymentId, Ct);
        matched.ShouldSatisfyAllConditions(l => l.Match.ShouldBe(SettlementMatch.Matched), l => l.ReceiptId.ShouldBe(order.ReceiptId));
        (await checkDb.Set<GatewaySettlement>().IgnoreQueryFilters().SingleAsync(s => s.Id == matched.GatewaySettlementId, Ct)).ExceptionsRaised.ShouldBeTrue();
        (await ExceptionAsync(DiscrepancyKind.UnrecordedPayment, order.GatewayPaymentId!))!.Status.ShouldBe(DiscrepancyStatus.Resolved);
    }

    [Fact]
    public async Task A_late_receipt_raises_or_clears_the_amount_difference_and_moves_the_settlement_journal()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var order = await PaidOrderAsync();
        var paymentId = order.GatewayPaymentId!;
        var heldBy = Guid.CreateVersion7();
        var settlement = Settlement(heldBy, Paid(paymentId, order.PaidAmount!.Value + 10m));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            // Stored before the receipt was recorded: nothing matched it then.
            await StoreAsync(scope, settlement, match: false);
        }

        // The settled amount differs from the receipt: an amount difference, and the line is now the receipt's facility's.
        var days = await RematchAsync(paymentId);
        days.ShouldBe([(heldBy, settlement.SettledOn), (order.FacilityId, settlement.SettledOn)], ignoreOrder: true);
        (await ExceptionAsync(DiscrepancyKind.AmountMismatch, paymentId))!.ShouldSatisfyAllConditions(
            e => e.Status.ShouldBe(DiscrepancyStatus.Open),
            e => e.FacilityId.ShouldBe(order.FacilityId),
            e => e.RecordedAmount.ShouldBe(order.PaidAmount));

        // Corrected: the amounts agree, and the difference clears.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<GatewaySettlementLine>()
                .Where(l => l.GatewaySettlementId == settlement.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.Amount, order.PaidAmount!.Value).SetProperty(l => l.Match, SettlementMatch.NoReceipt), Ct);
        }

        (await RematchAsync(paymentId)).ShouldBeEmpty("The line stays with the receipt's facility.");
        (await ExceptionAsync(DiscrepancyKind.AmountMismatch, paymentId))!.Status.ShouldBe(DiscrepancyStatus.Resolved);
    }

    [Fact]
    public async Task A_facility_sharing_the_merchant_account_sees_only_its_part_of_a_settlement()
    {
        var holder = await FacilityAsync();
        var sharing = Guid.CreateVersion7();
        var settlement = Settlement(holder, Paid("pay_Holder" + Letters(12), 1_000m, 23.60m, 3.60m), Paid("pay_Sharer" + Letters(12), 500m, 11.80m, 1.80m));
        settlement.Lines[1].Unmatched(sharing);
        await using var scope = server.Services.CreateAsyncScope();
        await StoreAsync(scope, settlement);
        var settlements = scope.ServiceProvider.GetRequiredService<GatewaySettlementService>();

        var part = (await settlements.GetAsync(sharing, settlement.Id, Ct)).ShouldNotBeNull();
        var whole = (await settlements.GetAsync(holder, settlement.Id, Ct)).ShouldNotBeNull();
        var day = (await settlements.ListAsync(sharing, settlement.SettledOn, settlement.SettledOn, Ct)).ShouldHaveSingleItem();

        part.Settlement.ShouldSatisfyAllConditions(
            s => s.IsShare.ShouldBeTrue(),
            s => s.PaymentsAmount.ShouldBe(500m),
            s => s.Amount.ShouldBe(488.20m),
            s => s.Lines.ShouldBe(1));
        part.Lines.ShouldHaveSingleItem().Amount.ShouldBe(500m);
        whole.Settlement.ShouldSatisfyAllConditions(s => s.IsShare.ShouldBeFalse(), s => s.PaymentsAmount.ShouldBe(1_500m), s => s.Lines.ShouldBe(2));
        day.ShouldSatisfyAllConditions(d => d.PaymentsAmount.ShouldBe(500m), d => d.Amount.ShouldBe(488.20m), d => d.Unmatched.ShouldBe(1));
    }

    [Fact]
    public async Task Fresh_payment_links_are_checked_however_many_older_ones_are_open()
    {
        // 06:02 UTC: a run that checks every window. Long ago, so no other test's links fall into it.
        var now = new DateTimeOffset(2020, 1, 6, 6, 2, 0, TimeSpan.Zero);
        var facilityId = await FacilityAsync();
        var batch = "it" + Letters(8);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var orders = Enumerable.Range(0, 901).Select(i => new GatewayOrder
        {
            FacilityId = facilityId,
            Provider = "test",
            Kind = GatewayOrderKind.PaymentLink,
            OrderId = $"plink_{batch}{i:0000}",
            MerchantReference = $"{batch}{i:0000}",
            Amount = 100m,
            PatientId = Guid.CreateVersion7(),
            Status = GatewayOrderStatus.Created,
        })
            .ToList();
        db.AddRange(orders);
        await db.SaveChangesAsync(Ct);
        try
        {
            var fresh = orders[^1].Id;
            var older = orders.Take(900).Select(o => o.Id).ToList();
            await db.Set<GatewayOrder>().IgnoreQueryFilters().Where(o => older.Contains(o.Id))
                .ExecuteUpdateAsync(u => u.SetProperty(o => o.CreatedAt, now.AddDays(-3)), Ct);
            await db.Set<GatewayOrder>().IgnoreQueryFilters().Where(o => o.Id == fresh)
                .ExecuteUpdateAsync(u => u.SetProperty(o => o.CreatedAt, now.AddMinutes(-10)), Ct);

            var due = await scope.ServiceProvider.GetRequiredService<GatewayReconciliationRunner>().DueOrdersAsync(now, Ct);

            due.ShouldContain(fresh);
            due.Count(older.Contains).ShouldBe(200, "Older links are checked a batch at a time.");
        }
        finally
        {
            var ids = orders.Select(o => o.Id).ToList();
            await db.Set<GatewayOrder>().IgnoreQueryFilters().Where(o => ids.Contains(o.Id)).ExecuteDeleteAsync(Ct);
        }
    }

    [Fact]
    public async Task A_refund_sent_again_long_after_it_was_first_paid_out_is_followed()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var order = await PaidOrderAsync();
        var now = new DateTimeOffset(2020, 1, 6, 7, 2, 0, TimeSpan.Zero);
        var retried = await GatewayRefundAsync(order, "rfnd_Retried" + Letters(10), failed: false);
        var forgotten = await GatewayRefundAsync(order, "rfnd_Forgot" + Letters(10), failed: false);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await db.Set<Refund>().IgnoreQueryFilters().Where(r => r.Id == retried || r.Id == forgotten)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.PaidAt, now.AddDays(-40)).SetProperty(r => r.GatewayRefundStartedAt, now.AddDays(-40)), Ct);
        await db.Set<Refund>().IgnoreQueryFilters().Where(r => r.Id == retried)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.GatewayRefundStartedAt, now.AddDays(-1)), Ct);

        var due = await scope.ServiceProvider.GetRequiredService<GatewayReconciliationRunner>().DueRefundsAsync(now, Ct);

        due.ShouldContain(retried);
        due.ShouldNotContain(forgotten);
    }

    [Fact]
    public async Task A_webhook_being_handled_is_not_handled_again_and_a_processed_one_stays_processed()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var log = scope.ServiceProvider.GetRequiredService<GatewayWebhookLog>();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var info = new GatewayWebhookInfo("payment.captured", null, null, null, null);
        var eventId = "evt" + Letters(20);

        var first = (await log.BeginAsync("test", eventId, info, null, Ct)).ShouldNotBeNull();
        (await log.BeginAsync("test", eventId, info, null, Ct)).ShouldBeNull("The first delivery is still being handled.");

        // The first delivery seems to have stopped: the next one takes it over, and only one does.
        await db.Set<GatewayWebhookEvent>().Where(e => e.Id == first)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.StartedAt, DateTimeOffset.UtcNow - GatewayWebhookLog.InFlight - TimeSpan.FromSeconds(1)), Ct);
        (await log.BeginAsync("test", eventId, info, null, Ct)).ShouldBe(first);
        (await log.BeginAsync("test", eventId, info, null, Ct)).ShouldBeNull();

        // The first delivery finishes after all; the one that took over fails afterwards and must not undo it.
        await log.CompleteAsync(first, WebhookEventOutcome.Processed, "Receipt recorded.", Ct);
        await log.CompleteAsync(first, WebhookEventOutcome.Failed, "Could not be handled.", Ct);

        var row = await db.Set<GatewayWebhookEvent>().AsNoTracking().SingleAsync(e => e.Id == first, Ct);
        row.ShouldSatisfyAllConditions(r => r.Outcome.ShouldBe(WebhookEventOutcome.Processed), r => r.Note.ShouldBe("Receipt recorded."), r => r.Deliveries.ShouldBe(4));
        (await log.BeginAsync("test", eventId, info, null, Ct)).ShouldBeNull();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    /// <summary>The signed-in user's own token with the facility left out (an account that has none).</summary>
    private string WithoutFacility(ApiClient api)
    {
        var token = new JsonWebToken(api.Http.DefaultRequestHeaders.Authorization!.Parameter);
        var claim = (string type) => token.Claims.FirstOrDefault(c => c.Type == type)?.Value;
        using var scope = server.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<TokenService>().CreateAccessToken(new AccessTokenSubject(
            Guid.Parse(claim(HcClaims.UserId)!), claim(HcClaims.UserName)!, claim(HcClaims.DisplayName)!, claim(HcClaims.UserType)!,
            Guid.Parse(claim(HcClaims.Session)!), null, null, null, null,
            token.Claims.Where(c => c.Type == HcClaims.Role).Select(c => c.Value).ToList())).Token;
    }

    /// <summary>A bill paid in full through a payment link on the training gateway; the link's order, as stored.</summary>
    private async Task<GatewayOrder> PaidOrderAsync()
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var bill = await FinalizedBillAsync(billing);
        var link = await billing.PostAsync<GatewayOrderDto>("/api/v1/billing/payment-links", new CreatePaymentLinkRequest(bill.Id, null, null, false, false));
        (await billing.PostAsync<GatewayOrderDto>($"/api/v1/billing/payment-links/{link.Id}/refresh")).Status.ShouldBe(GatewayOrderStatus.Paid);
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<GatewayOrder>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(o => o.Id == link.Id, Ct);
    }

    /// <summary>A ₹1 refund of the order's bill paid out through the gateway as <paramref name="gatewayRefundId"/>, failed or pending.</summary>
    private async Task<Guid> GatewayRefundAsync(GatewayOrder order, string gatewayRefundId, bool failed)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var now = DateTimeOffset.UtcNow;
        var refund = Refund.Request(order.FacilityId, "ITRF" + Letters(12).ToUpperInvariant(), RefundPurpose.Bill, order.PatientId, order.BillId, null, 1m,
            "Duplicate payment", Guid.CreateVersion7(), "Cashier", now);
        refund.Approve(Guid.CreateVersion7(), null, now).IsSuccess.ShouldBeTrue();
        refund.MarkPaid(PaymentMode.PaymentGateway, gatewayRefundId, null, null, null, now).IsSuccess.ShouldBeTrue();
        refund.StartGatewayRefund(gatewayRefundId, now);
        if (failed)
        {
            refund.ApplyGatewayStatus(new RefundStatusResult(gatewayRefundId, PaymentRefundStatus.Failed, 1m, null, null, now, "The bank account is closed."), now)
                .ShouldBeTrue();
        }

        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Add(refund);
        await db.SaveChangesAsync(Ct);
        return refund.Id;
    }

    private static Task<Refund> RefundAsync(AsyncServiceScope scope, Guid id) =>
        scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Refund>().IgnoreQueryFilters().SingleAsync(r => r.Id == id, Ct);

    private async Task<List<string>> SimulatedRefundsAsync(string paymentId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<SimulatorTransaction>().AsNoTracking()
            .Where(t => t.Kind == SimulatorTransactionKind.PaymentRefund && t.Reference == paymentId)
            .Select(t => t.ExternalId)
            .ToListAsync(Ct);
    }

    private async Task<GatewayDiscrepancy?> ExceptionAsync(DiscrepancyKind kind, string key)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<GatewayDiscrepancy>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(d => d.Kind == kind && d.Key == key, Ct);
    }

    private async Task<IReadOnlyList<(Guid FacilityId, DateOnly Date)>> RematchAsync(string paymentId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var days = await scope.ServiceProvider.GetRequiredService<GatewaySettlementService>().RematchPaymentAsync(paymentId, Ct);
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().SaveChangesAsync(Ct);
        return days;
    }

    private async Task<Guid> FacilityAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Facility>().OrderBy(f => f.Code).Select(f => f.Id).FirstAsync(Ct);
    }

    /// <summary>A processed settlement of yesterday, as the gateway reported it, held by <paramref name="facilityId"/>.</summary>
    private static GatewaySettlement Settlement(Guid facilityId, params PaymentSettlementItem[] items)
    {
        var settledAt = DateTimeOffset.UtcNow.AddDays(-1);
        var amount = items.Sum(i => i.Credit - i.Debit);
        return GatewaySettlement.From(
            facilityId, "simulator",
            new PaymentSettlement("setl_It" + Letters(14), "UTR" + Letters(8).ToUpperInvariant(), PaymentSettlementStatus.Processed, amount, 0m, 0m, settledAt, items),
            DateOnly.FromDateTime(settledAt.ToOffset(IndiaOffset).DateTime), DateTimeOffset.UtcNow);
    }

    private static PaymentSettlementItem Paid(string paymentId, decimal amount, decimal fee = 0m, decimal tax = 0m) =>
        new(paymentId, PaymentTransactionType.Payment, null, null, amount, fee, tax, amount - fee, 0m, "upi", DateTimeOffset.UtcNow.AddDays(-2));

    private static PaymentSettlementItem Refunded(string refundId, decimal amount, string paymentId) =>
        new(refundId, PaymentTransactionType.Refund, paymentId, null, amount, 0m, 0m, 0m, amount, "upi", DateTimeOffset.UtcNow.AddDays(-2));

    /// <summary>Stores the settlement with its lines matched, as a fetch does (or as reported, unmatched).</summary>
    private static async Task StoreAsync(AsyncServiceScope scope, GatewaySettlement settlement, bool match = true)
    {
        var services = scope.ServiceProvider;
        if (match)
        {
            await services.GetRequiredService<GatewaySettlementService>().MatchAsync(settlement.Lines, Ct);
        }

        var db = services.GetRequiredService<HospitalDbContext>();
        db.Add(settlement);
        await db.SaveChangesAsync(Ct);
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
