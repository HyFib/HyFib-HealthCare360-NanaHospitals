using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Documents;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Billing.Tests.Documents;

public sealed class BillDocumentTests
{
    private const string Hostile = "<script>alert(1)</script></style>";

    /// <summary>25 Sep 2026, 12:00 IST.</summary>
    private static readonly DateTimeOffset Created = new(2026, 9, 25, 6, 30, 0, TimeSpan.Zero);

    private static readonly DocumentLetterhead Letterhead = new(
        "Nana Hospitals", "Nana Hospitals Pvt Ltd", "12 MG Road, Pune, Maharashtra, 411001", "+912012345678", "billing@nana.in", "https://nana.in",
        "MH/PUN/2019/0042", "H-2021-0456", "27AAACN1234F1Z5", "AAACN1234F", "Subject to Pune jurisdiction.", ImageData.FromBytes([1, 2, 3], "image/png"));

    [Fact]
    public void An_exempt_bill_is_a_bill_of_supply_and_a_taxed_one_a_tax_invoice()
    {
        BillDocument.Title(Bill()).ShouldBe("Bill of supply");
        BillDocument.Title(Taxed()).ShouldBe("Tax invoice");
        BillDocument.Title(Bill() with { Type = BillType.InPatient, IsFinalBill = true }).ShouldBe("Bill of supply · IP final bill");
        BillDocument.Title(Taxed() with { Type = BillType.InPatient }).ShouldBe("Tax invoice · IP bill");
        BillDocument.Title(Bill() with { Status = BillStatus.Draft }).ShouldBe("Estimate (draft bill)");

        var exempt = Render(Bill());
        exempt.ShouldContain("<div class=\"h-title\">Bill of supply</div>");
        exempt.ShouldContain("This is a computer-generated bill of supply. Subject to Pune jurisdiction.");
        exempt.ShouldNotContain("CGST");
        exempt.ShouldNotContain("GST summary");

        var taxed = Render(Taxed() with { Type = BillType.InPatient, IsFinalBill = true });
        taxed.ShouldContain("<div class=\"h-title\">Tax invoice · IP final bill</div>");
        taxed.ShouldContain("This is a computer-generated tax invoice · IP final bill.");
        taxed.ShouldContain("<th class=\"num\">CGST</th>");
        taxed.ShouldContain("<td class=\"num\">18%</td>");
        taxed.ShouldContain("<td>Taxable value</td><td class=\"num\">₹1,000.00</td>");
        taxed.ShouldContain("<h3>GST summary</h3>");
        taxed.ShouldContain("<tr><td>999312</td><td class=\"num\">₹1,000.00</td><td class=\"num\">9%</td><td class=\"num\">₹90.00</td><td class=\"num\">9%</td><td class=\"num\">₹90.00</td></tr>");
    }

    [Fact]
    public void A_draft_says_draft_and_a_cancelled_bill_says_cancelled_across_the_page()
    {
        var draft = Render(Bill() with { Status = BillStatus.Draft, BillNumber = null, FinalizedAt = null, PaidAmount = 0m, DueAmount = 500m, Receipts = [] });
        draft.ShouldContain("<div class=\"stamp\">DRAFT</div>");
        draft.ShouldContain("<div class=\"h-title\">Estimate (draft bill)</div>");
        draft.ShouldContain("<div>Bill no.: <b>Draft</b></div>");
        draft.ShouldContain("<div>Date: <b>25 Sep 2026, 12:00 pm</b></div>");
        draft.ShouldNotContain("<td>Paid</td>");
        draft.ShouldNotContain("Balance due");

        var cancelled = Render(Bill() with { Status = BillStatus.Cancelled, CancelledAt = Created.AddMinutes(30), CancelReason = "Wrong patient" });
        cancelled.ShouldContain("<div class=\"stamp\">CANCELLED</div>");
        cancelled.ShouldContain("<div class=\"box\"><b>Cancelled</b> on 25 Sep 2026, 12:30 pm: Wrong patient</div>");
        cancelled.ShouldContain("Status: Cancelled");

        var refunded = Render(Bill() with { Status = BillStatus.Refunded, CancelledAt = Created.AddMinutes(30) });
        refunded.ShouldContain("<div class=\"stamp\">CANCELLED</div>");
        refunded.ShouldContain("Status: Cancelled, refunded");

        Render(Bill()).ShouldNotContain("class=\"stamp\"");
    }

