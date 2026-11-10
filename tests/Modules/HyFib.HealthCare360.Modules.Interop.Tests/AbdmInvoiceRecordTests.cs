using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.Testing.Abdm;
using Bill = HyFib.HealthCare360.Modules.Billing.Domain.Bill;
using BillStatus = HyFib.HealthCare360.Modules.Billing.Domain.BillStatus;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// A finalised bill as an ABDM invoice record (the guide's InvoiceRecord, with its Invoice). The sample document is in
/// Fixtures/Abdm with the other kinds (<see cref="AbdmDocumentTests"/>); these tests say what it holds: amounts that add
/// up to the bill's, line by line and in total, its GST, its status, which bills are exported, its name as a care context
/// and its HI type. The guide's InvoiceRecord and Invoice profiles are not in the 6.5.0 package the validator has, so the
/// document is validated against base FHIR R4 and the guide's other profiles, and the validator is shown to say it could
/// not check those two rather than pass them.
/// </summary>
public sealed class AbdmInvoiceRecordTests
{
    private static readonly DateTimeOffset RecordedAt = new(2026, 9, 25, 5, 0, 0, TimeSpan.Zero);

    private static R4.Bundle Build(Bill bill, AbdmPdf? pdf = null) => AbdmDocumentBuilder.Build(AbdmRecords.Invoice(AbdmTestData.InvoiceRecord(bill, pdf)));

    private static T Single<T>(R4.Bundle bundle)
        where T : R4.Resource =>
        bundle.Entry.Select(e => e.Resource).OfType<T>().Single();

    private static string FullUrl(R4.Bundle bundle, R4.Resource resource) => bundle.Entry.Single(e => ReferenceEquals(e.Resource, resource)).FullUrl!;

    /// <summary>What a set of price components adds up to: every one but those given as information only.</summary>
    private static decimal Sum(IEnumerable<R4.Invoice.PriceComponentComponent> components) =>
        components.Where(c => c.Type != R4.InvoicePriceComponentType.Informational).Sum(c => c.Amount.Value!.Value);

    private static R4.Invoice.PriceComponentComponent? Component(IEnumerable<R4.Invoice.PriceComponentComponent> components, string code) =>
        components.SingleOrDefault(c => c.Code?.Coding.SingleOrDefault()?.Code == code);

    [Fact]
    public void A_finalised_bill_is_an_invoice_record_of_the_facility_that_issued_it_with_the_printed_bill()
    {
        var document = AbdmRecords.Invoice(AbdmTestData.InvoiceRecord(pdf: AbdmTestData.Pdf));
        var bundle = AbdmDocumentBuilder.Build(document);
        var composition = (R4.Composition)bundle.Entry[0].Resource;
        var hospital = Single<R4.Organization>(bundle);
        var invoice = Single<R4.Invoice>(bundle);
        var pdf = Single<R4.Binary>(bundle);

        document.FileName.ShouldBe($"bill-{AbdmTestData.BillNumber}-abdm.json");
        composition.Meta.Profile.ShouldBe([AbdmProfiles.InvoiceRecord]);
        var type = composition.Type.Coding.ShouldHaveSingleItem();
        (type.System, type.Code, type.Display).ShouldBe(("https://nrces.in/ndhm/fhir/r4/CodeSystem/ndhm-billing-codes", "INVOICE", "Invoice"));
        composition.Title.ShouldBe("Invoice");
        composition.Status.ShouldBe(R4.CompositionStatus.Final);
        composition.Date.ShouldBe(FhirTime.DateTime(AbdmTestData.BillFinalized));
        composition.Author.ShouldHaveSingleItem().Reference.ShouldBe(FullUrl(bundle, hospital));
        composition.Custodian.Reference.ShouldBe(FullUrl(bundle, hospital));
        composition.Encounter.Reference.ShouldBe(FullUrl(bundle, Single<R4.Encounter>(bundle)));
        composition.Attester.ShouldBeEmpty("no one signs a bill as a doctor signs a note");
        var section = composition.Section.ShouldHaveSingleItem();
        section.Code.Coding.ShouldHaveSingleItem().Code.ShouldBe("INVOICE");
        section.Entry.Select(e => (e.Type, e.Reference)).ShouldBe([("Invoice", FullUrl(bundle, invoice)), ("Binary", FullUrl(bundle, pdf))]);
        section.Entry[0].Display.ShouldBe($"Bill {AbdmTestData.BillNumber}");

        // The invoice: the bill's number, issued by the hospital to the document's patient when it was finalised, with the
        // doctor whose consultation it bills.
        invoice.Meta.Profile.ShouldBe([AbdmProfiles.Invoice]);
        var identifier = invoice.Identifier.ShouldHaveSingleItem();
        (identifier.System, identifier.Value).ShouldBe(("urn:hc360:bill-number", AbdmTestData.BillNumber));
        invoice.Status.ShouldBe(R4.Invoice.InvoiceStatus.Issued);
        invoice.Type.Coding.ShouldHaveSingleItem().Code.ShouldBe("op");
        invoice.Subject.Reference.ShouldBe(composition.Subject.Reference);
        invoice.Date.ShouldBe(composition.Date);
        invoice.Issuer.Reference.ShouldBe(FullUrl(bundle, hospital));
        var participant = invoice.Participant.ShouldHaveSingleItem();
        participant.Actor.Reference.ShouldBe(FullUrl(bundle, Single<R4.Practitioner>(bundle)));
        participant.Actor.Display.ShouldBe("Dr. Asha Rao");
        invoice.PaymentTerms.ShouldBe("Payable by the patient: ₹1,399.00.");
        pdf.ContentType.ShouldBe("application/pdf");

        // What a person reads: the bill, each service with what it came to, and who pays.
        section.Text.Div.ShouldContain($"<p>Bill {AbdmTestData.BillNumber} (issued), Out-patient bill: total ₹1,399.00, taxes excluded ₹1,363.25</p>");
        section.Text.Div.ShouldContain("<p>Consultation, General Medicine × 1: ₹684.00</p>");
        section.Text.Div.ShouldContain("<p>Dressing kit × 2: ₹335.16</p>");
        section.Text.Div.ShouldContain("<p>Payable by the patient: ₹1,399.00.</p>");
    }

