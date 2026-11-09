using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// GSTR-1 and GSTR-3B built from a worked month: a Tamil Nadu hospital's October 2026, with an OP bill to a patient (an
/// exempt consultation and a taxable cosmetic procedure), a cancelled OP bill, an IP bill to a Karnataka corporate
/// registered for GST, a pharmacy sale of ₹1,20,000 to a Kerala patient, its return, a credit note on the OP bill, a
/// debit note to the corporate, and two purchases. Each figure below is worked out by hand in the comments.
/// </summary>
public sealed class GstReturnTests
{
    private const string Hospital = "33AABCN1234F1Z3";
    private const string Corporate = "29AAACI1681G1ZM";
    private static readonly DateOnly Oct5 = new(2026, 10, 5);

    private static GstReturnLine Service(string sac, string what, decimal rate, decimal taxable, decimal igst = 0m, decimal cgst = 0m, decimal sgst = 0m) =>
        new(sac, what, true, "NA", 0m, rate, rate > 0 ? GstSupplyKind.Taxable : GstSupplyKind.Exempt, taxable, igst, cgst, sgst);

    private static GstReturnLine Medicine(decimal quantity, decimal taxable, decimal igst) =>
        new("3004", "Amoxicillin 500", false, "NOS", quantity, 12m, GstSupplyKind.Taxable, taxable, igst, 0m, 0m);

    private static readonly IReadOnlyList<GstReturnDocument> Month =
    [
        // OP bill to a patient, within the state: consultation ₹500 exempt; procedure ₹1,000 + CGST 90 + SGST 90. Value 1,680.
        new("bill", Guid.NewGuid(), GstDocumentKind.Invoice, "NHOB26000001", Oct5, null, "Patient", "33", 1680m, false,
            [Service("999312", "Consultation", 0m, 500m), Service("999722", "Cosmetic procedure", 18m, 1000m, cgst: 90m, sgst: 90m)], "OP bills"),
        new("bill", Guid.NewGuid(), GstDocumentKind.Invoice, "NHOB26000002", Oct5, null, "Patient", "33", 300m, true,
            [Service("999312", "Consultation", 0m, 300m)], "OP bills"),

        // IP bill to the Karnataka corporate: room and care ₹2,000 exempt; ₹10,000 at 18% IGST 1,800. Value 13,800.
        new("bill", Guid.NewGuid(), GstDocumentKind.Invoice, "NHIB26000001", Oct5, Corporate, "Infosys Limited", "29", 13800m, false,
            [Service("999312", "Bed charges", 0m, 2000m), Service("999722", "Cosmetic procedure", 18m, 10000m, igst: 1800m)], "IP bills"),

        // Pharmacy sale to a Kerala patient, ₹1,20,000 at 12%: 1,07,142.86 + IGST 12,857.14. Above ₹1,00,000 and inter-state: B2CL.
        new("pharmacy", Guid.NewGuid(), GstDocumentKind.Invoice, "NHPH26000001", Oct5, null, "Patient", "32", 120000m, false,
            [Medicine(10m, 107142.86m, 12857.14m)], "Pharmacy invoices"),

        // Its return of one strip, ₹11,200: 10,000 + IGST 1,200. The invoice was B2CL, so the note is CDNUR.
        new("pharmacyreturn", Guid.NewGuid(), GstDocumentKind.CreditNote, "NHPHR2600001", new DateOnly(2026, 10, 9), null, "Patient", "32", 11200m, false,
            [Medicine(1m, 10000m, 1200m)], "Pharmacy returns", "NHPH26000001", Oct5, 120000m, "32"),

        // Credit note on the OP bill, ₹118: 100 + CGST 9 + SGST 9. The bill was B2CS, so the note nets into B2CS.
        new("creditnote", Guid.NewGuid(), GstDocumentKind.CreditNote, "NHCN26000001", new DateOnly(2026, 10, 12), null, "Patient", "33", 118m, false,
            [Service("999722", "Cosmetic procedure", 18m, 100m, cgst: 9m, sgst: 9m)], "Credit notes", "NHOB26000001", Oct5, 1680m, "33"),

        // Debit note to the corporate, ₹590: 500 + IGST 90. CDNR, type D.
        new("debitnote", Guid.NewGuid(), GstDocumentKind.DebitNote, "NHDB26000001", new DateOnly(2026, 10, 20), Corporate, "Infosys Limited", "29", 590m, false,
            [Service("999722", "Cosmetic procedure", 18m, 500m, igst: 90m)], "Debit notes", "NHIB26000001", Oct5, 13800m, "29"),
    ];