    [Fact]
    public void Totals_split_the_bill_between_payer_and_patient_in_indian_money_format()
    {
        var bill = Bill() with
        {
            Status = BillStatus.PartiallyPaid,
            GrossAmount = 123456.50m,
            PackageCoveredAmount = 1000m,
            Package = new BillPackageSummary(Guid.NewGuid(), "PKG-CATARACT", "Cataract surgery (one eye)", 25000m, 1000m, 1000m, 0m),
            TariffDiscountAmount = 456.50m,
            LineDiscountAmount = 1000m,
            BillDiscountAmount = 500m,
            DiscountReason = "Senior citizen",
            NetAmount = 120500m,
            PayerType = PayerType.Insurance,
            PayerName = "Star Health",
            CoPayPercent = 10m,
            PayerAmount = 100000m,
            PatientAmount = 20500m,
            PaidAmount = 20000m,
            DueAmount = 500m,
        };

        BillDocument.Totals(bill).Select(r => (r.Label, r.Amount, r.Strong)).ShouldBe(
        [
            ("Gross amount", 123456.50m, false),
            ("Less: covered by package", -1000m, false),
            ("Less: tariff concession", -456.50m, false),
            ("Less: discount (Senior citizen)", -1500m, false),
            ("Net amount", 120500m, true),
            ("Payable by Star Health", 100000m, false),
            ("Payable by patient", 20500m, true),
            ("Paid", 20000m, false),
            ("Balance due", 500m, true),
        ]);

        var html = Render(bill);
        html.ShouldContain("<tr><td>Gross amount</td><td class=\"num\">₹1,23,456.50</td></tr>");
        html.ShouldContain("<tr><td>Less: covered by package</td><td class=\"num\">-₹1,000.00</td></tr>");
        html.ShouldContain("<tr class=\"strong\"><td>Payable by patient</td><td class=\"num\">₹20,500.00</td></tr>");
        html.ShouldContain("<tr class=\"strong\"><td>Balance due</td><td class=\"num\">₹500.00</td></tr>");
        html.ShouldContain("Payable by patient: Rupees Twenty Thousand Five Hundred Only");
        html.ShouldContain("<div>Payer: Star Health</div>");
        html.ShouldContain("<div>Co-pay: 10%</div>");
        html.ShouldContain("<div>Package: Cataract surgery (one eye)</div>");
        html.ShouldContain("<div>Status: Partly paid</div>");
    }

    [Fact]
    public void A_tax_invoice_adds_its_gst_and_a_refund_due_replaces_the_balance()
    {
        var bill = Taxed() with { RoundOffAmount = -0.40m, CreditAmount = 100m, RefundedAmount = 200m, RefundableAmount = 50m };

        BillDocument.Totals(bill).Select(r => r.Label).ShouldBe(
            ["Gross amount", "Taxable value", "CGST", "SGST", "Round off", "Net amount", "Credit notes / write-off", "Paid", "Refunded", "Refundable to patient"]);
        Render(bill).ShouldContain("<tr><td>Round off</td><td class=\"num\">-₹0.40</td></tr>");
    }

