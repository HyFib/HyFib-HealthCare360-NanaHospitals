using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Simulator;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Notifications;
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
    public async Task On_a_live_server_the_training_gateway_marks_no_bill_paid()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var bill = await FinalizedBillAsync(billing);
        var sentBefore = await billing.PostAsync<GatewayOrderDto>("/api/v1/billing/payment-links", new CreatePaymentLinkRequest(bill.Id, null, null, false, false));

        using (server.AsLiveServer())
        {
            // The training gateway would take the payment at once, with no money moving: on the hospital's live server
            // no link is made with it, and a link it made before is not marked paid.
            using var refused = await billing.SendRawAsync(HttpMethod.Post, "/api/v1/billing/payment-links", new CreatePaymentLinkRequest(bill.Id, null, null, false, false));
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("billing.gateway_unavailable");
            var checkedAgain = await billing.PostAsync<GatewayOrderDto>($"/api/v1/billing/payment-links/{sentBefore.Id}/refresh");
            checkedAgain.Status.ShouldNotBe(GatewayOrderStatus.Paid);
        }

        (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}")).DueAmount.ShouldBe(bill.DueAmount);
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
    public async Task Money_the_gateway_took_back_is_followed_as_a_chargeback_and_won_when_it_comes_back()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var order = await PaidOrderAsync();
        var takenId = "adj_Cb" + Letters(12);
        var returnedId = "adj_Rt" + Letters(12);

        // A later settlement takes the payment's money back: a chargeback case to follow, not an unexplained difference.
        var taken = Settlement(order.FacilityId, Adjusted(takenId, -order.Amount, order.GatewayPaymentId!));
        await RaiseAsync(taken);
        var opened = (await CasesOfAsync(order.ReceiptId!.Value)).ShouldHaveSingleItem();
        opened.ShouldSatisfyAllConditions(
            d => d.Source.ShouldBe(PaymentDisputeSource.Gateway),
            d => d.Status.ShouldBe(PaymentDisputeStatus.Open),
            d => d.Amount.ShouldBe(order.Amount),
            d => d.Mode.ShouldBe(PaymentMode.PaymentGateway),
            d => d.PaymentReference.ShouldBe(order.GatewayPaymentId),
            d => d.GatewaySettlementLineId.ShouldBe(taken.Lines.Single().Id),
            d => d.OpenedByName.ShouldBeNull("HC360 opened it from the settlement."));
        (await ExceptionAsync(DiscrepancyKind.AmountMismatch, takenId)).ShouldBeNull();

        // The next fetch of the day reads the settlement again: still one case.
        await RaiseAsync(taken, rematched: true);
        (await CasesOfAsync(order.ReceiptId.Value)).ShouldHaveSingleItem().Id.ShouldBe(opened.Id);

        // The bank decided for the hospital and the gateway gives the money back: the case is won by itself.
        var returned = Settlement(order.FacilityId, Adjusted(returnedId, order.Amount, order.GatewayPaymentId!));
        await RaiseAsync(returned);
        var won = (await CasesOfAsync(order.ReceiptId.Value)).ShouldHaveSingleItem();
        won.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Won),
            d => d.ClosedByName.ShouldBeNull(),
            d => d.ReturnLineId.ShouldBe(returned.Lines.Single().Id),
            d => d.Outcome.ShouldBe($"The gateway returned ₹{Money.FormatInr(order.Amount)} in settlement {returned.SettlementId} ({returnedId})."));

        // Read again on the next fetch, the return is still the case's, not an unexplained addition.
        await RaiseAsync(returned, rematched: true);
        (await ExceptionAsync(DiscrepancyKind.AmountMismatch, returnedId)).ShouldBeNull();
        (await AuditCountAsync("billing.dispute.opened", opened.Id)).ShouldBe(1);
        (await AuditCountAsync("billing.dispute.won", opened.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_loss_asked_for_is_not_made_once_the_gateway_gives_the_money_back()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var order = await PaidOrderAsync();
        await RaiseAsync(Settlement(order.FacilityId, Adjusted("adj_Cb" + Letters(12), -order.Amount, order.GatewayPaymentId!)));
        var opened = (await CasesOfAsync(order.ReceiptId!.Value)).ShouldHaveSingleItem();
        var current = await accountant.GetAsync<PaymentDisputeDto>($"/api/v1/billing/disputes/{opened.Id}");

        // The bank decided against the hospital: the accountant asks to accept the loss, which waits for a second person.
        using var asked = await accountant.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/disputes/{opened.Id}/accept-loss",
            new PaymentDisputeNoteRequest("The bank upheld the cardholder's claim.", current.Version));
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asked.Content.ReadAsStringAsync(Ct));
        var approval = (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();

        // Before anyone approves it, the gateway gives the money back (the decision was reversed): the case is won.
        await RaiseAsync(Settlement(order.FacilityId, Adjusted("adj_Rt" + Letters(12), order.Amount, order.GatewayPaymentId!)));
        (await CasesOfAsync(order.ReceiptId.Value)).ShouldHaveSingleItem().Status.ShouldBe(PaymentDisputeStatus.Won);

        // Approving the loss now would reverse a receipt whose money came back: it is refused, and nothing is reversed.
        using var approve = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approval.Id}/approve", new ApproveRequest(null, approval.Version));
        var problem = await approve.Content.ReadAsStringAsync(Ct);
        approve.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem);
        problem.ShouldContain("billing.dispute_won");
        await using var check = server.Services.CreateAsyncScope();
        (await check.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ReceiptReversal>().IgnoreQueryFilters()
            .AnyAsync(r => r.ReceiptId == order.ReceiptId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Part_of_the_money_given_back_leaves_the_case_open_and_the_return_to_look_at()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var order = await PaidOrderAsync();
        await RaiseAsync(Settlement(order.FacilityId, Adjusted("adj_Cb" + Letters(12), -order.Amount, order.GatewayPaymentId!)));
        var part = Math.Round(order.Amount / 2, 2);
        var returnedId = "adj_Rt" + Letters(12);

        // The bank gave back only part of what it took: the rest is still taken back, so the case stays open to be
        // answered or accepted, and the return is for an accountant to put against it.
        var returned = Settlement(order.FacilityId, Adjusted(returnedId, part, order.GatewayPaymentId!));
        await RaiseAsync(returned);
        await RaiseAsync(returned, rematched: true);

        var open = (await CasesOfAsync(order.ReceiptId!.Value)).ShouldHaveSingleItem();
        open.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Open),
            d => d.ReturnLineId.ShouldBeNull(),
            d => d.Amount.ShouldBe(order.Amount));
        (await ExceptionAsync(DiscrepancyKind.AmountMismatch, returnedId)).ShouldNotBeNull().GatewayAmount.ShouldBe(part);
    }

    [Fact]
    public async Task A_chargeback_a_concurrent_run_saved_first_leaves_nothing_of_this_runs_attempt_behind()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var order = await PaidOrderAsync();
        var settlement = Settlement(order.FacilityId, Adjusted("adj_Cb" + Letters(12), -order.Amount, order.GatewayPaymentId!));

        // This run stages the chargeback's case (and its reminder)...
        await using var run = server.Services.CreateAsyncScope();
        await StoreAsync(run, settlement);
        (await run.ServiceProvider.GetRequiredService<PaymentDisputeService>()
            .TakeGatewayAdjustmentAsync(settlement.Lines.Single(), settlement.SettlementId, Ct)).ShouldNotBeNull();

        // ...while a concurrent run opens and saves the same case first.
        await using (var other = server.Services.CreateAsyncScope())
        {
            var stored = await other.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<GatewaySettlement>().IgnoreQueryFilters()
                .Include(s => s.Lines).SingleAsync(s => s.Id == settlement.Id, Ct);
            await other.ServiceProvider.GetRequiredService<GatewaySettlementService>().RaiseExceptionsAsync([stored], null, Ct);
        }

        // This run's save clashes on the case: it lets go of all it staged, follows the saved case, and its unit of work
        // saves cleanly afterwards (no reminder left pointing at a case that was never saved).
        await run.ServiceProvider.GetRequiredService<GatewaySettlementService>().RaiseExceptionsAsync([settlement], null, Ct);
        await run.ServiceProvider.GetRequiredService<HospitalDbContext>().SaveChangesAsync(Ct);

        var dispute = (await CasesOfAsync(order.ReceiptId!.Value)).ShouldHaveSingleItem();
        await using var check = server.Services.CreateAsyncScope();
        (await check.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PaymentDisputeReminder>().CountAsync(r => r.DisputeId == dispute.Id, Ct))
            .ShouldBe(1);
        (await AuditCountAsync("billing.dispute.opened", dispute.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_dispute_the_gateway_reports_is_a_case_that_follows_the_gateways_record_to_its_end()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var order = await PaidOrderAsync();
        var raised = await RaiseDisputeAsync(order, "10.4");

        // The webhook names the dispute. What its body says of it (an amount of ₹1, "won") is not believed: the case
        // takes what the gateway holds, read with the facility's account.
        (await DeliverAsync(DisputeEvent("payment.dispute.created", raised.ExternalId, order, "won"), "evt" + Letters(16))).ShouldBe(new WebhookAck(true));
        var opened = (await CasesOfAsync(order.ReceiptId!.Value)).ShouldHaveSingleItem();
        opened.ShouldSatisfyAllConditions(
            d => d.Source.ShouldBe(PaymentDisputeSource.Gateway),
            d => d.Status.ShouldBe(PaymentDisputeStatus.Open),
            d => d.Amount.ShouldBe(order.Amount),
            d => d.GatewayDisputeId.ShouldBe(raised.ExternalId),
            d => d.GatewayStatus.ShouldBe("open"),
            d => d.GatewayPhase.ShouldBe("chargeback"),
            d => d.ReasonCode.ShouldBe("10.4"),
            d => d.RespondBy.ShouldBe(PaymentDisputeService.LastDay(raised.CreatedAt.AddDays(7))));

        // The hospital submits its evidence at the gateway; the next event makes the case contested.
        await MoveDisputeAsync(raised.ExternalId, SimulatorTransactionStatus.UnderReview);
        (await DeliverAsync(DisputeEvent("payment.dispute.under_review", raised.ExternalId, order), "evt" + Letters(16))).ShouldBe(new WebhookAck(true));
        (await CasesOfAsync(order.ReceiptId.Value)).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Contested),
            d => d.EvidenceSentOn.ShouldBe(Today));

        // The settlement that takes the money names the dispute: it joins the case instead of opening another.
        var taken = Settlement(order.FacilityId, Adjusted("adj_Cb" + Letters(12), -order.Amount, order.GatewayPaymentId!, raised.ExternalId));
        await RaiseAsync(taken);
        (await CasesOfAsync(order.ReceiptId.Value)).ShouldHaveSingleItem().GatewaySettlementLineId.ShouldBe(taken.Lines.Single().Id);

        // The bank decides for the hospital: the case is won by the gateway's word, and the money returned later is its.
        await MoveDisputeAsync(raised.ExternalId, SimulatorTransactionStatus.Won);
        (await DeliverAsync(DisputeEvent("payment.dispute.won", raised.ExternalId, order), "evt" + Letters(16))).ShouldBe(new WebhookAck(true));
        var returnedId = "adj_Rt" + Letters(12);
        var returned = Settlement(order.FacilityId, Adjusted(returnedId, order.Amount, order.GatewayPaymentId!, raised.ExternalId));
        await RaiseAsync(returned);
        var won = (await CasesOfAsync(order.ReceiptId.Value)).ShouldHaveSingleItem();
        won.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Won),
            d => d.ClosedByName.ShouldBeNull(),
            d => d.Outcome.ShouldBe("The gateway decided the dispute for the hospital."),
            d => d.ReturnLineId.ShouldBe(returned.Lines.Single().Id));
        (await ExceptionAsync(DiscrepancyKind.AmountMismatch, returnedId)).ShouldBeNull();

        // The closed case is still the dispute's: read again (another event, or the hourly read), it opens no other.
        (await DeliverAsync(DisputeEvent("payment.dispute.won", raised.ExternalId, order), "evt" + Letters(16))).ShouldBe(new WebhookAck(true));
        (await CasesOfAsync(order.ReceiptId.Value)).ShouldHaveSingleItem().Id.ShouldBe(opened.Id);
        (await AuditCountAsync("billing.dispute.opened", opened.Id)).ShouldBe(1);
        (await AuditCountAsync("billing.dispute.won", opened.Id)).ShouldBe(1);
        (await AuditCountAsync("billing.dispute.returned", opened.Id)).ShouldBe(1);

        // What the gateway says of the case afterwards (here, that it was lost) is followed: the case stays won, and it is
        // not audited as a second win.
        var followedBefore = await AuditCountAsync("billing.dispute.followed", opened.Id);
        await MoveDisputeAsync(raised.ExternalId, SimulatorTransactionStatus.Lost);
        (await DeliverAsync(DisputeEvent("payment.dispute.lost", raised.ExternalId, order), "evt" + Letters(16))).ShouldBe(new WebhookAck(true));
        (await CasesOfAsync(order.ReceiptId.Value)).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Won),
            d => d.GatewayStatus.ShouldBe("lost"),
            d => d.ReversalId.ShouldBeNull());
        (await AuditCountAsync("billing.dispute.won", opened.Id)).ShouldBe(1);
        (await AuditCountAsync("billing.dispute.followed", opened.Id)).ShouldBe(followedBefore + 1);
    }

    [Fact]
    public async Task A_loss_waiting_for_its_approver_when_the_gateway_decides_for_the_hospital_reverses_nothing()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var order = await PaidOrderAsync();
        var raised = await RaiseDisputeAsync(order, "13.1");
        (await DeliverAsync(DisputeEvent("payment.dispute.created", raised.ExternalId, order), "evt" + Letters(16))).ShouldBe(new WebhookAck(true));
        var opened = (await CasesOfAsync(order.ReceiptId!.Value)).ShouldHaveSingleItem();
        var current = await accountant.GetAsync<PaymentDisputeDto>($"/api/v1/billing/disputes/{opened.Id}");

        // The accountant asks to accept the loss; before anyone approves it, the gateway decides for the hospital.
        using var asked = await accountant.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/disputes/{opened.Id}/accept-loss",
            new PaymentDisputeNoteRequest("Nothing to send the bank.", current.Version));
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asked.Content.ReadAsStringAsync(Ct));
        var approval = (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
        await MoveDisputeAsync(raised.ExternalId, SimulatorTransactionStatus.Won);
        (await DeliverAsync(DisputeEvent("payment.dispute.won", raised.ExternalId, order), "evt" + Letters(16))).ShouldBe(new WebhookAck(true));
        (await CasesOfAsync(order.ReceiptId.Value)).ShouldHaveSingleItem().Status.ShouldBe(PaymentDisputeStatus.Won);

        // Approving the loss now is refused, and nothing is reversed: the case was won after the loss was asked for.
        using var approve = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approval.Id}/approve", new ApproveRequest(null, approval.Version));
        var problem = await approve.Content.ReadAsStringAsync(Ct);
        approve.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, problem);
        problem.ShouldContain("billing.dispute_won");
        await using var check = server.Services.CreateAsyncScope();
        (await check.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ReceiptReversal>().IgnoreQueryFilters()
            .AnyAsync(r => r.ReceiptId == order.ReceiptId, Ct)).ShouldBeFalse();
        (await CasesOfAsync(order.ReceiptId.Value)).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Won),
            d => d.ReversalId.ShouldBeNull());
    }

    [Fact]
    public async Task A_loss_the_gateway_decided_is_told_once_and_booked_only_by_a_second_person()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var order = await PaidOrderAsync();
        var raised = await RaiseDisputeAsync(order, "4837");
        await DeliverAsync(DisputeEvent("payment.dispute.created", raised.ExternalId, order), "evt" + Letters(16));
        await MoveDisputeAsync(raised.ExternalId, SimulatorTransactionStatus.Lost);

        (await DeliverAsync(DisputeEvent("payment.dispute.lost", raised.ExternalId, order), "evt" + Letters(16))).ShouldBe(new WebhookAck(true));

        var decided = (await CasesOfAsync(order.ReceiptId!.Value)).ShouldHaveSingleItem();
        decided.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Open, "a loss is booked only by a reversal a second person approves"),
            d => d.GatewayStatus.ShouldBe("lost"),
            d => d.ReversalId.ShouldBeNull());
        var current = await accountant.GetAsync<PaymentDisputeDto>($"/api/v1/billing/disputes/{decided.Id}");
        current.ShouldSatisfyAllConditions(d => d.DecidedAgainst.ShouldBeTrue(), d => d.DaysLeft.ShouldBeNull());
        await RemindAsync();
        await RemindAsync();
        (await NotificationTitlesAsync(decided.Number)).Count(t => t == $"Chargeback {decided.Number} was lost at the gateway").ShouldBe(1);

        using var asked = await accountant.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/disputes/{decided.Id}/accept-loss",
            new PaymentDisputeNoteRequest("The bank decided against the hospital at the gateway.", current.Version));
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asked.Content.ReadAsStringAsync(Ct));
        var approval = (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
        await admin.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval.Id}/approve", new ApproveRequest(null, approval.Version));

        var lost = await accountant.GetAsync<PaymentDisputeDto>($"/api/v1/billing/disputes/{decided.Id}");
        lost.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Lost),
            d => d.ReversalNumber.ShouldNotBeNull(),
            d => d.DecidedAgainst.ShouldBeFalse());
    }

    [Fact]
    public async Task A_dispute_whose_webhook_never_came_is_found_by_the_hourly_read_and_joins_the_case_from_the_notice()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var order = await PaidOrderAsync();
        string receiptNumber;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            receiptNumber = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Receipt>().IgnoreQueryFilters()
                .Where(r => r.Id == order.ReceiptId).Select(r => r.ReceiptNumber).SingleAsync(Ct);
        }

        // The bank's notice came first: the accountant typed the deadline it gave.
        var notice = await accountant.PostAsync<PaymentDisputeDto>("/api/v1/billing/disputes",
            new OpenPaymentDisputeRequest(receiptNumber, null, order.Amount, null, "Cardholder does not recognise the payment", Today.AddDays(10)));
        var raised = await RaiseDisputeAsync(order, "fraudulent");

        // No webhook arrives; the hourly read of the gateway's disputes finds it, and twice is the same as once.
        (await FollowGatewayDisputesAsync(order.FacilityId)).ShouldNotBeNull();
        (await FollowGatewayDisputesAsync(order.FacilityId)).ShouldNotBeNull();

        var joined = await accountant.GetAsync<PaymentDisputeDto>($"/api/v1/billing/disputes/{notice.Id}");
        joined.ShouldSatisfyAllConditions(
            d => d.Source.ShouldBe(PaymentDisputeSource.Notice),
            d => d.GatewayDisputeId.ShouldBe(raised.ExternalId),
            d => d.ReasonCode.ShouldBe("fraudulent"),
            d => d.Reason.ShouldBe("Cardholder does not recognise the payment"),
            d => d.RespondBy.ShouldBe(PaymentDisputeService.LastDay(raised.CreatedAt.AddDays(7)), "the gateway's deadline is the one to answer by"));
        (await CasesOfAsync(order.ReceiptId!.Value)).ShouldHaveSingleItem();
        (await AuditCountAsync("billing.dispute.followed", notice.Id)).ShouldBe(1);

        // The deadline is the gateway's: recording the notice with another date is refused.
        using var other = await accountant.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/disputes/{notice.Id}/details",
            new DescribePaymentDisputeRequest(null, null, Today.AddDays(20), joined.Version));
        other.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await other.Content.ReadAsStringAsync(Ct)).ShouldContain("respondBy");
    }

    [Fact]
    public async Task Money_taken_back_on_a_payment_HC360_never_recorded_stays_an_exception()
    {
        var facilityId = await FacilityAsync();
        var takenId = "adj_Cb" + Letters(12);
        var settlement = Settlement(facilityId, Adjusted(takenId, -700m, "pay_Unknown" + Letters(10)));

        await RaiseAsync(settlement);

        var exception = (await ExceptionAsync(DiscrepancyKind.AmountMismatch, takenId)).ShouldNotBeNull();
        exception.Status.ShouldBe(DiscrepancyStatus.Open);
        exception.GatewayAmount.ShouldBe(-700m);
    }

    [Fact]
    public async Task A_chargeback_staged_in_a_run_that_clashed_is_opened_once()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var order = await PaidOrderAsync();
        var clashing = "pay_Clash" + Letters(12);
        var settlement = Settlement(order.FacilityId, Paid(clashing, 300m), Adjusted("adj_Cb" + Letters(12), -order.Amount, order.GatewayPaymentId!));

        await using var run = server.Services.CreateAsyncScope();
        await StoreAsync(run, settlement);
        await run.ServiceProvider.GetRequiredService<GatewayDiscrepancyService>()
            .RaiseAsync(DiscrepancyKind.UnrecordedPayment, clashing, order.FacilityId, "Staged by this run.", _ => { }, Ct);

        // A webhook saves the same exception first: this run's first save fails, the chargeback staged with it as well.
        await using (var webhook = server.Services.CreateAsyncScope())
        {
            await webhook.ServiceProvider.GetRequiredService<GatewayDiscrepancyService>()
                .RaiseAsync(DiscrepancyKind.UnrecordedPayment, clashing, order.FacilityId, "Raised by a webhook.", _ => { }, Ct);
            await webhook.ServiceProvider.GetRequiredService<HospitalDbContext>().SaveChangesAsync(Ct);
        }

        await run.ServiceProvider.GetRequiredService<GatewaySettlementService>().RaiseExceptionsAsync([settlement], null, Ct);

        var dispute = (await CasesOfAsync(order.ReceiptId!.Value)).ShouldHaveSingleItem();
        dispute.GatewaySettlementLineId.ShouldBe(settlement.Lines.Single(l => l.Type == PaymentTransactionType.Adjustment).Id);
        (await AuditCountAsync("billing.dispute.opened", dispute.Id)).ShouldBe(1);
        await using var check = server.Services.CreateAsyncScope();
        (await check.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<GatewaySettlement>().IgnoreQueryFilters()
            .SingleAsync(s => s.Id == settlement.Id, Ct)).ExceptionsRaised.ShouldBeTrue();
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

    [Fact]
    public async Task A_settlement_that_fails_after_its_payout_is_reversed_in_the_books_of_that_day_raised_and_restored_when_paid_out()
    {
        var heldBy = Guid.CreateVersion7();
        var settlement = Settlement(heldBy, Paid("pay_It" + Letters(14), 1_000m, 23.60m, 3.60m));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await StoreAsync(scope, settlement, match: false);
        }

        var journal = (await DayBookAsync(heldBy, settlement.SettledOn)).ShouldHaveSingleItem();
        journal.Number.ShouldBe(settlement.SettlementId);

        // The bank returned the transfer: the journal stays in its day (it may be in Tally already), today's books
        // reverse it, and the failure is raised and audited.
        (await ApplyAsync(heldBy, settlement, PaymentSettlementStatus.Failed)).Message.ShouldEndWith("1 settlement paid out earlier failed: their journals are reversed today.");
        (await DayBookAsync(heldBy, settlement.SettledOn)).ShouldHaveSingleItem().ShouldBe(journal);
        var reversal = (await DayBookAsync(heldBy, Today)).ShouldHaveSingleItem();
        reversal.ShouldSatisfyAllConditions(
            r => r.Number.ShouldBe(settlement.SettlementId + "-R1"),
            r => (r.Net, r.Charges, r.Tax).ShouldBe((-journal.Net, -journal.Charges, -journal.Tax)),
            r => r.Correction.ShouldNotBeNull().ShouldStartWith($"Reverses journal {settlement.SettlementId} of "));
        (await ExceptionAsync(DiscrepancyKind.FailedSettlement, settlement.SettlementId + "-R1")).ShouldNotBeNull().ShouldSatisfyAllConditions(
            e => e.Status.ShouldBe(DiscrepancyStatus.Open),
            e => e.GatewayAmount.ShouldBe(settlement.Amount),
            e => e.Summary.ShouldContain("failed after it was paid out"));
        (await AuditCountAsync("billing.gateway.settlement_failed", settlement.Id)).ShouldBe(1);

        // Read again while it stays failed: reversed once.
        await ApplyAsync(heldBy, settlement, PaymentSettlementStatus.Failed);
        (await DayBookAsync(heldBy, Today)).ShouldHaveSingleItem();

        // Paid out after all: the journal is restored in today's books, and the exception clears.
        await ApplyAsync(heldBy, settlement, PaymentSettlementStatus.Processed);
        var today = await DayBookAsync(heldBy, Today);
        today.Select(v => v.Number).ShouldBe([settlement.SettlementId + "-R1", settlement.SettlementId + "-S2"]);
        (today[1].Net, today[1].Charges, today[1].Tax).ShouldBe((journal.Net, journal.Charges, journal.Tax));
        (await ExceptionAsync(DiscrepancyKind.FailedSettlement, settlement.SettlementId + "-R1"))!.Status.ShouldBe(DiscrepancyStatus.Resolved);
        (await AuditCountAsync("billing.gateway.settlement_restored", settlement.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task A_settlement_webhook_reads_that_settlements_day_again()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var settlement = Settlement(await FacilityAsync(), Paid("pay_It" + Letters(14), 500m));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await StoreAsync(scope, settlement, match: false);
        }

        var body = new JsonObject
        {
            ["event"] = "settlement.processed",
            ["payload"] = new JsonObject { ["settlement"] = new JsonObject { ["entity"] = new JsonObject { ["id"] = settlement.SettlementId } } },
        }.ToJsonString();
        var eventId = "evt" + Guid.NewGuid().ToString("N")[..20];

        // Read at once, not left to the hourly job (which reads only today and yesterday).
        (await DeliverAsync(body, eventId)).ShouldBe(new WebhookAck(true));
        await using var check = server.Services.CreateAsyncScope();
        var logged = await check.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<GatewayWebhookEvent>().AsNoTracking()
            .SingleAsync(e => e.EventId == eventId, Ct);
        logged.Note.ShouldNotBeNull().ShouldStartWith($"Settlements of {settlement.SettledOn:dd MMM yyyy} read again");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The settlement journals of a facility's day in the Tally day book.</summary>
    private async Task<IReadOnlyList<TallyGatewaySettlement>> DayBookAsync(Guid facilityId, DateOnly date)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await GatewaySettlementService.ForDayBookAsync(scope.ServiceProvider.GetRequiredService<HospitalDbContext>(), facilityId, date, Ct);
    }

    /// <summary>The settlement's day as the gateway reports it now, the settlement in this status.</summary>
    private async Task<SettlementFetchResult> ApplyAsync(Guid facilityId, GatewaySettlement settlement, PaymentSettlementStatus status)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var report = new PaymentSettlementReport("simulator", true,
            [new PaymentSettlement(settlement.SettlementId, settlement.Utr, status, settlement.Amount, 0m, 0m, settlement.SettledAt, [])], null);
        var applied = await scope.ServiceProvider.GetRequiredService<GatewaySettlementService>().ApplyAsync(facilityId, settlement.SettledOn, report, Ct);
        applied.IsSuccess.ShouldBeTrue(applied.Error?.Message);
        return applied.Value;
    }

    private async Task<int> AuditCountAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = entityId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<HyFib.HealthCare360.Platform.Auditing.AuditEvent>()
            .CountAsync(e => e.Action == action && e.EntityId == id, Ct);
    }

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

    /// <summary>The gateway took money back on a payment (negative), or gave it back (positive), in a settlement, for <paramref name="disputeId"/>.</summary>
    private static PaymentSettlementItem Adjusted(string adjustmentId, decimal gross, string paymentId, string? disputeId = null) =>
        new(adjustmentId, PaymentTransactionType.Adjustment, paymentId, null, Math.Abs(gross), 0m, 0m, Math.Max(gross, 0m), Math.Max(-gross, 0m), null,
            DateTimeOffset.UtcNow.AddDays(-1), disputeId);

    /// <summary>The patient's bank disputes the whole payment of the order through the training gateway.</summary>
    private async Task<SimulatorTransaction> RaiseDisputeAsync(GatewayOrder order, string reasonCode)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<SimulatorInbox>().RaiseDisputeAsync(order.GatewayPaymentId!, order.Amount, reasonCode, Ct)).ShouldNotBeNull();
    }

    private async Task MoveDisputeAsync(string disputeId, SimulatorTransactionStatus status)
    {
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<SimulatorInbox>().MoveDisputeAsync(disputeId, status, Ct)).ShouldBeTrue();
    }

    /// <summary>
    /// A dispute's webhook as Razorpay sends it: the payment and the dispute. Its amount (₹1) and the status it claims are
    /// what a forged or stale body might say; HC360 reads the dispute from the gateway instead.
    /// </summary>
    private static string DisputeEvent(string eventType, string disputeId, GatewayOrder order, string status = "open") => new JsonObject
    {
        ["entity"] = "event",
        ["event"] = eventType,
        ["contains"] = new JsonArray("payment", "dispute"),
        ["payload"] = new JsonObject
        {
            ["payment"] = new JsonObject { ["entity"] = new JsonObject { ["id"] = order.GatewayPaymentId, ["amount"] = 100 } },
            ["dispute"] = new JsonObject
            {
                ["entity"] = new JsonObject { ["id"] = disputeId, ["payment_id"] = order.GatewayPaymentId, ["amount"] = 100, ["status"] = status },
            },
        },
    }.ToJsonString();

    /// <summary>The hourly read of the facility's gateway disputes, as the reconciliation job runs it.</summary>
    private async Task<int?> FollowGatewayDisputesAsync(Guid facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentDisputeService>().FollowGatewayDisputesAsync(facilityId, Ct);
    }

    /// <summary>The reconciliation job's five-minute chargeback step.</summary>
    private async Task<int> RemindAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentDisputeService>().RemindAsync(Ct);
    }

    /// <summary>The titles of the notifications the first accountant got about a case.</summary>
    private async Task<List<string>> NotificationTitlesAsync(string number)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var titles = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserNotification>().AsNoTracking()
            .Where(n => n.Title.Contains(number))
            .Select(n => new { n.UserId, n.Title })
            .ToListAsync(Ct);
        return titles.GroupBy(n => n.UserId).OrderBy(g => g.Key).First().Select(n => n.Title).ToList();
    }

    /// <summary>Stores the settlement (once) and raises its exceptions, as a fetch does; <paramref name="rematched"/> as a later fetch of its day.</summary>
    private async Task RaiseAsync(GatewaySettlement settlement, bool rematched = false)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        GatewaySettlement stored;
        if (rematched)
        {
            stored = await services.GetRequiredService<HospitalDbContext>().Set<GatewaySettlement>().IgnoreQueryFilters()
                .Include(s => s.Lines).SingleAsync(s => s.Id == settlement.Id, Ct);
        }
        else
        {
            await StoreAsync(scope, settlement);
            stored = settlement;
        }

        await services.GetRequiredService<GatewaySettlementService>()
            .RaiseExceptionsAsync([stored], rematched ? new HashSet<Guid> { stored.Id } : null, Ct);
    }

    /// <summary>The chargeback cases on the payments of a receipt.</summary>
    private async Task<List<PaymentDispute>> CasesOfAsync(Guid receiptId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PaymentDispute>().IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.ReceiptId == receiptId)
            .ToListAsync(Ct);
    }

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
            Title: "Ms.", FirstName: TestPatients.FirstName("Asha"), MiddleName: null, LastName: surname, Gender: Gender.Female,
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
        await admin.SaveSettingAsync(key, new SaveSettingRequest(
            new JsonObject { ["enabled"] = true, ["provider"] = "simulator", ["simulatorSettlementDays"] = 0 }, null, false, null));
        await admin.SaveSettingAsync(secretKey, new SaveSettingRequest(
            new JsonObject { ["simulatorSecret"] = SimulatorSecret }, null, true, null));
        return new Restore(async () =>
        {
            await admin.SaveSettingAsync(key, new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null));
            await admin.SaveSettingAsync(secretKey, new SaveSettingRequest(new JsonObject(), null, true, null));
        });
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
