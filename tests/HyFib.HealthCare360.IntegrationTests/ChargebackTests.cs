using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Chargebacks on card payments (documented gap 10): the acquirer's report takes money back on a payment a receipt
/// recorded and HC360 opens a case on the receipt; accountants are told and reminded of the deadline; accepting the loss
/// asks for the receipt to be reversed, which another person approves, and the bill owes the money again. A case opened
/// from the bank's notice is joined by the chargeback the report brings later, and closes once won.
/// </summary>
public sealed class ChargebackTests(HospitalServer server)
{
    private static readonly TimeSpan India = TimeSpan.FromMinutes(330);

    private static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(India).DateTime);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_chargeback_on_a_card_payment_is_followed_as_a_case_and_its_loss_reverses_the_receipt_once_another_person_approves()
    {
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (bill, receipt, rrn) = await CardReceiptAsync();
        var amount = receipt.Payments.Single().Amount;

        // The acquirer's report carries the sale and, under its RRN, the chargeback the patient's bank raised.
        var imported = await ImportAsync(accountant, Report(rrn, amount, chargeback: true));
        imported.ShouldSatisfyAllConditions(
            i => i.Matched.ShouldBe(1),
            i => i.ToLookAt.ShouldBe(0, "the chargeback is followed as a case, not looked at as a line"));
        var opened = await CaseOfAsync(accountant, receipt.ReceiptNumber, open: true);
        opened.ShouldSatisfyAllConditions(
            d => d.Source.ShouldBe(PaymentDisputeSource.Terminal),
            d => d.Status.ShouldBe(PaymentDisputeStatus.Open),
            d => d.Amount.ShouldBe(amount),
            d => d.PaymentReference.ShouldBe(rrn),
            d => d.BillId.ShouldBe(bill.Id),
            d => d.OpenedByName.ShouldBeNull(),
            d => d.DaysLeft.ShouldBeNull());
        (await accountant.GetAsync<PagedResult<PosSettlementLineDto>>("/api/v1/billing/pos/settlements/lines?attention=true&pageSize=200"))
            .Items.ShouldNotContain(l => l.Rrn == rrn);

        // What the bank's notice says, with a deadline tomorrow: the accountants are told of the case once, and reminded
        // of the deadline once a day.
        var described = await accountant.PostAsync<PaymentDisputeDto>($"/api/v1/billing/disputes/{opened.Id}/details",
            new DescribePaymentDisputeRequest("10.4", "Card absent fraud", Today.AddDays(1), opened.Version));
        described.ShouldSatisfyAllConditions(d => d.ReasonCode.ShouldBe("10.4"), d => d.DaysLeft.ShouldBe(1));
        await RemindAsync();
        await RemindAsync();
        (await NotificationsAsync(opened.Number)).ShouldSatisfyAllConditions(
            n => n.Count(t => t.Contains("taken back", StringComparison.Ordinal)).ShouldBe(1, "told once"),
            n => n.Count(t => t.Contains("answer by", StringComparison.Ordinal)).ShouldBe(1, "reminded once a day"));

        // The accountant accepts the loss: a receipt reversal waits for someone else; meanwhile the case cannot be won.
        using (var asked = await accountant.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/disputes/{opened.Id}/accept-loss",
                   new PaymentDisputeNoteRequest("The cardholder's bank upheld the fraud claim.", described.Version)))
        {
            asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asked.Content.ReadAsStringAsync(Ct));
            var approval = (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
            approval.Kind.ShouldBe(ReceiptReversalApproval.Key);
            var waiting = await CaseAsync(accountant, opened.Id);
            waiting.LossWaiting.ShouldBeTrue();
            (await RefusedAsync(accountant, $"/api/v1/billing/disputes/{opened.Id}/won", new PaymentDisputeNoteRequest("The bank reversed it.", waiting.Version)))
                .ShouldBe("billing.dispute_loss_waiting");

            await MakerChecker.ApproveAsync(admin, approval);
        }

        // Approved: the case is lost, the receipt reversed as taken back by a chargeback, and the bill owes the money again.
        var lost = await CaseAsync(accountant, opened.Id);
        lost.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Lost),
            d => d.ReversalNumber.ShouldNotBeNull(),
            d => d.LossWaiting.ShouldBeFalse(),
            d => d.Outcome.ShouldNotBeNull().ShouldContain(receipt.ReceiptNumber));
        (await accountant.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}")).DueAmount.ShouldBe(amount);

        // Tally reads why: the receipt is turned round, taken back by a chargeback rather than never received.
        var vouchers = await accountant.GetAsync<List<AccountingVoucher>>($"/api/v1/billing/tally/day-book?date={Today:yyyy-MM-dd}");
        var journal = vouchers.Single(v => v.Number == lost.ReversalNumber);
        journal.Narration.ShouldBe($"Receipt {receipt.ReceiptNumber} reversed, {lost.ReversalNumber}: taken back by a chargeback");
        journal.Entries.ShouldContain(e => e.IsDebit && e.Amount == amount);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<ReceiptReversal>().IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.ReceiptId == receipt.Id, Ct)).Reason.ShouldBe(ReversalReason.Chargeback);
            (await db.Set<AuditEvent>().AsNoTracking().Where(e => e.EntityId == opened.Id.ToString() && e.Action.StartsWith("billing.dispute.")).Select(e => e.Action).ToListAsync(Ct))
                .ShouldBe(["billing.dispute.opened", "billing.dispute.described", "billing.dispute.loss_asked", "billing.dispute.lost"], ignoreOrder: true);
        }

        (await RefusedAsync(accountant, $"/api/v1/billing/disputes/{opened.Id}/evidence", new PaymentDisputeEvidenceRequest(Today, "Too late to send anything.", lost.Version)))
            .ShouldBe("billing.dispute_closed");
    }

    [Fact]
    public async Task A_case_opened_from_the_banks_notice_is_joined_by_the_chargeback_the_report_brings_later_and_closes_once_won()
    {
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var (_, receipt, rrn) = await CardReceiptAsync();
        var amount = receipt.Payments.Single().Amount;

        // The bank's notice comes first: more than the payment is refused, then the case opens; a second one is refused.
        using (var more = await accountant.SendRawAsync(HttpMethod.Post, "/api/v1/billing/disputes",
                   new OpenPaymentDisputeRequest(receipt.ReceiptNumber, null, amount + 1m, null, null, null)))
        {
            more.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var opened = await accountant.PostAsync<PaymentDisputeDto>("/api/v1/billing/disputes",
            new OpenPaymentDisputeRequest(receipt.ReceiptNumber, null, amount, "4837", "No cardholder authorisation", Today.AddDays(5)));
        opened.ShouldSatisfyAllConditions(
            d => d.Source.ShouldBe(PaymentDisputeSource.Notice),
            d => d.OpenedByName.ShouldNotBeNull(),
            d => d.DaysLeft.ShouldBe(5));
        (await RefusedAsync(accountant, "/api/v1/billing/disputes", new OpenPaymentDisputeRequest(receipt.ReceiptNumber, null, amount, null, null, null), HttpStatusCode.Conflict))
            .ShouldBe("billing.dispute_open");

        var contested = await accountant.PostAsync<PaymentDisputeDto>($"/api/v1/billing/disputes/{opened.Id}/evidence",
            new PaymentDisputeEvidenceRequest(Today, "Bill, receipt and the signed charge slip emailed to the acquirer's chargeback desk", opened.Version));
        contested.ShouldSatisfyAllConditions(d => d.Status.ShouldBe(PaymentDisputeStatus.Contested), d => d.DaysLeft.ShouldBeNull());

        // The acquirer's report brings the chargeback: it joins the case, which stays the one to follow.
        (await ImportAsync(accountant, Report(rrn, amount, chargeback: true))).ToLookAt.ShouldBe(0);
        var cases = await CasesAsync(accountant, open: true);
        cases.Count(d => d.ReceiptNumber == receipt.ReceiptNumber).ShouldBe(1);
        cases.Single(d => d.ReceiptNumber == receipt.ReceiptNumber).Source.ShouldBe(PaymentDisputeSource.Notice);

        // The settlement joining the case changed it: the accountant acts on it as it is now.
        var joined = await CaseAsync(accountant, opened.Id);
        joined.Version.ShouldNotBe(contested.Version);
        (await RefusedAsync(accountant, $"/api/v1/billing/disputes/{opened.Id}/won", new PaymentDisputeNoteRequest("Acted on as it was before.", contested.Version),
            HttpStatusCode.Conflict)).ShouldBe("concurrency");
        var won = await accountant.PostAsync<PaymentDisputeDto>($"/api/v1/billing/disputes/{opened.Id}/won",
            new PaymentDisputeNoteRequest("The acquirer reversed the chargeback after the evidence.", joined.Version));
        won.ShouldSatisfyAllConditions(d => d.Status.ShouldBe(PaymentDisputeStatus.Won), d => d.ClosedByName.ShouldNotBeNull());
        (await CasesAsync(accountant, open: false)).ShouldContain(d => d.Id == opened.Id);
        (await RefusedAsync(accountant, $"/api/v1/billing/disputes/{opened.Id}/details", new DescribePaymentDisputeRequest(null, null, null, won.Version)))
            .ShouldBe("billing.dispute_closed");
    }

    [Fact]
    public async Task A_notice_on_a_receipt_paid_in_two_cards_names_the_payment_it_is_about()
    {
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var (_, receipt) = await TwoCardReceiptAsync();
        var second = receipt.Payments[1];

        var lines = await accountant.GetAsync<List<DisputablePaymentDto>>($"/api/v1/billing/disputes/payment-lines?receiptNumber={receipt.ReceiptNumber}");
        lines.Select(l => (l.Id, l.Mode, l.Amount, l.HasOpenCase)).ShouldBe(
            receipt.Payments.Select(p => (p.Id, PaymentMode.Card, p.Amount, false)), ignoreOrder: true);

        // Without saying which, it is refused; naming the second card opens the case on it, which the list then shows.
        (await RefusedAsync(accountant, "/api/v1/billing/disputes",
                new OpenPaymentDisputeRequest(receipt.ReceiptNumber, null, second.Amount, null, null, null), HttpStatusCode.BadRequest))
            .ShouldBe("validation");
        var opened = await accountant.PostAsync<PaymentDisputeDto>("/api/v1/billing/disputes",
            new OpenPaymentDisputeRequest(receipt.ReceiptNumber, second.Id, second.Amount, null, null, null));
        opened.ReceiptPaymentId.ShouldBe(second.Id);
        (await accountant.GetAsync<List<DisputablePaymentDto>>($"/api/v1/billing/disputes/payment-lines?receiptNumber={receipt.ReceiptNumber}"))
            .Single(l => l.Id == second.Id).HasOpenCase.ShouldBeTrue();
        using var missing = await accountant.SendRawAsync(HttpMethod.Get, "/api/v1/billing/disputes/payment-lines?receiptNumber=NHRC269999999");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_chargeback_the_acquirer_reverses_in_a_later_report_wins_its_case_and_puts_the_money_back_in_the_payout()
    {
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var (_, receipt, rrn) = await CardReceiptAsync();
        var amount = receipt.Payments.Single().Amount;
        (await ImportAsync(accountant, Report(rrn, amount, chargeback: true))).ToLookAt.ShouldBe(0);
        var opened = await CaseOfAsync(accountant, receipt.ReceiptNumber, open: true);

        // A later report: the acquirer reversed the chargeback under the same RRN. It is a transaction of its own (not
        // the chargeback again), it wins the case by itself, and nothing is left to look at.
        var reversal = Reversal(rrn, amount);
        var reversed = await ImportAsync(accountant, reversal);
        reversed.ShouldSatisfyAllConditions(r => r.Import.Added.ShouldBe(1), r => r.ToLookAt.ShouldBe(0));
        var won = await CaseAsync(accountant, opened.Id);
        won.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Won),
            d => d.ClosedByName.ShouldBeNull(),
            d => d.Outcome.ShouldStartWith($"The acquirer reversed the chargeback: ₹{Money.FormatInr(amount)} back"));

        // The reversal adds to that day's payout: it clears the card ledger back into the bank.
        await using var scope = server.Services.CreateAsyncScope();
        var lines = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PosSettlementLine>().IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Rrn == rrn && l.Type == PosSettlementLineType.Adjustment)
            .ToListAsync(Ct);
        lines.Select(l => l.Gross).OrderBy(g => g).ShouldBe([-amount, amount]);
        lines.ShouldAllBe(l => l.DisputeId == opened.Id);

        // The same report again is refused; another report listing the reversal (its own UTR) adds nothing, because the
        // reversal is known by its own key, and the case stays won.
        (await ImportAgainAsync(accountant, reversal)).ShouldBe(HttpStatusCode.Conflict);
        var listedAgain = await ImportAsync(accountant, Reversal(rrn, amount));
        listedAgain.Import.ShouldSatisfyAllConditions(i => i.Added.ShouldBe(0), i => i.AlreadyImported.ShouldBe(1));
        (await CaseAsync(accountant, opened.Id)).Status.ShouldBe(PaymentDisputeStatus.Won);
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PosSettlementLine>().IgnoreQueryFilters().AsNoTracking()
            .CountAsync(l => l.Rrn == rrn && l.Type == PosSettlementLineType.Adjustment, Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task A_chargeback_of_part_of_a_payment_is_not_accepted_by_reversing_the_whole_receipt()
    {
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var (_, receipt, _) = await CardReceiptAsync();
        var part = Math.Round(receipt.Payments.Single().Amount / 2, 2);
        var opened = await accountant.PostAsync<PaymentDisputeDto>("/api/v1/billing/disputes",
            new OpenPaymentDisputeRequest(receipt.ReceiptNumber, null, part, null, "Part of the bill disputed", null));

        (await RefusedAsync(accountant, $"/api/v1/billing/disputes/{opened.Id}/accept-loss", new PaymentDisputeNoteRequest("The bank kept half.", opened.Version)))
            .ShouldBe("billing.dispute_part");
    }

    /// <summary>
    /// A new patient's bill paid in full by card at a counter, the RRN from the charge slip typed as the reference (or,
    /// with <paramref name="pay"/> false, finalized and left due).
    /// </summary>
    private async Task<(BillDetail Bill, ReceiptDto Receipt, string Rrn)> CardReceiptAsync(bool pay = true)
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        using (var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current"))
        {
            if (current.StatusCode == HttpStatusCode.NoContent)
            {
                await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
            }
        }

        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var registered = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Kiran"), MiddleName: null, LastName: "Chargeback" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(365 * 30)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        var service = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=REG-NEW")).Single(s => s.Code == "REG-NEW");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(registered.Id, null, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, registered.Id, null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        if (!pay)
        {
            return (bill, null!, string.Empty);
        }

        var rrn = Digits(12);
        var receipt = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            bill.Id, [new PaymentLineInput(PaymentMode.Card, bill.DueAmount, rrn, Random.Shared.Next(2000, 9999).ToString(CultureInfo.InvariantCulture), "VISA", null, null, null)], null));
        return (bill, receipt, rrn);
    }

    /// <summary>A new patient's bill paid at the counter in two card payments.</summary>
    private async Task<(BillDetail Bill, ReceiptDto Receipt)> TwoCardReceiptAsync()
    {
        var (bill, _, _) = await CardReceiptAsync(pay: false);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var first = Math.Round(bill.DueAmount / 2, 2);
        var receipt = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            bill.Id,
            [
                new PaymentLineInput(PaymentMode.Card, first, Digits(12), Random.Shared.Next(2000, 9999).ToString(CultureInfo.InvariantCulture), "VISA", null, null, null),
                new PaymentLineInput(PaymentMode.Card, bill.DueAmount - first, Digits(12), Random.Shared.Next(2000, 9999).ToString(CultureInfo.InvariantCulture), "VISA", null, null, null),
            ],
            null));
        receipt.Payments.Count.ShouldBe(2);
        return (bill, receipt);
    }

    /// <summary>The acquirer's report of today: the card sale and, when asked, a chargeback of all of it under the same RRN.</summary>
    private static string Report(string rrn, decimal amount, bool chargeback)
    {
        var day = Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var at = DateTimeOffset.UtcNow.ToOffset(India).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        var utr = "AXISN" + Digits(11);
        var money = amount.ToString("0.00", CultureInfo.InvariantCulture);
        var report = new StringBuilder("Settlement Date,UTR No,TID,Txn Date,Txn Type,Card Type,Card No,Approval Code,RRN,Txn Amount,MDR Amount,GST,Net Amount\n")
            .AppendLine(CultureInfo.InvariantCulture, $"{day},{utr},72001234,{at},SALE,VISA DEBIT,4718XXXXXXXX1111,A{Digits(5)},{rrn},{money},0.00,0.00,{money}");
        if (chargeback)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{day},{utr},72001234,{at},CHARGEBACK,VISA DEBIT,4718XXXXXXXX1111,,{rrn},{money},0.00,0.00,{money}");
        }

        return report.ToString();
    }

    /// <summary>A later report of the acquirer: the chargeback of the payment under <paramref name="rrn"/> reversed.</summary>
    private static string Reversal(string rrn, decimal amount)
    {
        var day = Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var at = DateTimeOffset.UtcNow.ToOffset(India).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        var money = amount.ToString("0.00", CultureInfo.InvariantCulture);
        return new StringBuilder("Settlement Date,UTR No,TID,Txn Date,Txn Type,Card Type,Card No,Approval Code,RRN,Txn Amount,MDR Amount,GST,Net Amount\n")
            .AppendLine(CultureInfo.InvariantCulture, $"{day},AXISN{Digits(11)},72001234,{at},CHARGEBACK REVERSAL,VISA DEBIT,4718XXXXXXXX1111,,{rrn},{money},0.00,0.00,{money}")
            .ToString();
    }

    /// <summary>Imports a report the server is expected to refuse, and says how.</summary>
    private static async Task<HttpStatusCode> ImportAgainAsync(ApiClient client, string report)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(report));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "settlement.csv");
        form.Add(new StringContent("HDFC Bank"), "acquirer");
        using var response = await client.Http.PostAsync("/api/v1/billing/pos/settlements/imports", form, Ct);
        return response.StatusCode;
    }

    private static async Task<PosSettlementImportResult> ImportAsync(ApiClient client, string report)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(report));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "settlement.csv");
        form.Add(new StringContent("HDFC Bank"), "acquirer");
        using var response = await client.Http.PostAsync("/api/v1/billing/pos/settlements/imports", form, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PosSettlementImportResult>(ApiClient.Json, Ct))!;
    }

    private static async Task<List<PaymentDisputeDto>> CasesAsync(ApiClient client, bool open) =>
        (await client.GetAsync<PagedResult<PaymentDisputeDto>>($"/api/v1/billing/disputes?open={open.ToString().ToLowerInvariant()}&terminal=true&pageSize=100")).Items.ToList();

    private static async Task<PaymentDisputeDto> CaseOfAsync(ApiClient client, string receiptNumber, bool open) =>
        (await CasesAsync(client, open)).Single(d => d.ReceiptNumber == receiptNumber);

    private static Task<PaymentDisputeDto> CaseAsync(ApiClient client, Guid id) => client.GetAsync<PaymentDisputeDto>($"/api/v1/billing/disputes/{id}");

    /// <summary>The reconciliation job's chargeback step, as it runs every five minutes.</summary>
    private async Task<int> RemindAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PaymentDisputeService>().RemindAsync(Ct);
    }

    /// <summary>The titles of the notifications the accountants got about a case, one per notice (the first accountant's).</summary>
    private async Task<List<string>> NotificationsAsync(string number)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var titles = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserNotification>().AsNoTracking()
            .Where(n => n.Title.Contains(number))
            .Select(n => new { n.UserId, n.Title })
            .ToListAsync(Ct);
        return titles.GroupBy(n => n.UserId).OrderBy(g => g.Key).First().Select(n => n.Title).ToList();
    }

    private static async Task<string> RefusedAsync(ApiClient client, string path, object body, HttpStatusCode status = HttpStatusCode.UnprocessableEntity)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return problem.GetProperty("code").GetString().ShouldNotBeNull();
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(i => i == 0 ? Random.Shared.Next(1, 10) : Random.Shared.Next(10)));
}