    [Fact]
    public void The_header_carries_the_hospital_tax_ids_and_logo_beside_the_bill_number()
    {
        var html = Render(Bill());

        html.ShouldContain("<img class=\"h-logo\" src=\"data:image/png;base64,AQID\" alt=\"\">");
        html.ShouldContain("<div class=\"h-name\">Nana Hospitals Pvt Ltd</div>");
        html.ShouldContain("12 MG Road, Pune, Maharashtra, 411001");
        html.ShouldContain(Html.Encode("Phone +912012345678 · billing@nana.in"));
        html.ShouldContain("GSTIN 27AAACN1234F1Z5 · PAN AAACN1234F");
        html.ShouldContain("Registration MH/PUN/2019/0042");
        html.ShouldContain("<div>Bill no.: <b>NHOB260000001</b></div>");
        html.ShouldContain("<div>Date: <b>25 Sep 2026, 12:05 pm</b></div>");
        html.ShouldContain("<div>Patient: <b>Ravi Kumar</b></div>");
        html.ShouldContain("<div>Payer: Self pay</div>");
        html.ShouldContain("<div>Tariff: General</div>");
        html.ShouldNotContain("Package:");

        var plain = BillDocument.Build(Bill() with { PayerType = PayerType.GovernmentScheme }, Letterhead with { LegalName = null, Logo = null, Pan = null }).ToHtml();
        plain.ShouldContain("<div class=\"h-name\">Nana Hospitals</div>");
        plain.ShouldContain("<div class=\"muted\">GSTIN 27AAACN1234F1Z5</div>");
        plain.ShouldContain("<div>Payer: Government scheme</div>");
        plain.ShouldNotContain("<img");
    }

    [Fact]
    public void On_pre_printed_stationery_the_bill_keeps_its_tax_ids_and_number_and_leaves_the_rest_to_the_paper()
    {
        var template = new PrintTemplate
        {
            Letterhead = PrintLetterhead.Preprinted,
            TopSpaceMm = 40,
            ClosingNote = "Goods once sold are not taken back.",
        };

        var html = BillDocument.Build(Bill(), Letterhead with { Template = template }).ToHtml();

        html.ShouldNotContain("class=\"h-name\"");
        html.ShouldNotContain("12 MG Road");
        html.ShouldNotContain("<img");
        html.ShouldContain("GSTIN 27AAACN1234F1Z5 · PAN AAACN1234F", Case.Sensitive, "a tax invoice shows the tax ids whatever the paper has on it");
        html.ShouldContain("<div>Bill no.: <b>NHOB260000001</b></div>");
        html.ShouldContain("margin: 52mm 12mm 16mm;");
        html.ShouldContain("<div class=\"closing-note\">Goods once sold are not taken back.</div>");
    }

    [Fact]
    public void Text_from_records_is_encoded_everywhere()
    {
        var bill = Bill() with
        {
            Status = BillStatus.Cancelled,
            CancelledAt = Created,
            CancelReason = Hostile,
            PatientName = Hostile,
            Uhid = Hostile,
            PayerName = Hostile,
            PayerAmount = 100m,
            TariffPlanName = Hostile,
            DiscountReason = Hostile,
            BillDiscountAmount = 10m,
            Notes = $"First line\n{Hostile}",
            Lines = [Line() with { Description = Hostile, ServiceCode = Hostile, PractitionerName = Hostile, SacCode = Hostile }],
            Receipts = [Receipt() with { ReceiptNumber = Hostile, Modes = Hostile }],
        };
        var letterhead = Letterhead with { LegalName = Hostile, Address = Hostile, Email = Hostile, Gstin = Hostile, RegistrationNumber = Hostile, Note = Hostile };

        var document = BillDocument.Build(bill, letterhead);
        var html = document.ToHtml();

        html.ShouldNotContain("<script>");
        html.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;");
        html.ShouldContain("First line<br>&lt;script&gt;");
        html.Split("</style>").Length.ShouldBe(2);
        document.FileName.ShouldBe("bill-NHOB260000001");
    }

    [Fact]
    public void Every_page_names_the_patient_and_the_bill_and_the_file_is_named_after_the_bill()
    {
        var document = BillDocument.Build(Bill(), Letterhead);

        document.Footer.ShouldBe("Ravi Kumar · UHID NH26000123 · Bill NHOB260000001");
        document.ToHtml().ShouldContain("@bottom-left { content: \"Ravi Kumar · UHID NH26000123 · Bill NHOB260000001\"");
        document.FileName.ShouldBe("bill-NHOB260000001");
        document.Title.ShouldBe("Bill of supply NHOB260000001");
        document.ToDto().PageSize.ShouldBe("A4");

        var draft = BillDocument.Build(Bill() with { Status = BillStatus.Draft, BillNumber = null, FinalizedAt = null }, Letterhead);
        draft.FileName.ShouldBe("bill-draft-NH26000123-2026-09-25");
        draft.Title.ShouldBe("Estimate (draft bill)");
        draft.Footer.ShouldBe("Ravi Kumar · UHID NH26000123 · Estimate (draft bill)");
    }

