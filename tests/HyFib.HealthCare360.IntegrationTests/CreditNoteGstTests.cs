using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.EInvoices;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A credit note as a GST credit note (CGST Act 2017 s.34), over the real API and PostgreSQL. It names the services it
/// corrects and takes back each one's own GST, in the note and in the Tally day book. On a bill to a company registered
/// for GST it is an e-invoice of its own, a <c>CRN</c> naming the bill, which goes to the portal after the bill's IRN, is
/// recorded with its own signed QR code and printed with it, and keeps the bill's IRN from being cancelled under it. A
/// return of the pharmacy's B2B sale is a <c>CRN</c> too.
/// </summary>
public sealed class CreditNoteGstTests(HospitalServer server)
{
    private const string Register = "/api/v1/billing/einvoices";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_credit_note_names_the_services_it_corrects_and_takes_back_their_own_gst_in_the_books()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (lounge, dressing) = await ServicesAsync(admin);
        var bill = await BillAsync(null, 0m, lounge, dressing);
        var loungeLine = bill.Lines.Single(l => l.ServiceItemId == lounge.Id);
        var dressingLine = bill.Lines.Single(l => l.ServiceItemId == dressing.Id);
        (loungeLine.NetAmount, loungeLine.TaxableAmount, dressingLine.NetAmount).ShouldBe((1180m, 1000m, 250m));
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);

        // The bill carries GST, so a credit note names the services it corrects, and each no further than its value.
        (await RefusedAsync(billing, bill, new IssueCreditNoteRequest(CreditNoteKind.CreditNote, 640m, "Lounge charged for a day not used"), HttpStatusCode.BadRequest))
            .ShouldContain("Name the services this credit note corrects");
        (await RefusedAsync(billing, bill, new IssueCreditNoteRequest(CreditNoteKind.WriteOff, 50m, "Cannot be recovered", [new(dressingLine.Id, 50m)]),
            HttpStatusCode.BadRequest)).ShouldContain("A write-off is of the balance due");
        (await RefusedAsync(billing, bill, new IssueCreditNoteRequest(CreditNoteKind.CreditNote, 640m, "Lounge charged for a day not used",
            [new(loungeLine.Id, 590m), new(dressingLine.Id, 40m)]), HttpStatusCode.BadRequest)).ShouldContain("The amount is the sum of the services' credits.");
        (await RefusedAsync(billing, bill, new IssueCreditNoteRequest(CreditNoteKind.CreditNote, 50m, "Not this bill's", [new(Guid.NewGuid(), 50m)]),
            HttpStatusCode.BadRequest)).ShouldContain("This service is not on the bill.");
        (await RefusedAsync(billing, bill, new IssueCreditNoteRequest(CreditNoteKind.CreditNote, 300m, "Dressings not done", [new(dressingLine.Id, 300m)]),
            HttpStatusCode.UnprocessableEntity)).ShouldContain("At most ₹250.00 can be credited on");

        // Half the lounge and one dressing: the approver sees the GST each gives back.
        var asked = await AskAsync(billing, bill, new IssueCreditNoteRequest(CreditNoteKind.CreditNote, 640m, "Lounge charged for a day not used; a dressing not done",
            [new(loungeLine.Id, 590m), new(dressingLine.Id, 50m)]));
        asked.Details.ShouldContain(d => d.Value == "₹590.00, of it GST ₹90.00 at 18%");
        asked.Details.ShouldContain(new ApprovalDetail("GST credited", "₹90.00"));
        await MakerChecker.ApproveAsync(accountant, asked, "Lounge register checked");

        var note = (await BillOfAsync(billing, bill)).CreditNotes.ShouldHaveSingleItem();
        (note.Amount, note.TaxableAmount, note.GstAmount, note.GstTimeBarred).ShouldBe((640m, 550m, 90m, false));
        var credited = note.Lines.ShouldNotBeNull();
        credited.Single(l => l.BillLineId == loungeLine.Id).ShouldBe(new CreditNoteLineDto(loungeLine.Id, loungeLine.Description, "999799", 18m, 590m, 500m, 45m, 45m, 0m));
        credited.Single(l => l.BillLineId == dressingLine.Id).ShouldBe(new CreditNoteLineDto(dressingLine.Id, dressingLine.Description, "999312", 0m, 50m, 50m, 0m, 0m, 0m));

        // The rest of the lounge gives back the rest of its GST, and the line is then credited in full.
        await MakerChecker.ApproveAsync(accountant, await AskAsync(billing, await BillOfAsync(billing, bill), new IssueCreditNoteRequest(
            CreditNoteKind.CreditNote, 590m, "Lounge not used the second day", [new(loungeLine.Id, 590m)])));
        var notes = (await BillOfAsync(billing, bill)).CreditNotes;
        notes.Sum(n => n.Lines!.Where(l => l.BillLineId == loungeLine.Id).Sum(l => l.Cgst + l.Sgst)).ShouldBe(180m, "The lounge's own GST, no more.");
        (await RefusedAsync(billing, await BillOfAsync(billing, bill), new IssueCreditNoteRequest(CreditNoteKind.CreditNote, 1m, "Again",
            [new(loungeLine.Id, 1m)]), HttpStatusCode.UnprocessableEntity)).ShouldContain("is already credited in full");

        // The books take the GST back from the output GST ledgers; the bill to a patient is not an e-invoice.
        var vouchers = await accountant.GetAsync<List<AccountingVoucher>>($"/api/v1/billing/tally/day-book?date={EInvoicing.Today():yyyy-MM-dd}");
        var voucher = vouchers.Single(v => v.Number == note.Number);
        voucher.Entries.Single(e => e.LedgerName == "Output CGST").ShouldBe(new AccountingEntry("Output CGST", 45m, IsDebit: true));
        voucher.Entries.Single(e => e.LedgerName == "Output SGST").ShouldBe(new AccountingEntry("Output SGST", 45m, IsDebit: true));
        voucher.Entries.Single(e => e.LedgerName == "Sales Returns & Allowances").ShouldBe(new AccountingEntry("Sales Returns & Allowances", 550m, IsDebit: true));
        voucher.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount).ShouldBe(640m);

        // A write-off reduces no GST and names no services.
        await MakerChecker.ApproveAsync(accountant, await AskAsync(billing, await BillOfAsync(billing, bill), new IssueCreditNoteRequest(
            CreditNoteKind.WriteOff, 100m, "Cannot be recovered")));
        var writeOff = (await BillOfAsync(billing, bill)).CreditNotes.Single(n => n.Kind == CreditNoteKind.WriteOff);
        writeOff.GstAmount.ShouldBe(0m);
        (writeOff.Lines ?? []).ShouldBeEmpty();
        var document = await billing.GetAsync<PrintableDocumentDto>($"/api/v1/billing/bills/{bill.Id}/credit-notes/{writeOff.Id}/document");
        document.Html.ShouldContain("It is not a GST credit note: it reduces no tax.");
    }

    [Fact]
    public async Task A_credit_note_on_an_e_invoiced_bill_goes_to_the_portal_as_a_crn_after_its_invoice()
    {
        await using var _ = await server.EInvoicingAsync(EInvoicing.Today().AddDays(-1), Ct);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (lounge, _) = await ServicesAsync(admin);
        var company = await CompanyAsync(admin);

        // The company pays 90%; a credit note lowers the patient's 10%, and the supply invoiced to the company with it.
        var bill = await BillAsync(company, 10m, lounge);
        var loungeLine = bill.Lines.Single(l => l.ServiceItemId == lounge.Id);
        bill.PatientAmount.ShouldBeGreaterThanOrEqualTo(118m);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        await MakerChecker.ApproveAsync(accountant, await AskAsync(billing, bill, new IssueCreditNoteRequest(
            CreditNoteKind.CreditNote, 118m, "Lounge charged for a day not used", [new(loungeLine.Id, 118m)])));
        var note = (await BillOfAsync(billing, bill)).CreditNotes.ShouldHaveSingleItem();
        (note.TaxableAmount, note.Lines!.Single().Igst).ShouldBe((100m, 18m), "To Kerala the GST given back is IGST, as the bill charged it.");

        // It waits in the register as a credit note against the bill, and goes to the portal after the bill's own IRN.
        var row = (await accountant.GetAsync<EInvoiceRegister>(Register)).Rows.Single(r => r.Id == note.Id);
        (row.Source, row.Type, row.Against, row.State, row.Total, row.BuyerGstin).ShouldBe(("creditnote", "CRN", bill.BillNumber, EInvoiceState.Pending, 118m, EInvoicing.KeralaCompany));
        var waiting = await accountant.GetAsync<EInvoiceDetail>($"{Register}/creditnote/{note.Id}");
        (waiting.Type, waiting.Against).ShouldBe(("CRN", bill.BillNumber));
        waiting.Problems.ShouldContain(p => p.StartsWith($"Invoice {bill.BillNumber} has no IRN yet: record its IRN first", StringComparison.Ordinal));
        using (var early = await accountant.SendRawAsync(HttpMethod.Get, $"{Register}/creditnote/{note.Id}/json"))
        {
            (await CodeAsync(early)).ShouldBe("einvoice.invalid");
        }

        var billIrn = EInvoicing.Irn();
        await accountant.PostAsync<EInvoiceDetail>($"{Register}/bill/{bill.Id}/irn",
            new RecordIrnRequest("112610012340001", EInvoicing.Qr(bill.BillNumber!, bill.NetAmount, EInvoicing.KeralaCompany, billIrn, bill.Lines.Count)));
        (await accountant.GetAsync<EInvoiceDetail>($"{Register}/creditnote/{note.Id}")).Problems.ShouldBeEmpty();

        // Its JSON is the portal's CRN, naming the bill.
        var crn = (await JsonAsync(accountant, $"{Register}/creditnote/{note.Id}/json")).AsArray().ShouldHaveSingleItem()!;
        (crn["DocDtls"]!["Typ"]!.GetValue<string>(), crn["DocDtls"]!["No"]!.GetValue<string>()).ShouldBe(("CRN", note.Number));
        crn["RefDtls"]!["PrecDocDtls"]![0]!["InvNo"]!.GetValue<string>().ShouldBe(bill.BillNumber);
        var item = crn["ItemList"]!.AsArray().ShouldHaveSingleItem()!;
        (item["AssAmt"]!.GetValue<decimal>(), item["IgstAmt"]!.GetValue<decimal>(), item["HsnCd"]!.GetValue<string>()).ShouldBe((100m, 18m, "999799"));
        crn["ValDtls"]!["TotInvVal"]!.GetValue<decimal>().ShouldBe(118m);

        // The IRN the portal gave it must be a credit note's; the note then prints it.
        using (var asInvoice = await accountant.SendRawAsync(HttpMethod.Post, $"{Register}/creditnote/{note.Id}/irn",
                   new RecordIrnRequest("112610012340002", EInvoicing.Qr(note.Number, 118m, EInvoicing.KeralaCompany, EInvoicing.Irn(), 1))))
        {
            asInvoice.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await asInvoice.Content.ReadAsStringAsync(Ct)).ShouldContain("The code's document type is INV, the credit note's CRN.");
        }

        var noteIrn = EInvoicing.Irn();
        var recorded = await accountant.PostAsync<EInvoiceDetail>($"{Register}/creditnote/{note.Id}/irn",
            new RecordIrnRequest("112610012340003", EInvoicing.Qr(note.Number, 118m, EInvoicing.KeralaCompany, noteIrn, 1, "CRN")));
        recorded.State.ShouldBe(EInvoiceState.Registered);
        (await BillOfAsync(billing, bill)).CreditNotes.Single().EInvoice.ShouldNotBeNull().Irn.ShouldBe(noteIrn);
        var printed = await billing.GetAsync<PrintableDocumentDto>($"/api/v1/billing/bills/{bill.Id}/credit-notes/{note.Id}/document");
        printed.Html.ShouldContain(noteIrn);
        printed.Html.ShouldContain($"Against tax invoice: <b>{bill.BillNumber}</b>");
        printed.Html.ShouldContain($"GSTIN <b>{EInvoicing.KeralaCompany}</b>");
        printed.Html.ShouldContain("<tr><td>IGST credited</td><td class=\"num\">₹18.00</td></tr>");
        using (var pdf = await billing.SendRawAsync(HttpMethod.Get, $"/api/v1/billing/bills/{bill.Id}/credit-notes/{Guid.NewGuid()}/pdf"))
        {
            pdf.StatusCode.ShouldBe(HttpStatusCode.NotFound, "A note of another bill, or none, is not printed under this one.");
        }

        // A write-off of the rest of the patient's share is no GST credit note, and not an e-invoice.
        await MakerChecker.ApproveAsync(accountant, await AskAsync(billing, await BillOfAsync(billing, bill), new IssueCreditNoteRequest(
            CreditNoteKind.WriteOff, 10m, "Cannot be recovered")));
        var writeOff = (await BillOfAsync(billing, bill)).CreditNotes.Single(n => n.Kind == CreditNoteKind.WriteOff);
        (await accountant.GetAsync<EInvoiceRegister>(Register)).Rows.ShouldNotContain(r => r.Id == writeOff.Id);

        // The bill's IRN stays while the credit note's names it; once the note's is cancelled, the bill's can be.
        var billRegistration = (await accountant.GetAsync<EInvoiceDetail>($"{Register}/bill/{bill.Id}")).Registration!;
        using (var refused = await accountant.SendRawAsync(HttpMethod.Post, $"{Register}/bill/{bill.Id}/irn/cancel",
                   new CancelIrnRequest(EInvoiceCancelReason.DataEntryMistake, "Wrong company", billRegistration.Version)))
        {
            (await CodeAsync(refused)).ShouldBe("einvoice.credited");
        }

        await accountant.PostAsync<EInvoiceDetail>($"{Register}/creditnote/{note.Id}/irn/cancel",
            new CancelIrnRequest(EInvoiceCancelReason.DataEntryMistake, "Wrong company", recorded.Registration!.Version));
        (await accountant.PostAsync<EInvoiceDetail>($"{Register}/bill/{bill.Id}/irn/cancel",
            new CancelIrnRequest(EInvoiceCancelReason.DataEntryMistake, "Wrong company", billRegistration.Version))).State.ShouldBe(EInvoiceState.IrnCancelled);
    }

    [Fact]
    public async Task A_credit_note_after_the_bills_gst_could_be_reduced_lowers_the_amount_and_not_the_gst()
    {
        await using var _ = await server.EInvoicingAsync(EInvoicing.Today().AddDays(-1), Ct);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (lounge, _) = await ServicesAsync(admin);
        var bill = await BillAsync(await CompanyAsync(admin), 10m, lounge);

        // The bill was invoiced two years ago: the time to reduce its GST ended on 30 November after that financial year.
        DateTimeOffset invoiced;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            invoiced = bill.FinalizedAt!.Value.AddYears(-2);
            await db.Set<Bill>().Where(b => b.Id == bill.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.FinalizedAt, invoiced), Ct);
        }

        var billDate = DateOnly.FromDateTime(invoiced.ToOffset(TimeSpan.FromHours(5.5)).DateTime);
        var lastDay = new DateOnly(billDate.Month >= 4 ? billDate.Year + 1 : billDate.Year, 11, 30);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var loungeLine = bill.Lines.Single(l => l.ServiceItemId == lounge.Id);
        var asked = await AskAsync(billing, bill, new IssueCreditNoteRequest(
            CreditNoteKind.CreditNote, 118m, "Lounge charged for a day not used", [new(loungeLine.Id, 118m)]));
        asked.Details.ShouldContain(new ApprovalDetail("GST credited",
            $"None: the bill's GST could be reduced until {lastDay.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)} (CGST Act s.34(2))"));
        await MakerChecker.ApproveAsync(accountant, asked);

        var note = (await BillOfAsync(billing, bill)).CreditNotes.ShouldHaveSingleItem();
        (note.GstTimeBarred, note.GstAmount, note.TaxableAmount).ShouldBe((true, 0m, 118m));
        note.Lines.ShouldNotBeNull().Single().ShouldBe(new CreditNoteLineDto(loungeLine.Id, loungeLine.Description, "999799", 0m, 118m, 118m, 0m, 0m, 0m));
        (await BillOfAsync(billing, bill)).CreditAmount.ShouldBe(118m, "The patient owes less all the same.");

        // Not reported to the portal, no GST taken back in the books, and the note says so.
        (await accountant.GetAsync<EInvoiceRegister>(Register)).Rows.ShouldNotContain(r => r.Id == note.Id);
        var voucher = (await accountant.GetAsync<List<AccountingVoucher>>($"/api/v1/billing/tally/day-book?date={EInvoicing.Today():yyyy-MM-dd}"))
            .Single(v => v.Number == note.Number);
        voucher.Entries.ShouldNotContain(e => e.LedgerName.StartsWith("Output", StringComparison.Ordinal));
        voucher.Entries.Single(e => e.LedgerName == "Sales Returns & Allowances").Amount.ShouldBe(118m);
        (await billing.GetAsync<PrintableDocumentDto>($"/api/v1/billing/bills/{bill.Id}/credit-notes/{note.Id}/document")).Html
            .ShouldContain("<b>GST not reduced.</b>");
    }

    [Fact]
    public async Task A_return_of_a_b2b_pharmacy_sale_goes_to_the_portal_as_a_crn_naming_the_sale()
    {
        await using var _ = await server.EInvoicingAsync(EInvoicing.Today().AddDays(-1), Ct);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var medicine = await OwnMedicine.OnTheShelfAsync(server, pharmacist);
        var price = Math.Round(medicine.Batch.Mrp * 2m, MidpointRounding.AwayFromZero);
        var sold = await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter",
            new CounterSaleRequest(medicine.StoreId, null, "Sree Clinic", null, null, [new CounterSaleLine(medicine.ItemId, 2m, medicine.Batch.BatchId)], 0m,
                [new SalePayment(PharmacyPaymentMode.Cash, price, null)], false,
                new B2bBuyer(EInvoicing.KeralaCompany, "Sree Clinic", "32", "Sree Clinic, 22 Beach Road, Kozhikode 673001")));
        var sale = await pharmacist.GetAsync<PharmacySaleDetail>($"/api/v1/pharmacy/sales/{sold.Id}");
        var returned = await pharmacist.PostAsync<SaleReturnCreated>($"/api/v1/pharmacy/sales/{sold.Id}/returns",
            new SaleReturnRequest([new SaleReturnLine(sale.Items.Single().Id, 1m, true)], "One strip unopened", PharmacyPaymentMode.Cash, null));

        var row = (await pharmacist.GetAsync<EInvoiceRegister>(Register)).Rows.Single(r => r.Id == returned.Id);
        (row.Source, row.Type, row.Against, row.Number, row.Total).ShouldBe(("pharmacyreturn", "CRN", sale.InvoiceNumber, returned.ReturnNumber, returned.RefundAmount));
        (await pharmacist.GetAsync<EInvoiceDetail>($"{Register}/pharmacyreturn/{returned.Id}")).Problems
            .ShouldContain(p => p.StartsWith($"Invoice {sale.InvoiceNumber} has no IRN yet", StringComparison.Ordinal));

        await pharmacist.PostAsync<EInvoiceDetail>($"{Register}/pharmacy/{sold.Id}/irn",
            new RecordIrnRequest("112610087650001", EInvoicing.Qr(sale.InvoiceNumber, sold.NetAmount, EInvoicing.KeralaCompany, EInvoicing.Irn(), 1)));
        var crn = (await JsonAsync(pharmacist, $"{Register}/pharmacyreturn/{returned.Id}/json")).AsArray().ShouldHaveSingleItem()!;
        (crn["DocDtls"]!["Typ"]!.GetValue<string>(), crn["RefDtls"]!["PrecDocDtls"]![0]!["InvNo"]!.GetValue<string>()).ShouldBe(("CRN", sale.InvoiceNumber));
        var item = crn["ItemList"]!.AsArray().ShouldHaveSingleItem()!;
        (item["IsServc"]!.GetValue<string>(), item["Qty"]!.GetValue<decimal>()).ShouldBe(("N", 1m));
        item["IgstAmt"]!.GetValue<decimal>().ShouldBeGreaterThan(0m, "Goods sent to Kerala: the GST carved out of the refund is IGST.");
        (item["AssAmt"]!.GetValue<decimal>() + item["IgstAmt"]!.GetValue<decimal>()).ShouldBe(returned.RefundAmount);
        crn["ValDtls"]!["TotInvVal"]!.GetValue<decimal>().ShouldBe(returned.RefundAmount);
    }

    /// <summary>The lounge at ₹1,000 with GST at 18%, and a dressing at ₹250 with none, of the test's own.</summary>
    private async Task<(ServiceItemDto Taxed, ServiceItemDto Exempt)> ServicesAsync(ApiClient admin) =>
        (await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
                $"SV-{Letters(8)}", "Executive lounge access", null, ServiceCategory.Miscellaneous, null, 1000m, 18m, "999799", true, false, null, true, null)),
            await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
                $"SV-{Letters(8)}", "Wound dressing", null, ServiceCategory.Procedure, null, 250m, 0m, "999312", true, false, null, true, null)));

    private async Task<PayerDetail> CompanyAsync(ApiClient admin) => await server.CreatePayerAsync(admin, new SavePayerRequest(
        $"CO{Letters(8).ToUpperInvariant()}", $"Malabar Traders {Letters(6).ToUpperInvariant()} Ltd", PayerCategory.Corporate, "Anil Menon", "9876543210",
        null, "4/12 MG Road, Ernakulam, Kochi, Kerala 682016", EInvoicing.KeralaCompany, null, CreditDays: 30, DefaultCoPayPercent: 0m, PortalUrl: null,
        IsActive: true, Notes: null, Version: null));

    /// <summary>
    /// A health check-up visit with one charge of each service, billed and finalized by the billing desk: paid by the
    /// patient, or by <paramref name="company"/> on the patient's membership with <paramref name="coPay"/> percent co-pay.
    /// </summary>
    private async Task<BillDetail> BillAsync(PayerDetail? company, decimal coPay, params ServiceItemDto[] services)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Kiran"), MiddleName: null, LastName: "Credit" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1980, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        if (company is not null)
        {
            await server.VerifiedPolicyAsync(patient.Id, company.Id, coPay);
        }

        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patient.Id, VisitType.HealthCheckup, null, null, null, company is null ? PayerType.Self : PayerType.Corporate, company?.Id,
            company is null ? ReferralSource.Self : ReferralSource.Corporate, null, null, false, null, null, TokenPriority.Normal, IssueToken: false,
            NotifyPatient: false));

        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        foreach (var service in services)
        {
            await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(patient.Id, visit.Id, null, service.Id, 1m, null, null, null, null, null));
        }

        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));
        return await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
    }

    private static Task<BillDetail> BillOfAsync(ApiClient client, BillDetail bill) => client.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}");

    private static async Task<ApprovalRequestDto> AskAsync(ApiClient maker, BillDetail bill, IssueCreditNoteRequest request)
    {
        using var response = await maker.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/bills/{bill.Id}/credit-notes", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
    }

    /// <summary>Asks for the credit note, is refused with <paramref name="status"/>, and returns the refusal as it reads.</summary>
    private static async Task<string> RefusedAsync(ApiClient maker, BillDetail bill, IssueCreditNoteRequest request, HttpStatusCode status)
    {
        using var response = await maker.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/bills/{bill.Id}/credit-notes", request);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, body);
        return body;
    }

    private static async Task<JsonNode> JsonAsync(ApiClient client, string path)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string Letters(int count) => new(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
}
