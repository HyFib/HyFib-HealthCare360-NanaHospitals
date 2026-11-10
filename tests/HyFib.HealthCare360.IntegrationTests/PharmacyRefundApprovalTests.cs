using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Inpatient;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;
using SalePayment = HyFib.HealthCare360.Modules.Pharmacy.Domain.SalePayment;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Money out of the pharmacy waits for a second person (maker-checker, slice 2), over the real API: a same-day cancellation,
/// and a return that pays money out or writes stock off, are asked for by a pharmacist who did not sell the invoice and
/// approved by someone who approves every other refund (the accountant); until then nothing moves. The money goes back the
/// way it came (a card sale to the card, never from the till), the stock comes back, and both names are kept. An approval
/// over an invoice that changed since fails and changes nothing. A ward return restocked is made at once; one not restocked
/// waits for a second pharmacist and leaves the controlled drug in the register as returned and written off.
/// </summary>
public sealed class PharmacyRefundApprovalTests(HospitalServer server)
{
    private const string Approvals = "/api/v1/approvals";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_card_sale_cancelled_waits_for_someone_who_approves_refunds_and_goes_back_to_the_card_not_the_till()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var second = await server.SignInAsync(HospitalServer.Users.SecondPharmacist);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var rrn = Digits(12);
        var sold = await SellAsync(pharmacist, medicine, 2m, PaymentMode.Card, rrn);
        var onShelf = await AvailableAsync(pharmacist, medicine);
        var reason = new PharmacyReasonRequest("Billed to the wrong customer");

        // The pharmacist who sold it may not even ask, whatever they may do otherwise.
        (await RefusalAsync(pharmacist, Cancel(sold.Id), reason)).ShouldBe((HttpStatusCode.Forbidden, "pharmacy.cancel_own_sale"));