    private static Gstr1Return Gstr1 => GstReturnBuilder.Gstr1(Hospital, 2026, 10, Month);

    [Fact]
    public void B2b_lists_the_registered_buyers_invoice_by_rate()
    {
        var invoice = Gstr1.B2b.ShouldHaveSingleItem();
        invoice.Number.ShouldBe("NHIB26000001");
        invoice.BuyerGstin.ShouldBe(Corporate);
        invoice.Value.ShouldBe(13800m);
        invoice.Items.ShouldHaveSingleItem().ShouldBe(new Gstr1Item(18m, new GstReturnAmounts(10000m, 1800m, 0m, 0m, 0m)));
    }

    [Fact]
    public void An_inter_state_invoice_to_a_patient_above_one_lakh_is_b2cl_and_its_return_cdnur()
    {
        Gstr1.B2cl.ShouldHaveSingleItem().Items.ShouldHaveSingleItem().Amounts.ShouldBe(new GstReturnAmounts(107142.86m, 12857.14m, 0m, 0m, 0m));
        var note = Gstr1.Cdnur.ShouldHaveSingleItem();
        note.Number.ShouldBe("NHPHR2600001");
        note.Items.ShouldHaveSingleItem().Amounts.ShouldBe(new GstReturnAmounts(10000m, 1200m, 0m, 0m, 0m));
    }

    [Fact]
    public void B2cs_nets_the_patients_credit_note_into_the_row_of_its_place_and_rate()
    {
        // 1,000 − 100 = 900; CGST 90 − 9 = 81; SGST 81.
        Gstr1.B2cs.ShouldHaveSingleItem().ShouldBe(new Gstr1B2csRow(false, "33", 18m, new GstReturnAmounts(900m, 0m, 81m, 81m, 0m)));
    }

    [Fact]
    public void Cdnr_holds_the_debit_note_to_the_registered_buyer()
    {
        var note = Gstr1.Cdnr.ShouldHaveSingleItem();
        note.Kind.ShouldBe(GstDocumentKind.DebitNote);
        note.Items.ShouldHaveSingleItem().Amounts.ShouldBe(new GstReturnAmounts(500m, 90m, 0m, 0m, 0m));
    }

    [Fact]
    public void Table_8_holds_the_exempt_healthcare_services_and_leaves_out_the_cancelled_bill()
    {
        Gstr1.Nil.ShouldBe([new Gstr1NilRow("INTRB2B", 0m, 2000m, 0m), new Gstr1NilRow("INTRAB2C", 0m, 500m, 0m)]);
    }

    [Fact]
    public void The_hsn_summary_splits_b2b_from_b2c_and_takes_credit_notes_off()
    {
        Gstr1.HsnB2b.Select(r => (r.Hsn, r.Rate, r.Amounts)).ShouldBe([
            ("999312", 0m, new GstReturnAmounts(2000m, 0m, 0m, 0m, 0m)),
            ("999722", 18m, new GstReturnAmounts(10500m, 1890m, 0m, 0m, 0m)), // 10,000 + the debit note's 500
        ]);
        Gstr1.HsnB2c.Select(r => (r.Hsn, r.Uqc, r.Quantity, r.Rate, r.Amounts)).ShouldBe([
            ("3004", "NOS", 9m, 12m, new GstReturnAmounts(97142.86m, 11657.14m, 0m, 0m, 0m)), // 10 sold − 1 returned
            ("999312", "NA", 0m, 0m, new GstReturnAmounts(500m, 0m, 0m, 0m, 0m)),
            ("999722", "NA", 0m, 18m, new GstReturnAmounts(900m, 0m, 81m, 81m, 0m)),
        ]);
    }

    [Fact]
    public void Documents_issued_count_each_series_with_its_cancelled_ones()
    {
        Gstr1.DocumentsIssued.Select(d => (d.Nature, d.Series, d.From, d.To, d.Total, d.Cancelled, d.Net)).ShouldBe([
            (1, "IP bills", "NHIB26000001", "NHIB26000001", 1, 0, 1),
            (1, "OP bills", "NHOB26000001", "NHOB26000002", 2, 1, 1),
            (1, "Pharmacy invoices", "NHPH26000001", "NHPH26000001", 1, 0, 1),
            (4, "Debit notes", "NHDB26000001", "NHDB26000001", 1, 0, 1),
            (5, "Credit notes", "NHCN26000001", "NHCN26000001", 1, 0, 1),
            (5, "Pharmacy returns", "NHPHR2600001", "NHPHR2600001", 1, 0, 1),
        ]);
    }