    [Fact]
    public void Every_line_and_the_totals_add_up_to_what_the_bill_says()
    {
        var bill = AbdmTestData.OpBill();
        var invoice = Single<R4.Invoice>(Build(bill));
        var lines = bill.Lines.OrderBy(l => l.SortOrder).ToList();

        invoice.LineItem.Select(i => i.Sequence).ShouldBe([1, 2, 3]);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var item = invoice.LineItem[i];
            var service = item.ChargeItem.ShouldBeOfType<R4.CodeableConcept>();
            service.Text.ShouldBe(line.Description);
            service.Coding.Select(c => (c.System, c.Code)).ShouldBe([("urn:hc360:service", line.ServiceCode), ("urn:hc360:gst-sac", line.SacCode)]);

            // The base is the unit price times the quantity, with the quantity as its factor; the unit price is stated as
            // information; the rest adds up to the line's net amount.
            var @base = item.PriceComponent.Single(c => c.Type == R4.InvoicePriceComponentType.Base);
            (@base.Amount.Value, @base.Factor).ShouldBe((line.UnitPrice * line.Quantity, line.Quantity));
            Component(item.PriceComponent, "unit-price").ShouldNotBeNull().Amount.Value.ShouldBe(line.UnitPrice);
            Sum(item.PriceComponent).ShouldBe(line.NetAmount, $"line {i + 1}");
            item.PriceComponent.ShouldAllBe(c => c.Amount.Currency == R4.Money.Currencies.INR);
        }

        // The consultation's 10% tariff concession and its share of the bill's 5% discount; the blood count's ₹50 off.
        var consultation = invoice.LineItem[0].PriceComponent;
        Component(consultation, "tariff-discount")!.Amount.Value.ShouldBe(-80m);
        Component(consultation, "bill-discount")!.Amount.Value.ShouldBe(-36m);
        Component(invoice.LineItem[1].PriceComponent, "line-discount")!.Amount.Value.ShouldBe(-50m);
        invoice.LineItem.SelectMany(i => i.PriceComponent).ShouldNotContain(c => c.Code != null && c.Code.Coding[0].Code == "not-charged",
            "a bill charged as usual charges each line for what it lists");