        // Another pharmacist asks: the request comes back (202), saying in words what the approver decides.
        using var response = await second.SendRawAsync(HttpMethod.Post, Cancel(sold.Id), reason);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, Cancel(sold.Id));
        asked.ShouldSatisfyAllConditions(
            a => a.Kind.ShouldBe("pharmacy.sale_cancel"),
            a => a.Reason.ShouldBe("Billed to the wrong customer"),
            a => Detail(a, "Invoice").ShouldStartWith(sold.InvoiceNumber),
            a => Detail(a, "Patient").ShouldBe("Walk-in customer"),
            a => Detail(a, "Items").ShouldContain("× 2"),
            a => Detail(a, "Amount").ShouldBe($"₹{sold.NetAmount:N2}"),
            a => Detail(a, "Paid back").ShouldBe($"₹{sold.NetAmount:N2} back to the card (RRN or approval code {rrn})"),
            a => Detail(a, "Stock").ShouldStartWith("Goes back on the shelf"));

        // Nothing has moved: the invoice stands, nothing is paid back, the stock is where it was; the invoice shows the
        // request waiting, and tells the pharmacist who sold it that the sale is theirs.
        var waiting = await second.GetAsync<PharmacySaleDetail>(Sale(sold.Id));
        waiting.ShouldSatisfyAllConditions(
            s => s.Status.ShouldBe(SaleStatus.Paid),
            s => s.Payments.ShouldHaveSingleItem().IsRefund.ShouldBeFalse(),
            s => s.Pending.ShouldNotBeNull().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
                p => p.ApprovalId.ShouldBe(asked.Id), p => p.Kind.ShouldBe("pharmacy.sale_cancel"), p => p.AskedByYou.ShouldBeTrue()),
            s => s.OwnSale.ShouldBeFalse());
        var seen = await pharmacist.GetAsync<PharmacySaleDetail>(Sale(sold.Id));
        (seen.OwnSale, seen.Pending.ShouldNotBeNull().ShouldHaveSingleItem().AskedByYou).ShouldBe((true, false));
        (await AvailableAsync(pharmacist, medicine)).ShouldBe(onShelf);

        // A pharmacist does not approve refunds.
        (await RefusalAsync(second, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));

        // The accountant approves: the card is paid back under the sale's RRN, nothing comes out of the till, and the stock
        // is back, recorded by the accountant with the pharmacist who asked.
        var approved = await MakerChecker.ApproveAsync(accountant, asked);
        approved.Outcome.ShouldNotBeNull().ShouldContain("back to the card");
        var cancelled = await second.GetAsync<PharmacySaleDetail>(Sale(sold.Id));
        cancelled.Status.ShouldBe(SaleStatus.Cancelled);
        var refund = cancelled.Payments.Single(p => p.IsRefund);
        (refund.Mode, refund.Amount, refund.Reference).ShouldBe((PaymentMode.Card, sold.NetAmount, rrn));
        cancelled.Payments.ShouldNotContain(p => p.IsRefund && p.Mode == PaymentMode.Cash);
        (await AvailableAsync(pharmacist, medicine)).ShouldBe(onShelf + 2m);

        var accountantId = await UserIdAsync(accountant);
        var restocked = (await LedgerAsync(sold.Id, "pharmacy.cancel")).ShouldHaveSingleItem();
        restocked.ShouldSatisfyAllConditions(
            l => l.MovementType.ShouldBe(StockMovementType.Return),
            l => l.Quantity.ShouldBe(2m),
            l => l.UserId.ShouldBe(accountantId),
            l => l.Remarks.ShouldNotBeNull().ShouldContain($"asked for by {asked.RequestedByName}, approved by {approved.DecidedByName}"));
        var audited = await AuditAsync("pharmacy.sale_cancelled", sold.Id);
        audited.Summary.ShouldNotBeNull().ShouldContain($"Asked for by {asked.RequestedByName}, approved by {approved.DecidedByName}");
        audited.UserId.ShouldBe(accountantId);
    }

    [Fact]
    public async Task Whoever_sold_an_invoice_or_asked_for_its_cancellation_does_not_approve_it()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);

        // The hospital administrator, who may approve refunds, sold this one; a pharmacist asks to cancel it. The
        // administrator may not approve the refund of their own sale: the request waits for someone else.
        var adminsSale = await SellAsync(admin, medicine, 1m, PaymentMode.Cash, null);
        var asked = await pharmacist.PostAsync<ApprovalRequestDto>(Cancel(adminsSale.Id), new PharmacyReasonRequest("Entered twice"));
        (await RefusalAsync(admin, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "pharmacy.cancel_own_sale"));
        (await accountant.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}")).Status.ShouldBe(ApprovalStatus.Pending);
        (await admin.GetAsync<PharmacySaleDetail>(Sale(adminsSale.Id))).Status.ShouldBe(SaleStatus.Paid);

        // The administrator asks to cancel the pharmacist's sale, and may not approve what they asked for.
        var pharmacistsSale = await SellAsync(pharmacist, medicine, 1m, PaymentMode.Cash, null);
        var own = await admin.PostAsync<ApprovalRequestDto>(Cancel(pharmacistsSale.Id), new PharmacyReasonRequest("Customer changed their mind at the counter"));
        (await RefusalAsync(admin, $"{Approvals}/{own.Id}/approve", new ApproveRequest(null, own.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));

        // The accountant decides both (from what the requests say: the accountant does not open pharmacy invoices); the cash
        // goes back as cash.
        await MakerChecker.ApproveAsync(accountant, await accountant.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}"));
        await MakerChecker.ApproveAsync(accountant, own);
        foreach (var id in new[] { adminsSale.Id, pharmacistsSale.Id })
        {
            var sale = await pharmacist.GetAsync<PharmacySaleDetail>(Sale(id));
            sale.Status.ShouldBe(SaleStatus.Cancelled);
            sale.Payments.Single(p => p.IsRefund).Mode.ShouldBe(PaymentMode.Cash);
        }
    }

    [Fact]
    public async Task A_return_paying_cash_waits_for_approval_and_is_made_when_approved()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var second = await server.SignInAsync(HospitalServer.Users.SecondPharmacist);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var sold = await SellAsync(pharmacist, medicine, 3m, PaymentMode.Cash, null);
        var line = (await pharmacist.GetAsync<PharmacySaleDetail>(Sale(sold.Id))).Items.Single();
        var onShelf = await AvailableAsync(pharmacist, medicine);
        var request = new SaleReturnRequest([new SaleReturnLine(line.Id, 1m, true)], "Doctor stopped the course");

        (await RefusalAsync(pharmacist, Returns(sold.Id), request)).ShouldBe((HttpStatusCode.Forbidden, "pharmacy.return_own_sale"));

        using var response = await second.SendRawAsync(HttpMethod.Post, Returns(sold.Id), request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, Returns(sold.Id));
        var refund = Math.Round(medicine.Batch.Mrp, 2);
        asked.ShouldSatisfyAllConditions(
            a => a.Kind.ShouldBe("pharmacy.sale_return"),
            a => Detail(a, "Items returned").ShouldContain("× 1"),
            a => Detail(a, "Items returned").ShouldContain("back on the shelf"),
            a => Detail(a, "Refund").ShouldBe($"₹{refund:N2}"),
            a => Detail(a, "Paid back").ShouldBe($"₹{refund:N2} in cash"));

        // Nothing yet: no return, nothing paid back, the strip not on the shelf.
        (await second.GetAsync<List<SaleReturnDto>>(Returns(sold.Id))).ShouldBeEmpty();
        var waiting = await second.GetAsync<PharmacySaleDetail>(Sale(sold.Id));
        (waiting.ReturnedAmount, waiting.RefundedAmount, waiting.Pending.ShouldNotBeNull().ShouldHaveSingleItem().Kind).ShouldBe((0m, 0m, "pharmacy.sale_return"));
        (await AvailableAsync(pharmacist, medicine)).ShouldBe(onShelf);

        await MakerChecker.ApproveAsync(accountant, asked);
        var made = (await second.GetAsync<List<SaleReturnDto>>(Returns(sold.Id))).ShouldHaveSingleItem();
        (made.RefundAmount, made.CashRefund, made.Items.Single().Restocked).ShouldBe((refund, refund, true));
        var paidBack = (await second.GetAsync<PharmacySaleDetail>(Sale(sold.Id))).Payments.Single(p => p.IsRefund);
        (paidBack.Mode, paidBack.Amount).ShouldBe((PaymentMode.Cash, refund));
        (await AvailableAsync(pharmacist, medicine)).ShouldBe(onShelf + 1m);
    }

    [Fact]
    public async Task A_return_not_restocked_waits_even_when_nothing_is_paid_back_and_is_written_off_when_approved()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var second = await server.SignInAsync(HospitalServer.Users.SecondPharmacist);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var patientId = await RegisterPatientAsync(frontOffice);

        // Four strips on the patient's account: a return comes off the balance due, and no money goes out.
        var price = Math.Round(medicine.Batch.Mrp * 4m, MidpointRounding.AwayFromZero);
        var sold = await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", new CounterSaleRequest(
            medicine.StoreId, patientId, null, null, null, [new CounterSaleLine(medicine.ItemId, 4m, medicine.Batch.BatchId)], 0m,
            [new SalePayment(PaymentMode.Credit, price, null)], false));
        var line = (await pharmacist.GetAsync<PharmacySaleDetail>(Sale(sold.Id))).Items.Single();
        var onShelf = await AvailableAsync(pharmacist, medicine);

        // A strip back on the shelf, set against the due, is made at once.
        using (var atOnce = await second.SendRawAsync(HttpMethod.Post, Returns(sold.Id), new SaleReturnRequest([new SaleReturnLine(line.Id, 1m, true)], "Sealed strip back")))
        {
            atOnce.StatusCode.ShouldBe(HttpStatusCode.Created, await atOnce.Content.ReadAsStringAsync(Ct));
        }

        (await AvailableAsync(pharmacist, medicine)).ShouldBe(onShelf + 1m);

        // A strip that cannot go back on the shelf is a write-off: it waits, though nothing is paid back.
        using var response = await second.SendRawAsync(HttpMethod.Post, Returns(sold.Id),
            new SaleReturnRequest([new SaleReturnLine(line.Id, 1m, false)], "Strip crushed in the patient's bag"));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, Returns(sold.Id));
        asked.ShouldSatisfyAllConditions(
            a => Detail(a, "Items returned").ShouldContain("not restocked: written off"),
            a => Detail(a, "Paid back").ShouldBe("Nothing is paid back"),
            a => Detail(a, "Stock").ShouldStartWith("Not all of it goes back on the shelf"));
        (await second.GetAsync<List<SaleReturnDto>>(Returns(sold.Id))).Count.ShouldBe(1);
        var due = (await second.GetAsync<PharmacySaleDetail>(Sale(sold.Id))).DueAmount;

        await MakerChecker.ApproveAsync(accountant, asked);
        var writtenOff = (await second.GetAsync<List<SaleReturnDto>>(Returns(sold.Id))).OrderBy(r => r.ReturnedAt).Last();
        writtenOff.Items.Single().Restocked.ShouldBeFalse();
        (await second.GetAsync<PharmacySaleDetail>(Sale(sold.Id))).DueAmount.ShouldBe(due - writtenOff.RefundAmount);

        // It came back into its batch and went out again as a write-off: the ledger shows both, and the shelf is unchanged.
        var ledger = await LedgerAsync(writtenOff.Id, "pharmacy.return");
        ledger.Select(l => (l.MovementType, l.Quantity)).OrderBy(l => l.Quantity).ShouldBe([(StockMovementType.Adjustment, -1m), (StockMovementType.Return, 1m)]);
        ledger.ShouldAllBe(l => l.Remarks != null && l.Remarks.Contains("not restocked"));
        (await AvailableAsync(pharmacist, medicine)).ShouldBe(onShelf + 1m);
    }

    [Fact]
    public async Task An_approval_over_an_invoice_changed_since_it_was_asked_for_fails_and_changes_nothing()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var second = await server.SignInAsync(HospitalServer.Users.SecondPharmacist);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var sold = await SellAsync(pharmacist, medicine, 4m, PaymentMode.Upi, Digits(12));
        var line = (await pharmacist.GetAsync<PharmacySaleDetail>(Sale(sold.Id))).Items.Single();

        // A cancellation is asked for; then a return of one strip, which is approved first.
        var cancellation = await second.PostAsync<ApprovalRequestDto>(Cancel(sold.Id), new PharmacyReasonRequest("Wrong customer"));
        var returned = await second.PostAsync<ApprovalRequestDto>(Returns(sold.Id), new SaleReturnRequest([new SaleReturnLine(line.Id, 1m, true)], "One strip back"));
        await MakerChecker.ApproveAsync(accountant, returned);
        var afterReturn = await AvailableAsync(pharmacist, medicine);

        // The cancellation was asked for over the invoice before the return: approved now, it fails, and changes nothing.
        (await RefusalAsync(accountant, $"{Approvals}/{cancellation.Id}/approve", new ApproveRequest(null, cancellation.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "approval.stale"));
        (await accountant.GetAsync<ApprovalRequestDto>($"{Approvals}/{cancellation.Id}")).Status.ShouldBe(ApprovalStatus.Failed);
        var sale = await second.GetAsync<PharmacySaleDetail>(Sale(sold.Id));
        sale.Status.ShouldNotBe(SaleStatus.Cancelled);
        sale.Payments.Count(p => p.IsRefund).ShouldBe(1, "only the return's refund");
        sale.Payments.Single(p => p.IsRefund).Mode.ShouldBe(PaymentMode.Upi);
        (await AvailableAsync(pharmacist, medicine)).ShouldBe(afterReturn);
    }

    [Fact]
    public async Task A_ward_return_restocked_is_made_at_once_and_one_not_restocked_waits_for_another_pharmacist_and_enters_the_register()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var second = await server.SignInAsync(HospitalServer.Users.SecondPharmacist);
        var patientId = await RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patientId);
        var morphine = await DrugClassApprovalTests.CreateAsync(admin, "Morphine", DrugSchedule.X, controlled: true, highAlert: false);
        var store = (await admin.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == "IPPH").Id;
        await ReceiveAsync(admin, store, morphine);
        var issued = await pharmacist.PostAsync<IpIssueCreated>("/api/v1/pharmacy/ip-issues", new IpIssueRequest(
            store, null, null, patientId, admission.Id, admission.VisitId, [new IpIssueLineInput(morphine, 3m, null, null, null, null, false)],
            ConfirmControlledDrugs: true));
        var path = $"/api/v1/pharmacy/ip-issues/{issued.Id}";
        var line = (await pharmacist.GetAsync<IpIssueDetail>(path)).Lines.Single();

        // A sealed vial back on the shelf: made at once, as before.
        using (var sealedVial = await pharmacist.SendRawAsync(HttpMethod.Post, $"{path}/returns", new WardReturnRequest([new WardReturnLineInput(line.Id, 1m, true)], "Dose reduced")))
        {
            sealedVial.StatusCode.ShouldBe(HttpStatusCode.Created, await sealedVial.Content.ReadAsStringAsync(Ct));
        }

        // An opened vial not given: a write-off of a controlled drug, waiting for another pharmacist.
        using var response = await pharmacist.SendRawAsync(HttpMethod.Post, $"{path}/returns",
            new WardReturnRequest([new WardReturnLineInput(line.Id, 1m, false)], "Vial drawn up, dose not given"));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, $"{path}/returns");
        asked.ShouldSatisfyAllConditions(
            a => a.Kind.ShouldBe("pharmacy.ward_return"),
            a => Detail(a, "Items returned").ShouldContain("not restocked: written off"),
            a => Detail(a, "Controlled or high-alert").ShouldContain("controlled-drug register"),
            a => Detail(a, "Credited to the IP account").ShouldStartWith("₹"));
        var waiting = await pharmacist.GetAsync<IpIssueDetail>(path);
        (waiting.Lines.Single().ReturnedQuantity, waiting.Returns.Count, waiting.Pending.ShouldNotBeNull().ShouldHaveSingleItem().Kind).ShouldBe((1m, 1, "pharmacy.ward_return"));

        // The pharmacist who asked does not approve it; the second pharmacist does, and is named on the register.
        (await RefusalAsync(pharmacist, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        var approved = await MakerChecker.ApproveAsync(second, asked);
        var made = await pharmacist.GetAsync<IpIssueDetail>(path);
        made.Lines.Single().ReturnedQuantity.ShouldBe(2m);
        var writtenOff = made.Returns.OrderBy(r => r.ReturnedAt).Last();
        writtenOff.Lines.Single().Restocked.ShouldBeFalse();
        writtenOff.CreditAmount.ShouldBeGreaterThan(0m);

        var secondId = await UserIdAsync(second);
        var register = await RegisterAsync(writtenOff.Id);
        register.Select(r => (r.MovementType, r.QuantityIn, r.QuantityOut)).OrderBy(r => r.QuantityOut)
            .ShouldBe([(StockMovementType.Return, 1m, 0m), (StockMovementType.Adjustment, 0m, 1m)]);
        register.ShouldAllBe(r => r.RecordedByUserId == secondId && r.PatientId == patientId);
        register.ShouldAllBe(r => r.Remarks != null && r.Remarks.Contains($"asked for by {asked.RequestedByName}, approved by {approved.DecidedByName}"));
        (await AuditAsync("pharmacy.ward_return_written_off", issued.Id)).UserId.ShouldBe(secondId);
    }

    private static string Sale(Guid id) => $"/api/v1/pharmacy/sales/{id}";

    private static string Cancel(Guid id) => $"/api/v1/pharmacy/sales/{id}/cancel";

    private static string Returns(Guid id) => $"/api/v1/pharmacy/sales/{id}/returns";

    private static string Detail(ApprovalRequestDto request, string label) => request.Details.Single(d => d.Label == label).Value;

    private static Task<PharmacySaleCreated> SellAsync(ApiClient seller, OwnMedicine medicine, decimal strips, PaymentMode mode, string? reference) =>
        seller.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", new CounterSaleRequest(
            medicine.StoreId, null, "Walk-in customer", null, null, [new CounterSaleLine(medicine.ItemId, strips, medicine.Batch.BatchId)], 0m,
            [new SalePayment(mode, Math.Round(medicine.Batch.Mrp * strips, MidpointRounding.AwayFromZero), reference)], false));

    private static async Task<decimal> AvailableAsync(ApiClient pharmacist, OwnMedicine medicine) =>
        (await pharmacist.GetAsync<List<BatchStock>>($"/api/v1/pharmacy/stock/batches?storeId={medicine.StoreId}&itemId={medicine.ItemId}"))
            .Single(b => b.BatchId == medicine.Batch.BatchId).Available;

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (response.StatusCode, problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private static async Task<Guid> UserIdAsync(ApiClient user) => (await user.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();

    /// <summary>The stock ledger's movements of one pharmacy document: an invoice cancelled (<c>pharmacy.cancel</c>), a return.</summary>
    private async Task<List<StockLedgerEntry>> LedgerAsync(Guid referenceId, string module)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<StockLedgerEntry>().IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.ReferenceId == referenceId && l.ReferenceModule == module)
            .ToListAsync(Ct);
    }

    private async Task<List<ControlledDrugRegisterEntry>> RegisterAsync(Guid referenceId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ControlledDrugRegisterEntry>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.ReferenceId == referenceId)
            .ToListAsync(Ct);
    }

    private async Task<AuditEvent> AuditAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .SingleAsync(a => a.Action == action && a.EntityId == entityId.ToString(), Ct);
    }

    private static async Task<Guid> RegisterPatientAsync(ApiClient frontOffice) =>
        (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName("Lakshmi"), MiddleName: null, LastName: "Refund" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1965, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false))).Id;

    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var bed = await TestBeds.AddAsync(server, "SPW");
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Fracture of the right femur", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    private async Task ReceiveAsync(ApiClient storeKeeper, Guid storeId, Guid itemId)
    {
        var vendor = (await storeKeeper.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await GoodsReceipts.IntoStockAsync(server, storeKeeper, new CreateGoodsReceiptRequest(
            vendor.Id, storeId, null, $"INV-{Letters(10)}", today, today, null,
            [new GoodsReceiptLineInput(itemId, null, $"B{Letters(8)}", null, today.AddYears(2), 10m, 0m, 100m, 150m, 0m, null, AcceptShortExpiry: true)],
            SendForApproval: true));
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(i => i == 0 ? Random.Shared.Next(1, 10) : Random.Shared.Next(10)));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));
}