    [Fact]
    public void Gstr3b_adds_the_outward_supplies_and_takes_credit_only_with_a_supplier_gstin()
    {
        GstPurchase[] purchases =
        [
            new("grn", Guid.NewGuid(), "33AAACC1206D1ZM", "Chennai Pharma", "CP/991", Oct5, 50000m, 0m, 3000m, 3000m),
            new("grn", Guid.NewGuid(), null, "Local vendor", "77", Oct5, 1000m, 0m, 0m, 0m),
        ];
        var r = GstReturnBuilder.Gstr3b(Gstr1, purchases);

        // 3.1(a): B2B 10,000 + B2CL 1,07,142.86 + B2CS 900 + debit 500 − credit 10,000 = 1,08,542.86;
        // IGST 1,800 + 12,857.14 + 90 − 1,200 = 13,547.14; CGST and SGST 81 each.
        r.OutwardTaxable.ShouldBe(new GstReturnAmounts(108542.86m, 13547.14m, 81m, 81m, 0m));
        r.NilAndExempt.ShouldBe(2500m);
        r.InterStateToUnregistered.ShouldHaveSingleItem().ShouldBe(new Gstr1B2csRow(true, "32", 0m, new GstReturnAmounts(97142.86m, 11657.14m, 0m, 0m, 0m)));
        r.ItcAllOther.ShouldBe(new GstReturnAmounts(50000m, 0m, 3000m, 3000m, 0m));
        r.PurchasesWithoutGstin.ShouldBe(1);

        // 2,500 of 1,11,042.86: the share of turnover CGST rule 42 reverses common credit by.
        r.ExemptShareOfTurnover.ShouldBe(0.0225m);
        r.Notes.ShouldContain(n => n.Contains("rule 42", StringComparison.Ordinal));
    }

    [Fact]
    public void Before_august_2024_the_b2cl_limit_was_two_and_a_half_lakh()
    {
        var july = new GstReturnDocument("pharmacy", Guid.NewGuid(), GstDocumentKind.Invoice, "NHPH24000009", new DateOnly(2024, 7, 31), null, "Patient", "32",
            120000m, false, [Medicine(10m, 107142.86m, 12857.14m)], "Pharmacy invoices");
        var r = GstReturnBuilder.Gstr1(Hospital, 2024, 7, [july]);
        r.B2cl.ShouldBeEmpty();
        r.B2cs.ShouldHaveSingleItem().InterState.ShouldBeTrue();
    }

    [Fact]
    public void A_missing_hsn_or_place_of_supply_is_a_problem()
    {
        var bill = new GstReturnDocument("bill", Guid.NewGuid(), GstDocumentKind.Invoice, "NHOB26000009", Oct5, null, "Patient", "", 100m, false,
            [Service("", "Dressing", 0m, 100m)], "OP bills");
        var problems = GstReturnBuilder.Gstr1(Hospital, 2026, 10, [bill]).Problems;
        problems.ShouldContain("NHOB26000009: Dressing has no SAC code; table 12 needs one.");
        problems.ShouldContain("NHOB26000009: no place of supply; taken as the hospital's own state (33).");
    }

    [Fact]
    public void The_offline_tool_json_carries_the_period_and_each_table()
    {
        var json = GstReturnBuilder.Gstr1Json(Gstr1);
        json["gstin"]!.GetValue<string>().ShouldBe(Hospital);
        json["fp"]!.GetValue<string>().ShouldBe("102026");
        var invoice = json["b2b"]![0]!["inv"]![0]!;
        invoice["inum"]!.GetValue<string>().ShouldBe("NHIB26000001");
        invoice["idt"]!.GetValue<string>().ShouldBe("05-10-2026");
        invoice["itms"]![0]!["itm_det"]!["iamt"]!.GetValue<decimal>().ShouldBe(1800m);
        json["b2cl"]![0]!["pos"]!.GetValue<string>().ShouldBe("32");
        json["b2cs"]![0]!["sply_ty"]!.GetValue<string>().ShouldBe("INTRA");
        json["cdnr"]![0]!["nt"]![0]!["ntty"]!.GetValue<string>().ShouldBe("D");
        json["cdnur"]![0]!["typ"]!.GetValue<string>().ShouldBe("B2CL");
        json["nil"]!["inv"]!.AsArray().Count.ShouldBe(2);
        json["hsn"]!["hsn_b2c"]!.AsArray().Count.ShouldBe(3);
        json["doc_issue"]!["doc_det"]!.AsArray().Select(d => d!["doc_num"]!.GetValue<int>()).ShouldBe([1, 4, 5]);
    }
}