        // The totals: the gross less concessions and discounts, the GST, and the patient's share rounded to the rupee.
        bill.NetAmount.ShouldBe(1399m);
        Sum(invoice.TotalPriceComponent).ShouldBe(bill.NetAmount);
        invoice.TotalPriceComponent.Single(c => c.Type == R4.InvoicePriceComponentType.Base).Amount.Value.ShouldBe(bill.GrossAmount);
        Component(invoice.TotalPriceComponent, "round-off").ShouldNotBeNull().Amount.Value.ShouldBe(-0.16m);
        invoice.TotalNet.Value.ShouldBe(bill.TaxableAmount);
        invoice.TotalGross.Value.ShouldBe(bill.NetAmount);
        (invoice.TotalNet.Currency, invoice.TotalGross.Currency).ShouldBe((R4.Money.Currencies.INR, R4.Money.Currencies.INR));
        (invoice.LineItem.Sum(i => Sum(i.PriceComponent)) + bill.RoundOffAmount).ShouldBe(bill.NetAmount, "the lines and the round-off make the bill");
        (invoice.TotalGross.Value - invoice.TotalNet.Value).ShouldBe(bill.TaxAmount + bill.RoundOffAmount);
    }

    [Fact]
    public void Gst_is_central_and_state_within_the_state_at_half_the_rate_each_and_exempt_services_carry_none()
    {
        var bill = AbdmTestData.OpBill();
        var invoice = Single<R4.Invoice>(Build(bill));
        var kit = bill.Lines.Single(l => l.ServiceCode == "CSM-DRK");
        var components = invoice.LineItem[2].PriceComponent;

        var cgst = Component(components, "CGST").ShouldNotBeNull();
        var sgst = Component(components, "SGST").ShouldNotBeNull();
        (cgst.Type, cgst.Factor, cgst.Amount.Value, cgst.Code.Coding[0].System).ShouldBe((R4.InvoicePriceComponentType.Tax, 0.06m, kit.CgstAmount, "urn:hc360:price-component"));
        (sgst.Type, sgst.Factor, sgst.Amount.Value).ShouldBe((R4.InvoicePriceComponentType.Tax, 0.06m, kit.SgstAmount));
        (cgst.Amount.Value + sgst.Amount.Value).ShouldBe(35.91m, "12% of the kits after their share of the bill discount (₹299.25)");
        Component(components, "IGST").ShouldBeNull();
        invoice.LineItem.Take(2).SelectMany(i => i.PriceComponent).ShouldNotContain(c => c.Type == R4.InvoicePriceComponentType.Tax, "consultations and tests are exempt");
        Component(invoice.TotalPriceComponent, "CGST")!.Amount.Value.ShouldBe(bill.CgstAmount);
        Component(invoice.TotalPriceComponent, "SGST")!.Amount.Value.ShouldBe(bill.SgstAmount);
    }

    [Fact]
    public void Gst_on_a_supply_to_another_state_is_integrated_at_the_full_rate()
    {
        var draft = AbdmTestData.DraftBill();
        draft.SetPlaceOfSupply("27", "29");
        var bill = AbdmTestData.Finalized(draft);
        var invoice = Single<R4.Invoice>(Build(bill));
        var components = invoice.LineItem[2].PriceComponent;

        var igst = Component(components, "IGST").ShouldNotBeNull();
        (igst.Factor, igst.Amount.Value).ShouldBe((0.12m, 35.91m));
        Component(components, "CGST").ShouldBeNull();
        Component(components, "SGST").ShouldBeNull();
        Component(invoice.TotalPriceComponent, "IGST")!.Amount.Value.ShouldBe(bill.IgstAmount);
        Sum(invoice.TotalPriceComponent).ShouldBe(bill.NetAmount);
    }

    [Fact]
    public void A_bill_free_of_cost_by_law_adds_up_to_nothing_and_never_says_why()
    {
        const string reason = "MLC: survivor of sexual assault (BNSS s.397)";
        var draft = AbdmTestData.DraftBill();
        draft.SetFreeTreatment(reason);
        var bill = AbdmTestData.Finalized(draft);
        var bundle = Build(bill);
        var invoice = Single<R4.Invoice>(bundle);

        bill.NetAmount.ShouldBe(0m);
        invoice.LineItem.ShouldAllBe(i => Sum(i.PriceComponent) == 0m);
        Sum(invoice.TotalPriceComponent).ShouldBe(0m);
        invoice.TotalGross.Value.ShouldBe(0m);
        Component(invoice.LineItem[0].PriceComponent, "not-charged").ShouldNotBeNull().Amount.Value.ShouldBe(-720m);
        FhirJson.Serialize(bundle).ShouldNotContain("BNSS");
        FhirJson.Serialize(bundle).ShouldNotContain("MLC");
    }

    [Fact]
    public void A_bill_is_issued_until_the_patient_has_paid_their_share_and_the_payer_owes_nothing()
    {
        var bill = AbdmTestData.OpBill();
        AbdmRecords.InvoiceStatus(bill).ShouldBe(R4.Invoice.InvoiceStatus.Issued);

        bill.ApplyPayment(500m, allowExcess: false).IsSuccess.ShouldBeTrue();
        bill.Status.ShouldBe(BillStatus.PartiallyPaid);
        AbdmRecords.InvoiceStatus(bill).ShouldBe(R4.Invoice.InvoiceStatus.Issued);

        bill.ApplyPayment(bill.DueAmount, allowExcess: false).IsSuccess.ShouldBeTrue();
        bill.Status.ShouldBe(BillStatus.Paid);
        AbdmRecords.InvoiceStatus(bill).ShouldBe(R4.Invoice.InvoiceStatus.Balanced);
        Single<R4.Invoice>(Build(bill)).Status.ShouldBe(R4.Invoice.InvoiceStatus.Balanced);

        // Insured: the patient's co-pay paid, the insurer's share still owed, then settled.
        var insured = AbdmTestData.DraftBill();
        insured.ApplyPayerTerms(new(PayerType.Insurance, Guid.Parse("01923a4b-0000-7000-8000-0000000ab01e"), null, null, null, 10m, null));
        insured = AbdmTestData.Finalized(insured);
        insured.ApplyPayment(insured.DueAmount, allowExcess: false).IsSuccess.ShouldBeTrue();
        insured.Status.ShouldBe(BillStatus.Paid);
        AbdmRecords.InvoiceStatus(insured).ShouldBe(R4.Invoice.InvoiceStatus.Issued, "the insurer still owes its share");
        var terms = Single<R4.Invoice>(Build(insured)).PaymentTerms;
        terms.ShouldStartWith("Payable by the patient: ₹");
        terms.ShouldContain("Payable by the insurer: ₹");
        Sum(Single<R4.Invoice>(Build(insured)).TotalPriceComponent).ShouldBe(insured.PayerAmount + insured.PatientAmount);
        insured.ApplyPayerSettlement(insured.PayerOutstanding, 0m).IsSuccess.ShouldBeTrue();
        AbdmRecords.InvoiceStatus(insured).ShouldBe(R4.Invoice.InvoiceStatus.Balanced);
    }

    [Fact]
    public void Only_a_finalised_bill_not_cancelled_is_exported()
    {
        AbdmRecordLoader.InvoiceNotExportable(BillStatus.Finalized, AbdmTestData.BillNumber).ShouldBeNull();
        AbdmRecordLoader.InvoiceNotExportable(BillStatus.PartiallyPaid, AbdmTestData.BillNumber).ShouldBeNull();
        AbdmRecordLoader.InvoiceNotExportable(BillStatus.Paid, AbdmTestData.BillNumber).ShouldBeNull();
        AbdmRecordLoader.InvoiceNotExportable(BillStatus.Draft, null).ShouldBe("Only a finalised bill can be exported: a draft is an estimate.");
        AbdmRecordLoader.InvoiceNotExportable(BillStatus.Finalized, null).ShouldBe("Only a finalised bill can be exported: a draft is an estimate.");
        AbdmRecordLoader.InvoiceNotExportable(BillStatus.Cancelled, AbdmTestData.BillNumber).ShouldBe("A cancelled bill is not exported: the patient does not owe it.");
        AbdmRecordLoader.InvoiceNotExportable(BillStatus.Refunded, AbdmTestData.BillNumber).ShouldBe(
            "A cancelled bill is not exported: the patient does not owe it.", "a refunded bill is a cancelled bill whose payments went back");

        // And the document is never made of one.
        var cancelled = AbdmTestData.OpBill();
        cancelled.Cancel("Billed to the wrong patient", userId: null, AbdmTestData.BillFinalized.AddHours(1)).IsSuccess.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => Build(cancelled));
        Should.Throw<InvalidOperationException>(() => Build(AbdmTestData.DraftBill()));
    }

    [Fact]
    public void A_bill_is_linked_as_an_invoice_named_by_its_number_and_sent_under_a_consent_for_invoices()
    {
        var id = AbdmTestData.BillId;

        AbdmGatewayMessages.HiType(AbdmRecordType.Invoice).ShouldBe("Invoice");
        AbdmConsentRequestService.HiTypes.ShouldContain("Invoice");
        AbdmLinkingService.ReferenceNumber(AbdmRecordType.Invoice, id).ShouldBe($"invoice/{id:D}");
        AbdmBundleService.Describe(AbdmRecordType.Invoice).ShouldBe("bill");
        AbdmRecordLoader.NotFound(AbdmRecordType.Invoice, id).Code.ShouldBe("bill.not_found");
        AbdmCareContexts.Describe(AbdmRecordType.Invoice, id, RecordedAt, AbdmTestData.BillNumber).Display
            .ShouldBe($"Bill: {AbdmTestData.BillNumber}, 25 Sep 2026");
        AbdmDataFlowService.Covers("Invoice", AbdmRecordType.Invoice).ShouldBeTrue();
        AbdmDataFlowService.Covers("OPConsultation,Prescription", AbdmRecordType.Invoice).ShouldBeFalse();
        ((int)AbdmRecordType.Invoice).ShouldBe(Enum.GetValues<AbdmRecordType>().Length - 1, "a new kind goes last: the kinds are kept");
    }

    /// <summary>
    /// The guide's InvoiceRecord and Invoice profiles are not in the package: the validator says it could not check them,
    /// as errors naming them, and nothing else. Everything it can check passes: base FHIR R4 for every resource (the
    /// Composition and the Invoice included), the guide's DocumentBundle, Patient, Organization, Practitioner, Encounter
    /// and Binary profiles. Those two errors are what a sandbox validation run must clear.
    /// </summary>
    [Fact]
    public void The_document_meets_base_fhir_and_the_guides_other_profiles_and_the_two_profiles_the_package_lacks_are_reported_unchecked()
    {
        var validation = AbdmProfileValidator.Validate(Build(AbdmTestData.OpBill(), AbdmTestData.Pdf), AbdmProfileValidator.DocumentBundle);

        TestContext.Current.SendDiagnosticMessage(validation.Describe());
        validation.Errors.ShouldNotBeEmpty("a profile the validator does not have is never passed silently");
        validation.Errors.ShouldAllBe(e => IsUnresolved(e, AbdmProfiles.InvoiceRecord) || IsUnresolved(e, AbdmProfiles.Invoice), validation.Describe());
        validation.Errors.ShouldContain(e => IsUnresolved(e, AbdmProfiles.InvoiceRecord), validation.Describe());
        validation.Errors.ShouldContain(e => IsUnresolved(e, AbdmProfiles.Invoice), validation.Describe());
    }

    /// <summary>
    /// Base FHIR R4's rules for the Invoice and the Composition are checked, though the guide's profiles of them are not
    /// available.
    /// </summary>
    [Theory]
    [InlineData("a line of no service", "Missing required member: 'chargeItem")]
    [InlineData("a price component of no type", "Missing required member: 'type'")]
    [InlineData("a participant of no one", "Missing required member: 'actor'")]
    [InlineData("a composition of no title", "Missing required member: 'title'")]
    public void An_invoice_broken_against_base_fhir_fails(string breakage, string expected)
    {
        var bundle = Build(AbdmTestData.OpBill());
        var invoice = Single<R4.Invoice>(bundle);
        switch (breakage)
        {
            case "a line of no service":
                invoice.LineItem[0].ChargeItem = null!;
                break;
            case "a price component of no type":
                invoice.LineItem[0].PriceComponent[0].Type = null;
                break;
            case "a participant of no one":
                invoice.Participant[0].Actor = null!;
                break;
            case "a composition of no title":
                ((R4.Composition)bundle.Entry[0].Resource).Title = null!;
                break;
        }

        var validation = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);

        TestContext.Current.SendDiagnosticMessage($"{breakage}: {validation.Describe()}");
        validation.Errors.ShouldContain(e => (e.Details.Text ?? e.Diagnostics ?? string.Empty).Contains(expected), validation.Describe());
    }

    /// <summary>Firely's error for a profile it does not have: "Unable to resolve reference to profile '…'" (Incomplete).</summary>
    private static bool IsUnresolved(R4.OperationOutcome.IssueComponent issue, string profile) =>
        issue.Code == R4.OperationOutcome.IssueType.Incomplete
        && (issue.Details?.Text ?? issue.Diagnostics ?? string.Empty) == $"Unable to resolve reference to profile '{profile}'.";
}
