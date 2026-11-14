using System.Globalization;
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
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// GST compliance over the real API, with the Invoice Registration Portal's training simulator: the register asks the
/// portal for a bill's IRN and records the signed QR code it answers (once, however often it asks); the portal cancels an
/// IRN only within 24 hours; a pharmacy B2B invoice above the threshold gets its e-way bill from its IRN, once, its lost
/// answer read back from the portal; a debit note
/// raises a corporate's bill and is a DBN e-invoice; a payer credit note credits the GST of what the payer disallowed;
/// GSTR-1 lists them; and a GST invoice series refuses a format longer than 16 characters.
/// </summary>
public sealed class GstComplianceTests(HospitalServer server)
{
    private const string Register = "/api/v1/billing/einvoices";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_portal_gives_a_bill_its_irn_once_and_cancels_it_only_within_24_hours()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var service = await TaxedServiceAsync(admin);
        var company = await CompanyAsync(admin);
        await using var einvoicing = await server.EInvoicingAsync(EInvoicing.Today().AddDays(-1), Ct);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        // Not connected: the IRN is asked for nowhere (the request queued as the bill was finalized ends there), and the
        // hospital is told to upload the JSON by hand.
        var first = await BillAsync(company, service);
        await server.EInvoiceQueueSettledAsync(first.Id, Ct);
        await using (await server.IrpPortalAsync(enabled: false))
        {
            using var off = await billing.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{first.Id}/irn/generate");
            (await CodeAsync(off)).ShouldBe("einvoice.irp_not_connected");
        }

        await using var portal = await server.IrpPortalAsync(enabled: true);
        (await billing.GetAsync<EInvoiceDetail>($"{Register}/bill/{first.Id}")).PortalConnected.ShouldBeTrue();
        var registered = await billing.PostAsync<EInvoiceDetail>($"{Register}/bill/{first.Id}/irn/generate");
        registered.State.ShouldBe(EInvoiceState.Registered);
        var irn = registered.Registration.ShouldNotBeNull();
        irn.Provider.ShouldBe("simulator");
        irn.Irn.ShouldBe(SimulatorIrp.IrnOf(EInvoicing.Hospital, registered.Date, "INV", first.BillNumber!), "the portal's IRN is the hash of GSTIN, year, type and number");
        (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{first.Id}")).EInvoice.ShouldNotBeNull().Irn.ShouldBe(irn.Irn);

        // Asked again while it stands: refused here. Asked again after the answer was lost (the row is gone): the portal
        // says it already holds the invoice (2150), and the same IRN is fetched and recorded, not a second one.
        using (var again = await billing.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{first.Id}/irn/generate"))
        {
            (await CodeAsync(again)).ShouldBe("einvoice.registered");
        }

        await WithDbAsync(db => db.Set<EInvoice>().Where(e => e.SourceId == first.Id).ExecuteDeleteAsync(Ct));
        (await billing.PostAsync<EInvoiceDetail>($"{Register}/bill/{first.Id}/irn/generate")).Registration!.Irn.ShouldBe(irn.Irn);

        // A day and an hour later the portal no longer cancels it: a credit note undoes the invoice instead.
        var late = await billing.GetAsync<EInvoiceDetail>($"{Register}/bill/{first.Id}");
        await server.BackdateIrnAsync(irn.Irn, TimeSpan.FromHours(25), Ct);
        late = await billing.GetAsync<EInvoiceDetail>($"{Register}/bill/{first.Id}");
        late.Registration!.CanCancel.ShouldBeFalse();
        using (var refused = await billing.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{first.Id}/irn/cancel",
                   new CancelIrnRequest(EInvoiceCancelReason.DataEntryMistake, "Wrong company", late.Registration.Version)))
        {
            (await CodeAsync(refused)).ShouldBe("einvoice.cancel_window");
        }

        // Within the day, the IRN is cancelled on the portal and here.
        var second = await BillAsync(company, service);
        var live = (await billing.IrnFromThePortalAsync("bill", second.Id, Ct)).Registration!;
        var cancelled = await billing.PostAsync<EInvoiceDetail>($"{Register}/bill/{second.Id}/irn/cancel",
            new CancelIrnRequest(EInvoiceCancelReason.DataEntryMistake, "Billed to the wrong company", live.Version));
        cancelled.State.ShouldBe(EInvoiceState.IrnCancelled);
        server.Services.GetRequiredService<SimulatorIrpLedger>().Entries[live.Irn].Cancelled.ShouldBeTrue("the portal holds it as cancelled too");
    }

