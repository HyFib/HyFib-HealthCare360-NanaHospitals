using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Card and UPI terminal reconciliation end to end (INT-POS): a sale on the training terminal and a card payment typed at
/// the counter, the acquirer's settlement report imported and matched with their receipts, what does not match listed
/// and reviewed, the settlement journal in the day book, and a card receipt the acquirer never settled.
/// </summary>
public sealed class PosSettlementTests(HospitalServer server)
{
    private static readonly TimeSpan India = TimeSpan.FromMinutes(330);

    private static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(India).DateTime);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Terminal_and_typed_card_payments_are_matched_from_the_acquirers_report_and_journalled()
    {
        await using var terminal = await TerminalSimulatorAsync();
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await OpenCounterShiftAsync(billing);

        // A sale on the training terminal: approved at once, and its receipt recorded with the terminal's RRN.
        var bill = await FinalizedBillAsync(billing);
        var started = await billing.PostAsync<PosSaleDto>("/api/v1/billing/pos/sales", new StartPosSaleRequest(bill.Id, null, bill.DueAmount, "OPD-1", PaymentMode.Card));
        var sale = await billing.GetAsync<PosSaleDto>($"/api/v1/billing/pos/sales/{started.Id}");
        sale.ShouldSatisfyAllConditions(s => s.Status.ShouldBe(PosTransactionStatus.Approved), s => s.ReceiptId.ShouldNotBeNull(), s => s.Rrn.ShouldNotBeNull());

        // The training settlement report carries it: imported, it matches the receipt by the RRN.
        var training = await accounts.Http.GetAsync($"/api/v1/billing/pos/settlements/training-file?date={Today:yyyy-MM-dd}", Ct);
        training.StatusCode.ShouldBe(HttpStatusCode.OK);
        var trainingReport = await training.Content.ReadAsStringAsync(Ct);
        trainingReport.ShouldContain(sale.Rrn!);
        var fromTraining = await ImportAsync(accounts, trainingReport, "Training acquirer", "training.csv");
        fromTraining.Import.Added.ShouldBeGreaterThanOrEqualTo(1);
        (await LineAsync(sale.Rrn!)).ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.Matched),
            l => l.MatchBasis.ShouldBe(PosMatchBasis.Rrn),
            l => l.ReceiptId.ShouldBe(sale.ReceiptId));

        // A card payment from a terminal HC360 is not connected to: the cashier typed only the card's last four digits.
        var other = await FinalizedBillAsync(billing);
        var last4 = Random.Shared.Next(2000, 9999).ToString(CultureInfo.InvariantCulture);
        var typed = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            other.Id, [new PaymentLineInput(PaymentMode.Card, other.DueAmount, null, last4, "VISA", null, null, null)], null));

        // The bank's report of the day: the terminal sale again (the training report already brought it), the typed
        // payment, a UPI payment HC360 never recorded and a chargeback, after the bank's title rows and before its total.
        var utr = "AXISN" + Digits(11);
        var unknown = Digits(12);
        var day = Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var at = DateTimeOffset.UtcNow.ToOffset(India).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        var report = $"""
            HDFC Bank Merchant Settlement Report
            Settlement Date,UTR No,TID,Txn Date,Txn Type,Card Type,Card No,Approval Code,RRN,Txn Amount,MDR Amount,GST,Net Amount
            {day},{utr},72001234,{at},SALE,VISA DEBIT,4718XXXXXXXX1111,{sale.ApprovalCode},{sale.Rrn},{Money(sale.Amount)},0.00,0.00,{Money(sale.Amount)}
            {day},{utr},72005678,{at},SALE,VISA CREDIT,4111XXXXXXXX{last4},Q7W8E9,{Digits(12)},{Money(other.DueAmount)},10.00,1.80,{Money(other.DueAmount - 11.80m)}
            {day},{utr},72001234,{at},SALE,UPI,,,{unknown},999.00,0.00,0.00,999.00
            {day},{utr},72001234,{at},CHARGEBACK,VISA DEBIT,4718XXXXXXXX1111,,,40.00,0.00,0.00,40.00
            Total,,,,,,,,,,,,
            """;
        var imported = await ImportAsync(accounts, report, "HDFC Bank");

        imported.Import.ShouldSatisfyAllConditions(
            i => i.Rows.ShouldBe(4),
            i => i.Added.ShouldBe(3),
            i => i.AlreadyImported.ShouldBe(1, "the terminal sale came with the training report"),
            i => i.Ignored.ShouldBe(0, "a total row is not a transaction left out"));
        imported.Matched.ShouldBe(1);
        imported.ToLookAt.ShouldBe(2);
        imported.Message.ShouldBe("Imported 3 new transactions; 1 of the report's 4 were already imported. 1 matches HC360's records; 2 transactions to look at.");
        (await LineAsync(utr: utr, last4: last4)).ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.Matched),
            l => l.MatchBasis.ShouldBe(PosMatchBasis.CardAndAmount),
            l => l.ReceiptId.ShouldBe(typed.Id));

        // The same report again is refused; one that overlaps it adds only what is new.
        using (var again = await SendImportAsync(accounts, report, "HDFC Bank"))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await again.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.pos_settlement_imported");
        }

        // What to look at: the UPI payment without a receipt first, then the chargeback. Neither shows a patient.
        var attention = await accounts.GetAsync<PagedResult<PosSettlementLineDto>>("/api/v1/billing/pos/settlements/lines?attention=true&pageSize=200");
        var noReceipt = attention.Items.Single(l => l.Rrn == unknown);
        noReceipt.ShouldSatisfyAllConditions(l => l.Match.ShouldBe(PosSettlementMatch.NoReceipt), l => l.Mode.ShouldBe(PaymentMode.Upi), l => l.NeedsAttention.ShouldBeTrue());
        attention.Items.ShouldContain(l => l.Utr == utr && l.Type == PosSettlementLineType.Adjustment && l.Match == PosSettlementMatch.Review);
        attention.Items.ShouldNotContain(l => l.Match == PosSettlementMatch.Matched);

        // The accountant reviews it; it leaves the list, and the review is audited.
        var reviewed = await accounts.PostAsync<PosSettlementLineDto>(
            $"/api/v1/billing/pos/settlements/lines/{noReceipt.Id}/review", new ReviewPosSettlementLineRequest("Paid at the canteen counter; booked there.", noReceipt.Version));
        reviewed.ShouldSatisfyAllConditions(l => l.NeedsAttention.ShouldBeFalse(), l => l.ReviewNote.ShouldBe("Paid at the canteen counter; booked there."));
        (await accounts.GetAsync<PagedResult<PosSettlementLineDto>>("/api/v1/billing/pos/settlements/lines?attention=true&pageSize=200"))
            .Items.ShouldNotContain(l => l.Id == noReceipt.Id);

        // The payout of the day: what reached the bank under the UTR, and its journal number. The terminal sale stays in
        // the training report's payout, which brought it first.
        var days = await accounts.GetAsync<List<PosSettlementDayDto>>($"/api/v1/billing/pos/settlements/days?from={Today:yyyy-MM-dd}&to={Today:yyyy-MM-dd}");
        var payout = days.Single().Payouts.Single(p => p.Utr == utr);
        payout.ShouldSatisfyAllConditions(
            p => p.Lines.ShouldBe(3),
            p => p.Acquirers.ShouldBe("HDFC Bank"),
            p => p.Sales.ShouldBe(other.DueAmount + 999m),
            p => p.Refunds.ShouldBe(40m),
            p => p.Charges.ShouldBe(10m),
            p => p.Tax.ShouldBe(1.80m),
            p => p.Net.ShouldBe(other.DueAmount - 11.80m + 999m - 40m),
            p => p.ToLookAt.ShouldBe(1, "the chargeback; the UPI payment was reviewed"));

        // The day book carries the settlement journal: the payout to the bank, the charges and GST, and the gross out of
        // the card and UPI settlement ledgers the receipts went into.
        var vouchers = await accounts.GetAsync<List<AccountingVoucher>>($"/api/v1/billing/tally/day-book?date={Today:yyyy-MM-dd}");
        var journal = vouchers.Single(v => v.Number == payout.VoucherNumber);
        journal.VoucherType.ShouldBe("Journal");
        journal.Entries.Where(e => e.IsDebit).Sum(e => e.Amount).ShouldBe(journal.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount));
        journal.Entries.Single(e => e.LedgerName == "Bank").Amount.ShouldBe(payout.Net);
        journal.Entries.Single(e => e.LedgerName == "Card & UPI Charges (MDR)").Amount.ShouldBe(10m);
        journal.Entries.Single(e => e.LedgerName == "Card Settlement Account").ShouldSatisfyAllConditions(
            e => e.IsDebit.ShouldBeFalse(), e => e.Amount.ShouldBe(other.DueAmount - 40m));
        journal.Entries.Single(e => e.LedgerName == "UPI Settlement Account").Amount.ShouldBe(999m);

        await using var scope = server.Services.CreateAsyncScope();
        var actions = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.EntityId == imported.Import.Id.ToString() || e.EntityId == noReceipt.Id.ToString())
            .Select(e => e.Action)
            .ToListAsync(Ct);
        actions.ShouldBe(["billing.pos.settlement_imported", "billing.pos.settlement_reviewed"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_card_receipt_the_acquirer_has_not_settled_is_listed_until_a_report_carries_it()
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await OpenCounterShiftAsync(billing);
        var bill = await FinalizedBillAsync(billing);
        var last4 = Random.Shared.Next(2000, 9999).ToString(CultureInfo.InvariantCulture);
        var reference = Digits(12);

        // The cashier typed the RRN from the charge slip with a space and a dash; the report has the digits only.
        var typed = $"{reference[..4]} {reference[4..8]}-{reference[8..]}";
        var receipt = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            bill.Id, [new PaymentLineInput(PaymentMode.Card, bill.DueAmount, typed, last4, "RUPAY", null, null, null)], null));

        // Taken six days ago; the acquirer's reports run from eight days ago to today, but none carries it.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var sixDaysAgo = DateTimeOffset.UtcNow.AddDays(-6);
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Receipt>().IgnoreQueryFilters()
                .Where(r => r.Id == receipt.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.ReceivedAt, sixDaysAgo), Ct);
        }

        var eightDaysAgo = Today.AddDays(-8).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        await ImportAsync(accounts, $"""
            RRN,Txn Date,Card Type,Txn Amount,Net Amount,Settlement Date,UTR
            {Digits(12)},{eightDaysAgo},VISA,4321.00,4321.00,{Today:dd/MM/yyyy},AXISN{Digits(11)}
            """, "HDFC Bank");

        var unsettled = await accounts.GetAsync<List<UnsettledPosReceiptDto>>("/api/v1/billing/pos/settlements/unsettled");
        unsettled.Single(u => u.ReceiptId == receipt.Id).ShouldSatisfyAllConditions(
            u => u.Mode.ShouldBe(PaymentMode.Card),
            u => u.Amount.ShouldBe(bill.DueAmount),
            u => u.CardLast4.ShouldBe(last4),
            u => u.DaysWaiting.ShouldBe(6));
        var summary = await accounts.GetAsync<PosSettlementSummaryDto>("/api/v1/billing/pos/settlements/summary");
        summary.Unsettled.ShouldBeGreaterThanOrEqualTo(1);
        summary.SettlementDays.ShouldBe(3);

        // A later report carries it (by the RRN the cashier typed, spaces and dash aside): it is settled, and leaves the list.
        var sixDaysAgoDay = Today.AddDays(-6).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var carried = await ImportAsync(accounts, $"""
            RRN,Txn Date,Card Type,Txn Amount,Net Amount,Settlement Date,UTR
            {reference},{sixDaysAgoDay},RUPAY,{Money(bill.DueAmount)},{Money(bill.DueAmount)},{Today:dd/MM/yyyy},AXISN{Digits(11)}
            """, "HDFC Bank");
        carried.Matched.ShouldBe(1);
        (await LineAsync(reference)).ShouldSatisfyAllConditions(
            l => l.MatchBasis.ShouldBe(PosMatchBasis.Rrn),
            l => l.ReceiptId.ShouldBe(receipt.Id));
        (await accounts.GetAsync<List<UnsettledPosReceiptDto>>("/api/v1/billing/pos/settlements/unsettled")).ShouldNotContain(u => u.ReceiptId == receipt.Id);
    }

    [Fact]
    public async Task A_card_receipt_whose_money_did_not_arrive_waits_for_no_settlement()
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await OpenCounterShiftAsync(billing);
        var bill = await FinalizedBillAsync(billing);
        var receipt = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            bill.Id, [new PaymentLineInput(PaymentMode.Card, bill.DueAmount, Digits(12), Random.Shared.Next(2000, 9999).ToString(CultureInfo.InvariantCulture), "VISA", null, null, null)], null));

        // Taken six days ago, and in none of the acquirer's reports of the last eight days: it waits for a settlement.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var sixDaysAgo = DateTimeOffset.UtcNow.AddDays(-6);
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Receipt>().IgnoreQueryFilters()
                .Where(r => r.Id == receipt.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.ReceivedAt, sixDaysAgo), Ct);
        }

        var eightDaysAgo = Today.AddDays(-8).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        await ImportAsync(accounts, $"""
            RRN,Txn Date,Card Type,Txn Amount,Net Amount,Settlement Date,UTR
            {Digits(12)},{eightDaysAgo},VISA,4321.00,4321.00,{Today:dd/MM/yyyy},AXISN{Digits(11)}
            """, "HDFC Bank");
        (await accounts.GetAsync<List<UnsettledPosReceiptDto>>("/api/v1/billing/pos/settlements/unsettled")).ShouldContain(u => u.ReceiptId == receipt.Id);

        // The acquirer never paid it. Recorded as not received, one person asking and another approving, it waits no more.
        using var response = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/receipts/{receipt.Id}/reversal",
            new ReverseReceiptRequest([receipt.Payments.Single().Id], ReversalReason.NotSettled, "None of the acquirer's reports carries this charge slip."));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await MakerChecker.ApproveAsync(accounts, (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull());
        (await accounts.GetAsync<List<UnsettledPosReceiptDto>>("/api/v1/billing/pos/settlements/unsettled")).ShouldNotContain(u => u.ReceiptId == receipt.Id);
    }

    [Fact]
    public async Task A_report_imported_in_error_is_withdrawn_by_two_people_reversed_in_todays_books_and_its_receipt_matched_by_the_right_one()
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var maker = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var checker = await server.SignInAsync(HospitalServer.Users.Accountant);
        await OpenCounterShiftAsync(billing);
        var bill = await FinalizedBillAsync(billing);
        var rrn = Digits(12);
        var receipt = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            bill.Id, [new PaymentLineInput(PaymentMode.Card, bill.DueAmount, rrn, Random.Shared.Next(2000, 9999).ToString(CultureInfo.InvariantCulture), "VISA", null, null, null)], null));
        var threeDaysAgo = Today.AddDays(-3);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var takenAt = DateTimeOffset.UtcNow.AddDays(-3);
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Receipt>().IgnoreQueryFilters()
                .Where(r => r.Id == receipt.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.ReceivedAt, takenAt), Ct);
        }

        // The acquirer's provisional report, imported by mistake: the bank was not paid on its day, under its UTR. Its
        // transaction matches the receipt, and its payout's journal is in the books of two days ago.
        var twoDaysAgo = Today.AddDays(-2);
        var provisionalUtr = "AXISN" + Digits(11);
        var provisional = Report(rrn, threeDaysAgo, bill.DueAmount, twoDaysAgo, provisionalUtr);
        var wrong = await ImportAsync(maker, provisional, "HDFC Bank", "provisional.csv");
        wrong.Matched.ShouldBe(1);
        var payout = (await DaysAsync(maker, twoDaysAgo)).SelectMany(d => d.Payouts).Single(p => p.Utr == provisionalUtr);
        var journal = (await DayBookAsync(maker, twoDaysAgo)).Single(v => v.Number == payout.VoucherNumber);

        // The bank's final report, imported next, brings nothing: the provisional report brought its transaction first.
        var final = Report(rrn, threeDaysAgo, bill.DueAmount, Today.AddDays(-1), "AXISN" + Digits(11));
        (await ImportAsync(maker, final, "HDFC Bank", "final.csv")).Import.ShouldSatisfyAllConditions(
            i => i.Added.ShouldBe(0), i => i.AlreadyImported.ShouldBe(1));

        // The accountant asks to withdraw it and says why; the approver is told what it takes out. They may not approve
        // their own request.
        const string reason = "The acquirer's provisional report: the bank was paid on another day, under another UTR.";
        using var asked = await AskWithdrawalAsync(maker, wrong.Import.Id, reason);
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asked.Content.ReadAsStringAsync(Ct));
        var approval = (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
        approval.Details.ShouldContain(d => d.Label == "Matched" && d.Value.StartsWith("1 receipt", StringComparison.Ordinal));
        (await ImportOfAsync(maker, wrong.Import.Id)).WithdrawalAskedAt.ShouldNotBeNull();
        using (var own = await maker.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approval.Id}/approve", new ApproveRequest(null, approval.Version)))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // Someone else who approves reversals approves it. Its transaction matches nothing more, and the receipt is free.
        var approved = await MakerChecker.ApproveAsync(checker, approval);
        approved.Outcome.ShouldNotBeNull().ShouldSatisfyAllConditions(
            o => o.ShouldStartWith("Withdrew provisional.csv: its 1 transaction(s) are reversed in the books of "),
            o => o.ShouldEndWith("and 1 receipt(s) or refund(s) are free for a correct report."));
        (await ImportOfAsync(maker, wrong.Import.Id)).ShouldSatisfyAllConditions(
            i => i.WithdrawnOn.ShouldBe(Today),
            i => i.WithdrawnByName.ShouldBe(approved.DecidedByName),
            i => i.WithdrawReason.ShouldBe(reason),
            i => i.WithdrawalAskedAt.ShouldBeNull());
        var withdrawn = (await LinesOfAsync(rrn)).ShouldHaveSingleItem();
        withdrawn.ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.Withdrawn),
            l => l.WithdrawnOn.ShouldBe(Today),
            l => l.ReceiptId.ShouldBeNull(),
            l => l.ReceiptPaymentId.ShouldBeNull());

        // The payouts no longer show it, and it cannot be reviewed.
        (await DaysAsync(maker, twoDaysAgo)).SelectMany(d => d.Payouts).ShouldNotContain(p => p.Utr == provisionalUtr);
        using (var review = await maker.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/pos/settlements/lines/{withdrawn.Id}/review",
                   new ReviewPosSettlementLineRequest("Looked at it.", withdrawn.Version)))
        {
            review.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await review.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.pos_settlement_withdrawn");
        }

        // The books: the payout's journal stays in its day as it was, which may be in Tally already. Today's books turn it
        // round, numbered after it and the report.
        (await DayBookAsync(maker, twoDaysAgo)).Single(v => v.Number == payout.VoucherNumber).ShouldBeEquivalentTo(journal);
        var reversal = (await DayBookAsync(maker, Today)).Single(v => v.Number == $"{payout.VoucherNumber}-W{wrong.Import.Id.ToString("N")[..6].ToUpperInvariant()}");
        reversal.ShouldSatisfyAllConditions(
            v => v.Date.ShouldBe(Today),
            v => v.Narration.ShouldBe($"Reverses card and UPI settlement journal {payout.VoucherNumber} of "
                                      + $"{twoDaysAgo.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)}: its report was withdrawn as imported in error"));
        reversal.Entries.Select(e => (e.LedgerName, !e.IsDebit, e.Amount)).ShouldBe(journal.Entries.Select(e => (e.LedgerName, e.IsDebit, e.Amount)), ignoreOrder: true);

        // Asked again, it is refused.
        using (var twice = await AskWithdrawalAsync(maker, wrong.Import.Id, reason))
        {
            twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await twice.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.pos_settlement_import_withdrawn");
        }

        // The bank's final report imported again now brings the transaction, under the payout the bank received, and
        // matches it to the receipt the withdrawal freed; once more, it brings nothing and is refused.
        var correct = await ImportAsync(maker, final, "HDFC Bank", "final.csv");
        correct.ShouldSatisfyAllConditions(c => c.Import.Added.ShouldBe(1), c => c.Import.AlreadyImported.ShouldBe(0), c => c.Matched.ShouldBe(1));
        var line = (await LinesOfAsync(rrn)).Single(l => l.WithdrawnOn is null);
        line.ShouldSatisfyAllConditions(
            l => l.Match.ShouldBe(PosSettlementMatch.Matched),
            l => l.ReceiptId.ShouldBe(receipt.Id),
            l => l.ImportId.ShouldBe(correct.Import.Id),
            l => l.SettledOn.ShouldBe(Today.AddDays(-1)));
        using (var thrice = await SendImportAsync(maker, final, "HDFC Bank", "final.csv"))
        {
            thrice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await thrice.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.pos_settlement_imported");
        }

        // The withdrawn report no longer counts as imported: brought again, its transaction is the final report's already.
        var again = await ImportAsync(maker, provisional, "HDFC Bank", "provisional.csv");
        again.Import.ShouldSatisfyAllConditions(i => i.Added.ShouldBe(0), i => i.AlreadyImported.ShouldBe(1));

        await using var audit = server.Services.CreateAsyncScope();
        (await audit.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .CountAsync(e => e.EntityId == wrong.Import.Id.ToString() && e.Action == "billing.pos.settlement_import_withdrawn", Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_report_that_cannot_be_read_says_why_and_the_cashier_may_not_import()
    {
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        using (var unreadable = await SendImportAsync(accounts, "Name,Value\nA,1\n", "HDFC Bank"))
        {
            unreadable.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await unreadable.Content.ReadAsStringAsync(Ct)).ShouldContain(PosSettlementSettings.Key);
        }

        using var refused = await SendImportAsync(billing, "RRN,Txn Amount,Settlement Date\n123456789012,10.00,07/10/2026\n", "HDFC Bank");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var summary = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/pos/settlements/summary");
        summary.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<PosSettlementLine> LineAsync(string? rrn = null, string? utr = null, string? last4 = null)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var lines = scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PosSettlementLine>().IgnoreQueryFilters().AsNoTracking();
        return rrn is not null
            ? await lines.SingleAsync(l => l.Rrn == rrn, Ct)
            : await lines.SingleAsync(l => l.Utr == utr && l.CardLast4 == last4, Ct);
    }

    /// <summary>Every line that brought the transaction, a withdrawn one among them.</summary>
    private async Task<List<PosSettlementLine>> LinesOfAsync(string rrn)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PosSettlementLine>().IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.Rrn == rrn)
            .ToListAsync(Ct);
    }

    /// <summary>An acquirer's report of one card sale, with its charges and GST, paid out on <paramref name="settledOn"/> under <paramref name="utr"/>.</summary>
    private static string Report(string rrn, DateOnly takenOn, decimal amount, DateOnly settledOn, string utr) => $"""
        RRN,Txn Date,Card Type,Txn Amount,MDR Amount,GST,Net Amount,Settlement Date,UTR
        {rrn},{Slashed(takenOn)},VISA,{Money(amount)},2.00,0.36,{Money(amount - 2.36m)},{Slashed(settledOn)},{utr}
        """;

    private static string Slashed(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static Task<HttpResponseMessage> AskWithdrawalAsync(ApiClient client, Guid importId, string reason) =>
        client.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/pos/settlements/imports/{importId}/withdraw", new WithdrawPosImportRequest(reason));

    private static async Task<PosSettlementImportDto> ImportOfAsync(ApiClient client, Guid importId) =>
        (await client.GetAsync<PagedResult<PosSettlementImportDto>>("/api/v1/billing/pos/settlements/imports?pageSize=200")).Items.Single(i => i.Id == importId);

    private static Task<List<PosSettlementDayDto>> DaysAsync(ApiClient client, DateOnly day) =>
        client.GetAsync<List<PosSettlementDayDto>>($"/api/v1/billing/pos/settlements/days?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}");

    private static Task<List<AccountingVoucher>> DayBookAsync(ApiClient client, DateOnly day) =>
        client.GetAsync<List<AccountingVoucher>>($"/api/v1/billing/tally/day-book?date={day:yyyy-MM-dd}");

    private static async Task<PosSettlementImportResult> ImportAsync(ApiClient client, string report, string acquirer, string fileName = "settlement.csv")
    {
        using var response = await SendImportAsync(client, report, acquirer, fileName);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PosSettlementImportResult>(ApiClient.Json, Ct))!;
    }

    private static async Task<HttpResponseMessage> SendImportAsync(ApiClient client, string report, string acquirer, string fileName = "settlement.csv")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(report));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(acquirer), "acquirer");
        return await client.Http.PostAsync("/api/v1/billing/pos/settlements/imports", form, Ct);
    }

    private static string Money(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(i => i == 0 ? Random.Shared.Next(1, 10) : Random.Shared.Next(10)));

    private static async Task OpenCounterShiftAsync(ApiClient billing)
    {
        using var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        if (current.StatusCode == HttpStatusCode.NoContent)
        {
            await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
        }
    }

    private async Task<BillDetail> FinalizedBillAsync(ApiClient billing)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var surname = "Card" + new string(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        var registered = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Ravi"), MiddleName: null, LastName: surname, Gender: Gender.Male,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 40)), AgeYears: null, AgeMonths: null,
            Phone: "7" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        var service = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=REG-NEW")).Single(s => s.Code == "REG-NEW");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(registered.Id, null, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, registered.Id, null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        bill.DueAmount.ShouldBeGreaterThan(0m);
        return bill;
    }

    /// <summary>
    /// Card sales go to the training terminal, which approves at once, for the rest of the test; then the setting is put
    /// back, because the server is shared by every test of the run.
    /// </summary>
    private async Task<IAsyncDisposable> TerminalSimulatorAsync()
    {
        const string key = "integrations.pos";
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}"))
            .FirstOrDefault(s => s.Key == key && s.FacilityId is null)?.Value;
        await admin.SaveSettingAsync(key, new SaveSettingRequest(
            new JsonObject { ["enabled"] = true, ["provider"] = "simulator", ["simulatorApproveAfterSeconds"] = 0 }, null, false, null));
        return new Restore(() => admin.SaveSettingAsync(key,
            new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null)));
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