    [Fact]
    public void Lines_show_returns_packages_and_quantities_as_the_counter_keys_them()
    {
        var bill = Bill() with
        {
            Lines =
            [
                Line() with { Quantity = 1.500m, PackageCoveredAmount = 200m },
                Line() with
                {
                    ServiceCode = "PH-PCM500", Description = "Paracetamol 500 mg", Quantity = -2.000m, UnitPrice = 20m, GrossAmount = -40m, TaxableAmount = -40m,
                    NetAmount = -40m, PractitionerName = null, IsReturn = true,
                },
            ],
        };

        var html = Render(bill);

        html.ShouldContain("<th class=\"num\">Package</th>");
        html.ShouldContain("<td class=\"num\">1.5</td>");
        html.ShouldContain("<td class=\"num\">₹200.00</td>");
        html.ShouldContain("<td class=\"num\">-2</td>");
        html.ShouldContain("<td>Paracetamol 500 mg<span class=\"tag\">Return</span><div class=\"muted\">PH-PCM500 · 25 Sep 2026</div></td>");
        html.ShouldContain("<div class=\"muted\">CONS-NEW · Dr. Asha Rao · 25 Sep 2026</div>");
        html.ShouldContain("<td class=\"num\">-₹40.00</td>");
        Render(Bill()).ShouldNotContain("<th class=\"num\">Package</th>");
    }

    [Fact]
    public void Payments_received_list_every_receipt_with_its_modes_in_words()
    {
        var html = Render(Bill() with { Receipts = [Receipt(), Receipt() with { ReceiptNumber = "NHRC260000002", Modes = "Advance", Amount = 1000m }] });

        html.ShouldContain("<h3>Payments received</h3>");
        html.ShouldContain($"<tr><td>NHRC260000001</td><td>25 Sep 2026, 12:06 pm</td><td>{Html.Encode("Cash + UPI")}</td><td class=\"num\">₹500.00</td></tr>");
        html.ShouldContain("<td>Advance adjusted</td><td class=\"num\">₹1,000.00</td>");
        Render(Bill() with { Receipts = [] }).ShouldNotContain("Payments received");

        BillDocument.ModesLabel("NetBanking + Cheque + Unknown").ShouldBe("Bank transfer + Cheque + Unknown");
        BillDocument.ModesLabel(null).ShouldBe("—");
    }

    [Fact]
    public void Gst_is_summed_by_sac_code_and_rate()
    {
        var rows = BillDocument.GstSummary(
        [
            Line() with { TaxRate = 18m, TaxableAmount = 100m, CgstAmount = 9m, SgstAmount = 9m },
            Line() with { TaxRate = 18.00m, TaxableAmount = 50.5m, CgstAmount = 4.55m, SgstAmount = 4.54m },
            Line() with { SacCode = "999311", TaxRate = 0m },
            Line() with { SacCode = null, TaxRate = 5m, TaxableAmount = 200m, CgstAmount = 5m, SgstAmount = 5m },
        ]);

        rows.ShouldBe([new GstSummaryRow("999312", 18m, 150.5m, 13.55m, 13.54m), new GstSummaryRow("—", 5m, 200m, 5m, 5m)]);
    }

    [Theory]
    [InlineData("500", "Rupees Five Hundred Only")]
    [InlineData("1250.50", "Rupees One Thousand Two Hundred Fifty and Fifty Paise Only")]
    [InlineData("12345678.05", "Rupees One Crore Twenty Three Lakh Forty Five Thousand Six Hundred Seventy Eight and Five Paise Only")]
    [InlineData("1000000000", "Rupees One Hundred Crore Only")]
    [InlineData("0.004", "Rupees Zero Only")]
    [InlineData("-100", "Minus Rupees One Hundred Only")]
    public void Amounts_are_written_in_words_on_the_indian_system(string amount, string words) =>
        AmountInWords.Rupees(decimal.Parse(amount, CultureInfo.InvariantCulture)).ShouldBe(words);