    [Fact]
    public async Task A_pharmacy_b2b_invoice_above_the_threshold_gets_its_e_way_bill_from_its_irn()
    {
        await using var einvoicing = await server.EInvoicingAsync(EInvoicing.Today().AddDays(-1), Ct);
        await using var portal = await server.IrpPortalAsync(enabled: true);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var sold = await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter",
            new CounterSaleRequest(medicine.StoreId, null, "Sree Clinic", null, null, [new CounterSaleLine(medicine.ItemId, 1m, medicine.Batch.BatchId)], 0m,
                [new SalePayment(PharmacyPaymentMode.Cash, Math.Round(medicine.Batch.Mrp, MidpointRounding.AwayFromZero), null)], false,
                new B2bBuyer(EInvoicing.KeralaCompany, "Sree Clinic", "32", "Sree Clinic, 22 Beach Road, Kozhikode 673001")));
        // The sale gets its IRN on its own once it is saved: the outbox asks the portal.
        var registered = await pharmacist.EInvoiceWhenAsync("pharmacy", sold.Id, d => d.State == EInvoiceState.Registered, "the sale's IRN", Ct);
        registered.Registration!.RecordedByName.ShouldBe(EInvoiceAutomation.RecordedBy);
        var request = new GenerateEwayBillRequest(450, TransportMode.Road, "KL11AB1234", null, null, null, null);

        // One strip is far below ₹50,000: it moves without an e-way bill.
        using (var below = await pharmacist.SendRawAsync(HttpMethod.Post, $"{Register}/pharmacy/{sold.Id}/ewaybill", request))
        {
            (await CodeAsync(below)).ShouldBe("einvoice.ewaybill_not_needed");
        }

        await using var threshold = await SettingAsync(EInvoiceService.EwayBillThresholdSetting, 1m);
        var detail = await pharmacist.PostAsync<EInvoiceDetail>($"{Register}/pharmacy/{sold.Id}/ewaybill", request);
        var registration = detail.Registration.ShouldNotBeNull();
        registration.EwbNo.ShouldNotBeNull();
        // 450 km at 200 km a day: valid to the end of the third day.
        var issued = registration.EwbDate!.Value.ToOffset(TimeSpan.FromHours(5.5));
        registration.EwbValidTill!.Value.ToOffset(TimeSpan.FromHours(5.5)).Date.ShouldBe(issued.Date.AddDays(2));

