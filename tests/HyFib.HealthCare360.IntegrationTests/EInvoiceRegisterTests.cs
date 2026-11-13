using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Irp;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.EInvoices;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// GST e-invoicing over the real API (GST rule 48(4)). The hospital in Chennai has e-invoicing from yesterday. A bill to
/// a company registered in Kerala, and a pharmacy B2B sale, wait in the register for an IRN; their e-invoice JSON goes to
/// the portal, and the IRN comes back with the signed QR code, which must name the invoice. The bill then prints its IRN
/// and QR code and cannot be cancelled here until the IRN is cancelled on the portal, within 24 hours.
/// </summary>
public sealed class EInvoiceRegisterTests(HospitalServer server)
{
    private const string Register = "/api/v1/billing/einvoices";
    private const string Hospital = EInvoicing.Hospital;
    private const string KeralaCompany = EInvoicing.KeralaCompany;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_bill_to_a_registered_company_gets_its_irn_recorded_printed_and_cancelled_within_the_day()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var service = await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            $"SV-{Letters(8)}", "Executive lounge access", null, ServiceCategory.Miscellaneous, null, 1000m, 18m, "999799", true, false, null, true, null));
        var company = await server.CreatePayerAsync(admin, new SavePayerRequest(
            $"CO{Letters(8).ToUpperInvariant()}", $"Malabar Traders {Letters(6).ToUpperInvariant()} Ltd", PayerCategory.Corporate, "Anil Menon", "9876543210",
            null, "4/12 MG Road, Ernakulam, Kochi, Kerala 682016", KeralaCompany, null, CreditDays: 30, DefaultCoPayPercent: 0m, PortalUrl: null,
            IsActive: true, Notes: null, Version: null));

        await using (await EInvoicingAsync(from: null))
        {
            var early = await BillAsync(company, service);
            var billing = await server.SignInAsync(HospitalServer.Users.Billing);
            using var off = await billing.SendRawAsync(HttpMethod.Get, $"{Register}/bill/{early.Id}");
            (await ProblemCodeAsync(off)).ShouldBe("einvoice.not_applicable");
            (await billing.GetAsync<EInvoiceRegister>(Register)).Applicable.ShouldBeFalse();
        }

        await using var _ = await EInvoicingAsync(from: Today().AddDays(-1));
        var bill = await BillAsync(company, service);
        var accounts = await server.SignInAsync(HospitalServer.Users.Billing);

        // The bill waits in the register, due to the portal within 30 days.
        var register = await accounts.GetAsync<EInvoiceRegister>(Register);
        register.Applicable.ShouldBeTrue();
        var row = register.Rows.Single(r => r.Id == bill.Id);
        (row.Source, row.Number, row.BuyerGstin, row.State, row.Total).ShouldBe(("bill", bill.BillNumber, KeralaCompany, EInvoiceState.Pending, bill.NetAmount));
        row.ReportBy.ShouldBe(row.Date.AddDays(30));

        // Its JSON is the portal's: Kerala is the place of supply, and the whole tax is IGST.
        var payload = await JsonAsync(accounts, $"{Register}/bill/{bill.Id}/json");
        var invoice = payload.AsArray().ShouldHaveSingleItem()!;
        invoice["DocDtls"]!["No"]!.GetValue<string>().ShouldBe(bill.BillNumber);
        (invoice["SellerDtls"]!["Gstin"]!.GetValue<string>(), invoice["SellerDtls"]!["Pin"]!.GetValue<int>()).ShouldBe((Hospital, 600018));
        (invoice["BuyerDtls"]!["Pos"]!.GetValue<string>(), invoice["BuyerDtls"]!["Loc"]!.GetValue<string>(), invoice["BuyerDtls"]!["Pin"]!.GetValue<int>())
            .ShouldBe(("32", "Kochi", 682016));
        invoice["ValDtls"]!["IgstVal"]!.GetValue<decimal>().ShouldBe(bill.IgstAmount);
        invoice["ValDtls"]!["TotInvVal"]!.GetValue<decimal>().ShouldBe(bill.NetAmount);
        var lines = invoice["ItemList"]!.AsArray().Count;
        lines.ShouldBe(bill.Lines.Count);

        // A signed QR code for another invoice is refused; the bill's own is recorded with the acknowledgement.
        var detail = await accounts.GetAsync<EInvoiceDetail>($"{Register}/bill/{bill.Id}");
        detail.Problems.ShouldBeEmpty();
        using var wrong = await accounts.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{bill.Id}/irn",
            new RecordIrnRequest("112610012345678", Qr(bill.BillNumber!, bill.NetAmount + 1m, KeralaCompany, Irn(), lines)));
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await wrong.Content.ReadAsStringAsync(Ct)).ShouldContain("not this invoice's");

        var irn = Irn();
        var recorded = await accounts.PostAsync<EInvoiceDetail>($"{Register}/bill/{bill.Id}/irn",
            new RecordIrnRequest("112610012345678", Qr(bill.BillNumber!, bill.NetAmount, KeralaCompany, irn, lines)));
        recorded.State.ShouldBe(EInvoiceState.Registered);
        var registration = recorded.Registration.ShouldNotBeNull();
        (registration.Irn, registration.AckNo, registration.CanCancel).ShouldBe((irn, "112610012345678", true));
        registration.QrCodeSvg.ShouldStartWith("data:image/svg+xml;base64,");
        (await accounts.GetAsync<EInvoiceRegister>(Register)).Rows.Single(r => r.Id == bill.Id).Irn.ShouldBe(irn);

        // The same IRN cannot be recorded twice, nor a second one while the first is live.
        using var again = await accounts.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{bill.Id}/irn",
            new RecordIrnRequest("112610012345679", Qr(bill.BillNumber!, bill.NetAmount, KeralaCompany, Irn(), lines)));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // The bill carries its IRN and the signed QR code, on screen and in print.
        var printed = await accounts.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}");
        printed.EInvoice.ShouldNotBeNull().Irn.ShouldBe(irn);
        var document = await accounts.GetAsync<PrintableDocumentDto>($"/api/v1/billing/bills/{bill.Id}/document");
        document.Html.ShouldContain(irn);
        document.Html.ShouldContain("112610012345678");
        document.Html.ShouldContain("einvoice-qr");

        // Cancelling the bill waits for the IRN to be cancelled on the portal.
        using var refused = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/bills/{bill.Id}/cancel", new CancelBillRequest("Billed to the wrong company", printed.Version));
        (await ProblemCodeAsync(refused)).ShouldBe("einvoice.active");

        var cancelled = await accounts.PostAsync<EInvoiceDetail>($"{Register}/bill/{bill.Id}/irn/cancel",
            new CancelIrnRequest(EInvoiceCancelReason.DataEntryMistake, "Billed to the wrong company", registration.Version));
        cancelled.State.ShouldBe(EInvoiceState.IrnCancelled);
        cancelled.Registration!.Status.ShouldBe(EInvoiceStatus.Cancelled);
        (await accounts.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}")).EInvoice.ShouldBeNull();
    }

    [Fact]
    public async Task A_pharmacy_b2b_sale_needs_the_buyers_pin_code_and_gets_its_irn_like_a_bill()
    {
        await using var _ = await EInvoicingAsync(from: Today().AddDays(-1));
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        // A medicine of the test's own: other tests sell from the seeded shelf at the same time.
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        async Task<PharmacySaleCreated> SellAsync(string? address) => await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter",
            new CounterSaleRequest(medicine.StoreId, null, "Sree Clinic", null, null, [new CounterSaleLine(medicine.ItemId, 1m, medicine.Batch.BatchId)], 0m,
                [new SalePayment(PharmacyPaymentMode.Cash, Math.Round(medicine.Batch.Mrp, MidpointRounding.AwayFromZero), null)], false,
                new B2bBuyer(KeralaCompany, "Sree Clinic", "32", address)));

        var withoutAddress = await SellAsync(null);
        var problems = (await pharmacist.GetAsync<EInvoiceDetail>($"{Register}/pharmacy/{withoutAddress.Id}")).Problems;
        problems.ShouldContain("Enter the buyer's address, with its PIN code.");
        using var refused = await pharmacist.SendRawAsync(HttpMethod.Get, $"{Register}/pharmacy/{withoutAddress.Id}/json");
        (await ProblemCodeAsync(refused)).ShouldBe("einvoice.invalid");

        var sold = await SellAsync("Sree Clinic, 22 Beach Road, Kozhikode 673001");
        var invoice = (await JsonAsync(pharmacist, $"{Register}/pharmacy/{sold.Id}/json")).AsArray().ShouldHaveSingleItem()!;
        var item = invoice["ItemList"]![0]!;
        (item["IsServc"]!.GetValue<string>(), item["Unit"]!.GetValue<string>()).ShouldBe(("N", "NOS"));
        item["IgstAmt"]!.GetValue<decimal>().ShouldBeGreaterThan(0m);
        invoice["ValDtls"]!["TotInvVal"]!.GetValue<decimal>().ShouldBe(sold.NetAmount);

        var irn = Irn();
        await pharmacist.PostAsync<EInvoiceDetail>($"{Register}/pharmacy/{sold.Id}/irn",
            new RecordIrnRequest("112610087654321", Qr(invoice["DocDtls"]!["No"]!.GetValue<string>(), sold.NetAmount, KeralaCompany, irn, 1)));
        var sale = await pharmacist.GetAsync<PharmacySaleDetail>($"/api/v1/pharmacy/sales/{sold.Id}");
        sale.EInvoice.ShouldNotBeNull().Irn.ShouldBe(irn);
        sale.BuyerAddress.ShouldBe("Sree Clinic, 22 Beach Road, Kozhikode 673001");
        // Asked for by another pharmacist (whoever sold it may not ask), the cancellation is refused while the IRN stands.
        var secondPharmacist = await server.SignInAsync(HospitalServer.Users.SecondPharmacist);
        using var cancel = await secondPharmacist.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/sales/{sold.Id}/cancel", new { Reason = "Wrong buyer" });
        (await ProblemCodeAsync(cancel)).ShouldBe("einvoice.active");
    }

    [Fact]
    public async Task A_cancellation_whose_answer_is_lost_is_read_back_from_the_portal_before_anyone_is_told_to_try_again()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var service = await TaxedServiceAsync(admin);
        var company = await CompanyAsync(admin);
        await using var einvoicing = await EInvoicingAsync(from: Today().AddDays(-1));
        await using var portal = await server.IrpPortalAsync(enabled: true);
        var ledger = server.Services.GetRequiredService<SimulatorIrpLedger>();
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        // The portal cancels the IRN and its answer is lost: HC360 asks how the portal holds the IRN, and records the
        // cancellation at the portal's time.
        var first = await BillAsync(company, service);
        var live = (await billing.IrnFromThePortalAsync("bill", first.Id, Ct)).Registration!;
        using (ledger.Fail(SimulatorIrpOperations.Cancel, SimulatorIrpFault.LoseAnswer, irn: live.Irn))
        {
            var cancelled = await billing.PostAsync<EInvoiceDetail>($"{Register}/bill/{first.Id}/irn/cancel", Cancel(live.Version));
            cancelled.State.ShouldBe(EInvoiceState.IrnCancelled);
            cancelled.Registration!.CancelledAt!.Value.ShouldBe(ledger.Entries[live.Irn].CancelledAt!.Value, TimeSpan.FromMilliseconds(1));
        }

        // Its answer lost, and the portal not saying how it holds the IRN either: the outcome is not known, and staff are not
        // told that nothing was done. Tried again, the portal refuses the IRN as no longer active; HC360 finds it cancelled
        // there and records it.
        var second = await BillAsync(company, service);
        live = (await billing.IrnFromThePortalAsync("bill", second.Id, Ct)).Registration!;
        using (ledger.Fail(SimulatorIrpOperations.Cancel, SimulatorIrpFault.LoseAnswer, irn: live.Irn))
        using (ledger.Fail(SimulatorIrpOperations.Get, SimulatorIrpFault.Down, irn: live.Irn))
        {
            using var unknown = await billing.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{second.Id}/irn/cancel", Cancel(live.Version));
            (await ProblemCodeAsync(unknown)).ShouldBe("einvoice.irp_cancel_unknown");
        }

        ledger.Entries[live.Irn].Cancelled.ShouldBeTrue("the portal did cancel it");
        (await billing.PostAsync<EInvoiceDetail>($"{Register}/bill/{second.Id}/irn/cancel", Cancel(live.Version))).State.ShouldBe(EInvoiceState.IrnCancelled);

        // Not reached, the portal still holds the IRN as active: nothing was cancelled, and staff are told so.
        var third = await BillAsync(company, service);
        live = (await billing.IrnFromThePortalAsync("bill", third.Id, Ct)).Registration!;
        using (ledger.Fail(SimulatorIrpOperations.Cancel, SimulatorIrpFault.Down, irn: live.Irn))
        {
            using var down = await billing.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{third.Id}/irn/cancel", Cancel(live.Version));
            (await ProblemCodeAsync(down)).ShouldBe("einvoice.irp_unreachable");
            (await down.Content.ReadAsStringAsync(Ct)).ShouldContain($"The portal still holds IRN {live.Irn} as active, so nothing was cancelled.");
        }

        // A cancellation made in time with its answer lost is found on the portal even when tried again after the 24 hours.
        using (ledger.Fail(SimulatorIrpOperations.Cancel, SimulatorIrpFault.LoseAnswer, irn: live.Irn))
        using (ledger.Fail(SimulatorIrpOperations.Get, SimulatorIrpFault.Down, irn: live.Irn))
        {
            using var lost = await billing.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{third.Id}/irn/cancel", Cancel(live.Version));
            (await ProblemCodeAsync(lost)).ShouldBe("einvoice.irp_cancel_unknown");
        }

        await server.BackdateIrnAsync(live.Irn, TimeSpan.FromHours(25), Ct);
        var late = (await billing.GetAsync<EInvoiceDetail>($"{Register}/bill/{third.Id}")).Registration!;
        late.CanCancel.ShouldBeFalse();
        (await billing.PostAsync<EInvoiceDetail>($"{Register}/bill/{third.Id}/irn/cancel", Cancel(late.Version))).State.ShouldBe(EInvoiceState.IrnCancelled);
    }

    [Fact]
    public async Task Connected_to_the_portal_an_irn_recorded_by_hand_must_be_one_the_portal_holds_active_for_the_document()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var service = await TaxedServiceAsync(admin);
        var company = await CompanyAsync(admin);
        await using var einvoicing = await EInvoicingAsync(from: Today().AddDays(-10));
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        // Bills finalized before the hospital connected to the portal: their IRNs are then generated on the portal by hand.
        var bills = new List<BillDetail>();
        for (var i = 0; i < 4; i++)
        {
            var bill = await BillAsync(company, service);
            await server.EInvoiceQueueSettledAsync(bill.Id, Ct);
            bills.Add(bill);
        }

        await using var portal = await server.IrpPortalAsync(enabled: true);
        var ledger = server.Services.GetRequiredService<SimulatorIrpLedger>();

        // Generated on the portal and recorded here: the portal holds it, active, for this bill; its signed invoice is kept.
        var confirmed = bills[0];
        var onThePortal = await OnThePortalAsync(billing, confirmed);
        var recorded = await billing.PostAsync<EInvoiceDetail>($"{Register}/bill/{confirmed.Id}/irn", new RecordIrnRequest(onThePortal.AckNo, onThePortal.SignedQrCode));
        (recorded.State, recorded.Registration!.Irn, recorded.Registration.Provider).ShouldBe((EInvoiceState.Registered, onThePortal.Irn, EInvoice.ManualProvider));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<EInvoice>().SingleAsync(e => e.Irn == onThePortal.Irn, Ct))
                .SignedInvoice.ShouldBe(onThePortal.SignedInvoice);
        }

        // Cancelled on the portal since: it is not recorded as live.
        var cancelled = bills[1];
        var gone = await OnThePortalAsync(billing, cancelled);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IInvoiceRegistrationPortal>()
                .CancelAsync(cancelled.FacilityId, EInvoicing.Hospital, gone.Irn, 2, "Wrong company", Ct)).IsSuccess.ShouldBeTrue();
        }

        using (var refused = await billing.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{cancelled.Id}/irn", new RecordIrnRequest(gone.AckNo, gone.SignedQrCode)))
        {
            (await ProblemCodeAsync(refused)).ShouldBe("einvoice.irp_unconfirmed");
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("The portal holds it as cancelled.");
        }

        // An IRN the portal does not hold, and one it could not be asked about: neither is recorded.
        var unknown = bills[2];
        var madeUp = Irn();
        var lines = unknown.Lines.Count;
        using (var notHeld = await billing.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{unknown.Id}/irn",
                   new RecordIrnRequest("112610012345678", Qr(unknown.BillNumber!, unknown.NetAmount, KeralaCompany, madeUp, lines))))
        {
            (await ProblemCodeAsync(notHeld)).ShouldBe("einvoice.irp_unconfirmed");
        }

        using (ledger.Fail(SimulatorIrpOperations.Get, SimulatorIrpFault.Down, irn: madeUp))
        {
            using var down = await billing.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{unknown.Id}/irn",
                new RecordIrnRequest("112610012345678", Qr(unknown.BillNumber!, unknown.NetAmount, KeralaCompany, madeUp, lines)));
            (await ProblemCodeAsync(down)).ShouldBe("einvoice.irp_unreachable");
        }

        (await billing.GetAsync<EInvoiceDetail>($"{Register}/bill/{unknown.Id}")).State.ShouldBe(EInvoiceState.Pending);

        // Generated longer ago than the portal gives an IRN's details: recorded on its signed QR code, the portal not asked.
        var older = bills[3];
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Bill>().Where(b => b.Id == older.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.FinalizedAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-5)), Ct);
        }

        var olderDate = (await billing.GetAsync<EInvoiceDetail>($"{Register}/bill/{older.Id}")).Date;
        var unasked = Irn();
        var late = await billing.PostAsync<EInvoiceDetail>($"{Register}/bill/{older.Id}/irn", new RecordIrnRequest("112610012345679",
            EInvoicing.Qr(older.BillNumber!, older.NetAmount, KeralaCompany, unasked, older.Lines.Count, date: olderDate, generatedAt: DateTimeOffset.UtcNow.AddDays(-3))));
        (late.State, late.Registration!.Irn).ShouldBe((EInvoiceState.Registered, unasked));
        ledger.Entries.ContainsKey(unasked).ShouldBeFalse("the portal never held it, and was not asked");
    }

    [Fact]
    public async Task A_b2b_bill_gets_its_irn_on_its_own_once_final_and_a_debit_note_against_it_waits_for_it()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var service = await TaxedServiceAsync(admin);
        var company = await CompanyAsync(admin);
        await using var einvoicing = await EInvoicingAsync(from: Today().AddDays(-1));
        await using var portal = await server.IrpPortalAsync(enabled: true);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);

        // Finalized, a bill to a company registered for GST gets its IRN with nobody asking: the outbox asks the portal.
        var bill = await BillAsync(company, service);
        var registered = await billing.EInvoiceWhenAsync("bill", bill.Id, d => d.State == EInvoiceState.Registered, "the bill's IRN", Ct);
        (registered.Registration!.Provider, registered.Registration.RecordedByName).ShouldBe(("simulator", EInvoiceAutomation.RecordedBy));
        (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}")).EInvoice.ShouldNotBeNull().Irn.ShouldBe(registered.Registration.Irn);

        // One the portal would refuse (the company's address has no PIN code) is not asked for again: the billing desk is told
        // at once, and the bill waits in the register.
        var withoutPin = await CompanyAsync(admin, address: "4/12 MG Road, Ernakulam, Kochi");
        var refused = await BillAsync(withoutPin, service);
        var alert = await EventuallyAsync(async () => (await billing.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=100"))
            .Items.FirstOrDefault(n => n.Title == $"Invoice {refused.BillNumber} did not get its IRN"), "the alert of the bill the portal would refuse");
        (alert.Severity, alert.Link).ShouldBe(("warning", "/billing/einvoices"));
        alert.Body.ShouldNotBeNull().ShouldContain("PIN code");
        (await billing.GetAsync<EInvoiceDetail>($"{Register}/bill/{refused.Id}")).State.ShouldBe(EInvoiceState.Pending);

        // A debit note on it meanwhile waits for the bill's IRN, and goes on its own once the bill has one.
        var line = refused.Lines.Single(l => l.ServiceItemId == service.Id);
        await MakerChecker.ApproveAsync(accountant, await AskAsync(billing, $"/api/v1/billing/bills/{refused.Id}/debit-notes",
            new IssueDebitNoteRequest(DebitNoteParty.Payer, "Lounge charged at last year's rate", [new DebitNoteLineRequest(line.Id, 200m)])), "Rate card checked");
        var note = (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{refused.Id}")).DebitNotes.ShouldNotBeNull().ShouldHaveSingleItem();
        await EventuallyAsync(async () => await QueuedAsync(note.Id) is { Attempts: > 0, LastError: { } why } && why.Contains("has no IRN yet", StringComparison.Ordinal)
            ? "waiting" : null, "the debit note waiting for its bill's IRN");

        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Payer>().Where(p => p.Id == withoutPin.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Address, "4/12 MG Road, Ernakulam, Kochi, Kerala 682016"), Ct);
        }

        await billing.PostAsync<EInvoiceDetail>($"{Register}/bill/{refused.Id}/irn/generate");
        var debit = await billing.EInvoiceWhenAsync("debitnote", note.Id, d => d.State == EInvoiceState.Registered, "the debit note's IRN", Ct, TimeSpan.FromMinutes(3));
        (debit.Type, debit.Against, debit.Registration!.RecordedByName).ShouldBe(("DBN", refused.BillNumber, EInvoiceAutomation.RecordedBy));
    }

    private static CancelIrnRequest Cancel(uint version) => new(EInvoiceCancelReason.DataEntryMistake, "Billed to the wrong company", version);

    private async Task<ServiceItemDto> TaxedServiceAsync(ApiClient admin) => await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
        $"SV-{Letters(8)}", "Executive lounge access", null, ServiceCategory.Miscellaneous, null, 1000m, 18m, "999799", true, false, null, true, null));

    private async Task<PayerDetail> CompanyAsync(ApiClient admin, string address = "4/12 MG Road, Ernakulam, Kochi, Kerala 682016") =>
        await server.CreatePayerAsync(admin, new SavePayerRequest(
            $"CO{Letters(8).ToUpperInvariant()}", $"Malabar Traders {Letters(6).ToUpperInvariant()} Ltd", PayerCategory.Corporate, "Anil Menon", "9876543210",
            null, address, KeralaCompany, null, CreditDays: 30, DefaultCoPayPercent: 0m, PortalUrl: null, IsActive: true, Notes: null, Version: null));

    /// <summary>The document's IRN generated on the portal by hand, as the hospital does on the portal's own screens: HC360 records nothing.</summary>
    private async Task<IrpIrn> OnThePortalAsync(ApiClient client, BillDetail bill)
    {
        var payload = (await JsonAsync(client, $"{Register}/bill/{bill.Id}/json")).AsArray().ShouldHaveSingleItem()!.AsObject();
        await using var scope = server.Services.CreateAsyncScope();
        var generated = await scope.ServiceProvider.GetRequiredService<IInvoiceRegistrationPortal>()
            .GenerateAsync(bill.FacilityId, EInvoicing.Hospital, (JsonObject)payload.DeepClone(), Ct);
        return generated.Value.ShouldNotBeNull();
    }

    /// <summary>The outbox's request for a document's IRN, as the outbox holds it.</summary>
    private async Task<OutboxMessage?> QueuedAsync(Guid documentId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>()
            .FromSqlInterpolated($"SELECT * FROM platform.outbox_messages WHERE type = {EInvoiceGenerateHandler.MessageType} AND payload->>'id' = {documentId.ToString()}")
            .AsNoTracking().SingleOrDefaultAsync(Ct);
    }

    private static async Task<ApprovalRequestDto> AskAsync(ApiClient maker, string path, object request)
    {
        using var response = await maker.SendRawAsync(HttpMethod.Post, path, request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
    }

    /// <summary>Polls until the outbox has delivered (it runs every two seconds), for at most a minute.</summary>
    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> read, string failure)
        where T : class
    {
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromMinutes(1); waited += TimeSpan.FromSeconds(1))
        {
            if (await read() is { } value)
            {
                return value;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        throw new ShouldAssertException(failure);
    }

    /// <summary>A corporate visit with one charge of <paramref name="service"/>, billed and finalized by the billing desk.</summary>
    private async Task<BillDetail> BillAsync(PayerDetail company, ServiceItemDto service)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Arun"), MiddleName: null, LastName: "Invoice" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1980, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        await server.VerifiedPolicyAsync(patient.Id, company.Id);
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patient.Id, VisitType.HealthCheckup, null, null, null, PayerType.Corporate, company.Id, ReferralSource.Corporate, null, null, false, null, null,
            TokenPriority.Normal, IssueToken: false, NotifyPatient: false));

        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(
            patient.Id, visit.Id, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));
        return await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
    }

    private Task<IAsyncDisposable> EInvoicingAsync(DateOnly? from) => server.EInvoicingAsync(from, Ct);

    private static async Task<JsonNode> JsonAsync(ApiClient client, string path)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    private static string Qr(string number, decimal total, string buyer, string irn, int items) => EInvoicing.Qr(number, total, buyer, irn, items);

    private static string Irn() => EInvoicing.Irn();

    private static DateOnly Today() => EInvoicing.Today();

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string Letters(int count) => new(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
}