    private static string Render(BillDetail bill) => BillDocument.Build(bill, Letterhead).ToHtml();

    private static BillDetail Taxed()
    {
        var line = Line() with { UnitPrice = 1000m, GrossAmount = 1000m, TaxRate = 18m, TaxableAmount = 1000m, CgstAmount = 90m, SgstAmount = 90m, NetAmount = 1180m };
        return Bill() with
        {
            IsTaxInvoice = true,
            Lines = [line],
            GrossAmount = 1000m,
            TaxableAmount = 1000m,
            CgstAmount = 90m,
            SgstAmount = 90m,
            TaxAmount = 180m,
            NetAmount = 1180m,
            PatientAmount = 1180m,
            PaidAmount = 1180m,
        };
    }

    // A paid OP consultation bill, as BillQueries.DetailAsync returns it.
    private static BillDetail Bill() => new(
        Id: Guid.NewGuid(), BillNumber: "NHOB260000001", Type: BillType.OutPatient, IsFinalBill: false, Status: BillStatus.Paid, FacilityId: Guid.NewGuid(),
        PatientId: Guid.NewGuid(), Uhid: "NH26000123", PatientName: "Ravi Kumar", VisitId: Guid.NewGuid(), AdmissionId: null, IpAccountId: null,
        PayerType: PayerType.Self, PayerId: null, PayerName: null, TariffPlanId: null, TariffPlanName: "General", PolicyId: null, PreAuthorizationId: null,
        CoPayPercent: 0m, PayerCoverageLimit: null, BillDiscountPercent: null, BillDiscountValue: null, DiscountReason: null, ApprovedDiscountAmount: 0m,
        GrossAmount: 500m, PackageCoveredAmount: 0m, TariffDiscountAmount: 0m, LineDiscountAmount: 0m, BillDiscountAmount: 0m, DiscountAmount: 0m,
        TaxableAmount: 500m, CgstAmount: 0m, SgstAmount: 0m, TaxAmount: 0m, RoundOffAmount: 0m, NetAmount: 500m, PayerAmount: 0m, PatientAmount: 500m,
        CoPayAmount: 0m, NonPayableAmount: 0m, PaidAmount: 500m, RefundedAmount: 0m, CreditAmount: 0m, DueAmount: 0m, RefundableAmount: 0m,
        PayerSettledAmount: 0m, PayerDisallowedAmount: 0m, IsTaxInvoice: false, CreatedAt: Created, FinalizedAt: Created.AddMinutes(5), CancelledAt: null,
        CancelReason: null, Notes: null, Lines: [Line()], Receipts: [Receipt()], Refunds: [], CreditNotes: [], PendingDiscount: null, Package: null,
        Version: 3);

    private static BillLineDto Line() => new(
        Id: Guid.NewGuid(), ChargeId: Guid.NewGuid(), ServiceItemId: Guid.NewGuid(), ServiceCode: "CONS-NEW", Description: "Consultation",
        Category: ServiceCategory.Consultation, SacCode: "999312", Quantity: 1.000m, UnitPrice: 500m, GrossAmount: 500m, PackageCoveredAmount: 0m,
        TariffDiscountAmount: 0m, LineDiscountAmount: 0m, BillDiscountShare: 0m, TaxRate: 0m, TaxableAmount: 500m, CgstAmount: 0m, SgstAmount: 0m,
        NetAmount: 500m, IsDiscountAllowed: true, IsPayerPayable: true, PractitionerId: null, PractitionerName: "Dr. Asha Rao", DepartmentId: null,
        ServiceDate: Created, IsReturn: false);

    private static ReceiptSummary Receipt() => new(Guid.NewGuid(), "NHRC260000001", ReceiptPurpose.BillPayment, 500m, "Cash + Upi", Created.AddMinutes(6));
}