        using var twice = await pharmacist.SendRawAsync(HttpMethod.Post, $"{Register}/pharmacy/{sold.Id}/ewaybill", request);
        (await CodeAsync(twice)).ShouldBe("einvoice.ewaybill_exists");
    }

    [Fact]
    public async Task An_e_way_bill_whose_answer_is_lost_is_read_back_from_the_portal_and_recorded_once_even_on_retry()
    {
        await using var einvoicing = await server.EInvoicingAsync(EInvoicing.Today().AddDays(-1), Ct);
        await using var portal = await server.IrpPortalAsync(enabled: true);
        await using var threshold = await SettingAsync(EInvoiceService.EwayBillThresholdSetting, 1m);
        var ledger = server.Services.GetRequiredService<SimulatorIrpLedger>();
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var request = new GenerateEwayBillRequest(450, TransportMode.Road, "KL11AB1234", null, null, null, null);

        // The portal generates the e-way bill and its answer is lost: HC360 asks it for the IRN's e-way bill and records that.
        var (first, firstIrn) = await B2bSaleWithIrnAsync(pharmacist, medicine);
        using (ledger.Fail(SimulatorIrpOperations.EwayBill, SimulatorIrpFault.LoseAnswer, irn: firstIrn))
        {
            var recorded = await pharmacist.PostAsync<EInvoiceDetail>($"{Register}/pharmacy/{first}/ewaybill", request);
            recorded.Registration!.EwbNo.ShouldBe(ledger.Entries[firstIrn].Irn.EwbNo.ShouldNotBeNull());
        }

        // Not reached: the portal holds no e-way bill for the IRN, and staff are told none was generated.
        var (second, irn) = await B2bSaleWithIrnAsync(pharmacist, medicine);
        var path = $"{Register}/pharmacy/{second}/ewaybill";
        using (ledger.Fail(SimulatorIrpOperations.EwayBill, SimulatorIrpFault.Down, irn: irn))
        {
            using var down = await pharmacist.SendRawAsync(HttpMethod.Post, path, request);
            (await CodeAsync(down)).ShouldBe("einvoice.irp_unreachable");
            (await down.Content.ReadAsStringAsync(Ct)).ShouldContain($"holds no e-way bill for IRN {irn}");
        }

        ledger.Entries[irn].Irn.EwbNo.ShouldBeNull();

        // Generated with its answer lost, and the portal not answering when asked for it either: the outcome is not known,
        // and staff are not told that nothing was done.
        using (ledger.Fail(SimulatorIrpOperations.EwayBill, SimulatorIrpFault.LoseAnswer, irn: irn))
        using (ledger.Fail(SimulatorIrpOperations.GetEwayBill, SimulatorIrpFault.Down, irn: irn))
        {
            using var unknown = await pharmacist.SendRawAsync(HttpMethod.Post, path, request);
            (await CodeAsync(unknown)).ShouldBe("einvoice.irp_ewaybill_unknown");
        }

        var made = ledger.Entries[irn].Irn.EwbNo.ShouldNotBeNull("the portal did generate it");
        (await pharmacist.GetAsync<EInvoiceDetail>($"{Register}/pharmacy/{second}")).Registration!.EwbNo.ShouldBeNull();

        // Tried again: the portal refuses a second e-way bill for the IRN (4002), and HC360 records the one it holds.
        var retried = (await pharmacist.PostAsync<EInvoiceDetail>(path, request)).Registration!;
        (retried.EwbNo, retried.EwbValidTill).ShouldBe((made, ledger.Entries[irn].Irn.EwbValidTill));
        ledger.Entries[irn].Irn.EwbNo.ShouldBe(made, "the portal still holds the one e-way bill");
    }

    [Fact]
    public async Task An_e_way_bill_generated_outside_hc360_is_recorded_by_hand_after_the_checks_the_portal_can_answer()
    {
        await using var einvoicing = await server.EInvoicingAsync(EInvoicing.Today().AddDays(-1), Ct);
        await using var portal = await server.IrpPortalAsync(enabled: true);
        await using var threshold = await SettingAsync(EInvoiceService.EwayBillThresholdSetting, 1m);
        var ledger = server.Services.GetRequiredService<SimulatorIrpLedger>();
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var now = DateTimeOffset.UtcNow;

        // Generated on the e-way bill portal: the e-invoice portal holds no e-way bill for the IRN (4005), so it is recorded
        // on its print, and the audit trail says so.
        var (first, _) = await B2bSaleWithIrnAsync(pharmacist, medicine);
        var printed = EwayBillNumber();
        var recorded = (await pharmacist.PostAsync<EInvoiceDetail>($"{Register}/pharmacy/{first}/ewaybill/record",
            new RecordEwayBillRequest(printed, now.AddMinutes(-30), now.AddDays(1)))).Registration!;
        recorded.EwbNo.ShouldBe(long.Parse(printed, CultureInfo.InvariantCulture));
        recorded.EwbValidTill!.Value.ShouldBe(now.AddDays(1), TimeSpan.FromSeconds(1));
        (await AuditAsync("billing.einvoice.ewaybill_recorded", printed)).ShouldContain("holds no e-way bill for the invoice's IRN");

        // Recorded once: not again on another invoice.
        var (second, secondIrn) = await B2bSaleWithIrnAsync(pharmacist, medicine);
        using (var twice = await pharmacist.SendRawAsync(HttpMethod.Post, $"{Register}/pharmacy/{second}/ewaybill/record",
                   new RecordEwayBillRequest(printed, now.AddMinutes(-30), null)))
        {
            (await CodeAsync(twice)).ShouldBe("einvoice.ewaybill_number_exists");
        }

        // Generated on the e-invoice portal's own screens, so the portal holds it for the IRN: a mistyped number is refused
        // with the portal's own, and the right one is recorded with the portal's dates, whatever was typed.
        var entry = ledger.Entries[secondIrn];
        var made = long.Parse(EwayBillNumber(), CultureInfo.InvariantCulture);
        var (madeAt, madeTill) = (now.AddHours(-2), now.AddDays(2));
        ledger.Entries[secondIrn] = entry with { Irn = entry.Irn with { EwbNo = made, EwbDate = madeAt, EwbValidTill = madeTill } };
        using (var mistyped = await pharmacist.SendRawAsync(HttpMethod.Post, $"{Register}/pharmacy/{second}/ewaybill/record",
                   new RecordEwayBillRequest((made + 1).ToString(CultureInfo.InvariantCulture), now.AddMinutes(-30), null)))
        {
            (await CodeAsync(mistyped)).ShouldBe("einvoice.ewaybill_unconfirmed");
            (await mistyped.Content.ReadAsStringAsync(Ct)).ShouldContain($"The portal holds e-way bill {made} for this invoice's IRN");
        }

        var confirmed = (await pharmacist.PostAsync<EInvoiceDetail>($"{Register}/pharmacy/{second}/ewaybill/record",
            new RecordEwayBillRequest(made.ToString(CultureInfo.InvariantCulture), now.AddMinutes(-30), now.AddDays(9)))).Registration!;
        confirmed.EwbNo.ShouldBe(made);
        confirmed.EwbDate!.Value.ShouldBe(madeAt, TimeSpan.FromSeconds(1));
        confirmed.EwbValidTill!.Value.ShouldBe(madeTill, TimeSpan.FromSeconds(1));
        (await AuditAsync("billing.einvoice.ewaybill_recorded", made.ToString(CultureInfo.InvariantCulture)))
            .ShouldContain("holds it, live, for the invoice's IRN");

        // The portal not answering: nothing is recorded. Not connected: it is recorded on its print, the portal not asked.
        var (third, thirdIrn) = await B2bSaleWithIrnAsync(pharmacist, medicine);
        var other = EwayBillNumber();
        var record = new RecordEwayBillRequest(other, now.AddMinutes(-10), null);
        using (ledger.Fail(SimulatorIrpOperations.GetEwayBill, SimulatorIrpFault.Down, irn: thirdIrn))
        {
            using var down = await pharmacist.SendRawAsync(HttpMethod.Post, $"{Register}/pharmacy/{third}/ewaybill/record", record);
            (await CodeAsync(down)).ShouldBe("einvoice.irp_unreachable");
        }

        (await pharmacist.GetAsync<EInvoiceDetail>($"{Register}/pharmacy/{third}")).Registration!.EwbNo.ShouldBeNull();
        await using (await server.IrpPortalAsync(enabled: false))
        {
            var partA = (await pharmacist.PostAsync<EInvoiceDetail>($"{Register}/pharmacy/{third}/ewaybill/record", record)).Registration!;
            (partA.EwbNo, partA.EwbValidTill).ShouldBe((long.Parse(other, CultureInfo.InvariantCulture), null), "only Part-A filled: no validity yet");
        }

        (await AuditAsync("billing.einvoice.ewaybill_recorded", other)).ShouldContain("not connected to the e-invoice portal");

        // Not one the portal could have generated: 14 digits before 2027, or not 12 or 14 digits at all.
        var (fourth, _) = await B2bSaleWithIrnAsync(pharmacist, medicine);
        using (var early = await pharmacist.SendRawAsync(HttpMethod.Post, $"{Register}/pharmacy/{fourth}/ewaybill/record",
                   new RecordEwayBillRequest("33100012345678", now.AddMinutes(-10), null)))
        {
            (await CodeAsync(early)).ShouldBe("einvoice.ewaybill_invalid");
        }

        using var garbled = await pharmacist.SendRawAsync(HttpMethod.Post, $"{Register}/pharmacy/{fourth}/ewaybill/record",
            new RecordEwayBillRequest("12345", now.AddMinutes(-10), null));
        garbled.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_debit_note_raises_the_corporates_bill_goes_to_the_portal_as_a_dbn_and_into_gstr1()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var service = await TaxedServiceAsync(admin);
        var company = await CompanyAsync(admin);
        await using var einvoicing = await server.EInvoicingAsync(EInvoicing.Today().AddDays(-1), Ct);
        await using var portal = await server.IrpPortalAsync(enabled: true);
        var bill = await BillAsync(company, service);
        var line = bill.Lines.Single(l => l.ServiceItemId == service.Id);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);

        // ₹200 more on the lounge, charged to the company: IGST 18% to Kerala is ₹36.
        var asked = await AskAsync(billing, $"/api/v1/billing/bills/{bill.Id}/debit-notes",
            new IssueDebitNoteRequest(DebitNoteParty.Payer, "Lounge charged at last year's rate", [new DebitNoteLineRequest(line.Id, 200m)]));
        asked.Details.ShouldContain(new ApprovalDetail("Debit note", "₹236.00"));

        // Whoever asked may not approve; the accountant does.
        using (var self = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
        {
            self.IsSuccessStatusCode.ShouldBeFalse();
        }

        await MakerChecker.ApproveAsync(accountant, asked, "Rate card checked");
        var after = await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}");
        var note = after.DebitNotes.ShouldNotBeNull().ShouldHaveSingleItem();
        (note.Amount, note.TaxableAmount, note.GstAmount, note.Party).ShouldBe((236m, 200m, 36m, DebitNoteParty.Payer));
        note.Number.Length.ShouldBeLessThanOrEqualTo(16, "GST rule 46");
        after.PayerDebitAmount.ShouldBe(236m);

        // Both go to the portal on their own, the bill's IRN first and then the note's, as a DBN naming the bill (a note
        // waiting for its invoice's IRN: EInvoiceRegisterTests).
        await billing.EInvoiceWhenAsync("bill", bill.Id, d => d.State == EInvoiceState.Registered, "the bill's IRN", Ct);
        var dbn = await billing.EInvoiceWhenAsync("debitnote", note.Id, d => d.State == EInvoiceState.Registered, "the debit note's IRN", Ct);
        (dbn.Type, dbn.Against).ShouldBe(("DBN", bill.BillNumber));

        // GSTR-1 of the month: the bill in B2B and the debit note in CDNR, and both in the offline tool's JSON.
        var month = $"{EInvoicing.Today():yyyy-MM}";
        var gstr1 = await accountant.GetAsync<Gstr1Return>($"/api/v1/billing/gst-returns/gstr1?month={month}");
        gstr1.Gstin.ShouldBe(EInvoicing.Hospital);
        gstr1.B2b.ShouldContain(d => d.Number == bill.BillNumber && d.BuyerGstin == EInvoicing.KeralaCompany);
        var cdnr = gstr1.Cdnr.Single(d => d.Number == note.Number);
        (cdnr.Kind, cdnr.Items.Single().Amounts.Igst).ShouldBe((GstDocumentKind.DebitNote, 36m));
        gstr1.DocumentsIssued.ShouldContain(d => d.Nature == 4 && d.From.Length > 0);
        using var file = await accountant.SendRawAsync(HttpMethod.Get, $"/api/v1/billing/gst-returns/gstr1/json?month={month}");
        file.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = JsonNode.Parse(await file.Content.ReadAsStringAsync(Ct))!;
        json["fp"]!.GetValue<string>().ShouldBe($"{EInvoicing.Today():MMyyyy}");
        json["cdnr"]!.AsArray().SelectMany(c => c!["nt"]!.AsArray()).ShouldContain(n => n!["nt_num"]!.GetValue<string>() == note.Number);
        (await accountant.GetAsync<Gstr3bReturn>($"/api/v1/billing/gst-returns/gstr3b?month={month}")).OutwardTaxable.Igst.ShouldBeGreaterThanOrEqualTo(36m);

        // The billing desk does not read the returns.
        using var forbidden = await billing.SendRawAsync(HttpMethod.Get, $"/api/v1/billing/gst-returns/gstr1?month={month}");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_payer_credit_note_credits_the_gst_of_what_the_payer_disallowed_and_leaves_the_patients_share()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var service = await TaxedServiceAsync(admin);
        var company = await CompanyAsync(admin);
        var bill = await BillAsync(company, service);
        var line = bill.Lines.Single(l => l.ServiceItemId == service.Id);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var path = $"/api/v1/billing/bills/{bill.Id}/credit-notes";

        // Nothing disallowed yet: nothing to credit to the payer.
        using (var early = await billing.SendRawAsync(HttpMethod.Post, path,
                   new IssueCreditNoteRequest(CreditNoteKind.PayerShare, 118m, "Tariff deduction", [new CreditNoteLineRequest(line.Id, 118m)])))
        {
            (await CodeAsync(early)).ShouldBe("billing.payer_credit_exceeds");
        }

        // The company settles ₹236 short (its settlement records the deduction; the claim desk's own flow is tested elsewhere).
        await WithDbAsync(async db =>
        {
            var row = await db.Set<Bill>().SingleAsync(b => b.Id == bill.Id, Ct);
            row.ApplyPayerSettlement(row.PayerOutstanding - 236m, 236m).IsSuccess.ShouldBeTrue();
            await db.SaveChangesAsync(Ct);
        });
        bill = await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}");

        using (var tooMuch = await billing.SendRawAsync(HttpMethod.Post, path,
                   new IssueCreditNoteRequest(CreditNoteKind.PayerShare, 300m, "Tariff deduction", [new CreditNoteLineRequest(line.Id, 300m)])))
        {
            (await CodeAsync(tooMuch)).ShouldBe("billing.payer_credit_exceeds");
        }

        var asked = await AskAsync(billing, path,
            new IssueCreditNoteRequest(CreditNoteKind.PayerShare, 236m, "Lounge disallowed above the tariff", [new CreditNoteLineRequest(line.Id, 236m)]));
        asked.Details.ShouldContain(new ApprovalDetail("Disallowed by the payer", "₹236.00"));
        await MakerChecker.ApproveAsync(accountant, asked);

        var after = await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}");
        var note = after.CreditNotes.Single(n => n.Kind == CreditNoteKind.PayerShare);
        (note.Amount, note.TaxableAmount, note.GstAmount).ShouldBe((236m, 200m, 36m));
        (after.CreditAmount, after.DueAmount).ShouldBe((bill.CreditAmount, bill.DueAmount), "the patient owes what they owed");

        // Fully credited: no more.
        using var again = await billing.SendRawAsync(HttpMethod.Post, path,
            new IssueCreditNoteRequest(CreditNoteKind.PayerShare, 1m, "Again", [new CreditNoteLineRequest(line.Id, 1m)]));
        (await CodeAsync(again)).ShouldBe("billing.payer_credit_exceeds");

        // It prints to the company, as a credit note.
        var document = await billing.GetAsync<HyFib.HealthCare360.Platform.Documents.PrintableDocumentDto>($"{path}/{note.Id}/document");
        document.Html.ShouldContain(company.Name);
    }

    [Fact]
    public async Task A_gst_invoice_series_refuses_a_format_whose_numbers_can_pass_16_characters()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var list = await admin.GetAsync<NumberSeriesList>("/api/v1/admin/number-series");
        var op = list.Series.Single(s => s.Key == "billing.bill.op");
        op.GstDocument.ShouldBeTrue();
        list.Series.Single(s => s.Key == "billing.debitnote").GstDocument.ShouldBeTrue();
        list.Series.Single(s => s.Key == "billing.receipt").GstDocument.ShouldBeFalse();

        // {FAC}/OPB/{YYYY}/{SEQ:6}: at least 2 + 5 + 4 + 1 + 6 = 18 characters, within the column's 24 but past GST's 16.
        using var refused = await admin.SendRawAsync(HttpMethod.Put, "/api/v1/admin/number-series/billing.bill.op",
            new SaveNumberSeriesRequest("{FAC}/OPB/{YYYY}/{SEQ:6}", NumberResetPolicy.Yearly, null, op.Format, op.ResetPolicy));
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("GST rule 46 allows at most 16 characters");
        (await admin.GetAsync<NumberSeriesList>("/api/v1/admin/number-series")).Series.Single(s => s.Key == "billing.bill.op").Format.ShouldBe(op.Format);
    }

    private async Task<ServiceItemDto> TaxedServiceAsync(ApiClient admin) => await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
        $"SV-{Letters(8)}", "Executive lounge access", null, ServiceCategory.Miscellaneous, null, 1000m, 18m, "999799", true, false, null, true, null));

    private async Task<PayerDetail> CompanyAsync(ApiClient admin) => await server.CreatePayerAsync(admin, new SavePayerRequest(
        $"CO{Letters(8).ToUpperInvariant()}", $"Malabar Traders {Letters(6).ToUpperInvariant()} Ltd", PayerCategory.Corporate, "Anil Menon", "9876543210",
        null, "4/12 MG Road, Ernakulam, Kochi, Kerala 682016", EInvoicing.KeralaCompany, null, CreditDays: 30, DefaultCoPayPercent: 0m, PortalUrl: null,
        IsActive: true, Notes: null, Version: null));

    /// <summary>A corporate health check-up with one charge of <paramref name="service"/>, billed and finalized by the billing desk.</summary>
    private async Task<BillDetail> BillAsync(PayerDetail company, ServiceItemDto service)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: TestPatients.FirstName("Divya"), MiddleName: null, LastName: "Gst" + Letters(8), Gender: Gender.Female,
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
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(patient.Id, visit.Id, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));
        return await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
    }

    /// <summary>A pharmacy counter sale to a buyer with a GSTIN, once it has the IRN the register asked the portal for on its own.</summary>
    private static async Task<(Guid Sale, string Irn)> B2bSaleWithIrnAsync(ApiClient pharmacist, OwnMedicine medicine)
    {
        var sold = await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter",
            new CounterSaleRequest(medicine.StoreId, null, "Sree Clinic", null, null, [new CounterSaleLine(medicine.ItemId, 1m, medicine.Batch.BatchId)], 0m,
                [new SalePayment(PharmacyPaymentMode.Cash, Math.Round(medicine.Batch.Mrp, MidpointRounding.AwayFromZero), null)], false,
                new B2bBuyer(EInvoicing.KeralaCompany, "Sree Clinic", "32", "Sree Clinic, 22 Beach Road, Kozhikode 673001")));
        var registered = await pharmacist.EInvoiceWhenAsync("pharmacy", sold.Id, d => d.State == EInvoiceState.Registered, "the sale's IRN", Ct);
        return (sold.Id, registered.Registration!.Irn);
    }

    private async Task<IAsyncDisposable> SettingAsync(string key, decimal value)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var facilities = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<HyFib.HealthCare360.Platform.Organization.Facility>()
            .Select(f => f.Id).ToListAsync(Ct);
        foreach (var facility in facilities)
        {
            await settings.SetAsync<decimal?>(key, value, facility, cancellationToken: Ct);
        }

        return new Restore(async () =>
        {
            await using var inner = server.Services.CreateAsyncScope();
            var store = inner.ServiceProvider.GetRequiredService<ISettingsService>();
            foreach (var facility in facilities)
            {
                await store.SetAsync<decimal?>(key, null, facility, cancellationToken: CancellationToken.None);
            }
        });
    }

    /// <summary>A 12-digit e-way bill number, above those the simulator gives.</summary>
    private static string EwayBillNumber() => Random.Shared.NextInt64(500000000000L, 999999999999L).ToString(CultureInfo.InvariantCulture);

    /// <summary>The summary of the latest audit event <paramref name="action"/> naming <paramref name="naming"/>, once written.</summary>
    private async Task<string> AuditAsync(string action, string naming)
    {
        for (var waited = TimeSpan.Zero; ; waited += TimeSpan.FromMilliseconds(250))
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var summary = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
                    .Where(a => a.Action == action && a.Summary != null && a.Summary.Contains(naming))
                    .OrderByDescending(a => a.Id).Select(a => a.Summary).FirstOrDefaultAsync(Ct);
                if (summary is not null)
                {
                    return summary;
                }
            }

            waited.ShouldBeLessThan(TimeSpan.FromSeconds(15), $"no {action} audit event names {naming}");
            await Task.Delay(250, Ct);
        }
    }

    private async Task WithDbAsync(Func<HospitalDbContext, Task> work)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<HospitalDbContext>());
    }

    private static async Task<ApprovalRequestDto> AskAsync(ApiClient maker, string path, object request)
    {
        using var response = await maker.SendRawAsync(HttpMethod.Post, path, request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string Letters(int count) => new(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
