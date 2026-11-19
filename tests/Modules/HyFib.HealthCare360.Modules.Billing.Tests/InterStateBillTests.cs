using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.Modules.Billing.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A bill is supplied in the facility's state, whoever pays it: health care where it is performed (IGST Act s.12(4)), a
/// room where the hospital stands (s.12(3)). A payer registered in another state is billed CGST and SGST, with Tamil Nadu
/// as the place of supply, on the bill, its e-invoice and GSTR-1; its GSTIN names it as the buyer, and does not move the
/// supply. (The pharmacy's goods sent to another state are supplied there, s.10: its invoices keep IGST.)
/// </summary>
public sealed class InterStateBillTests
{
    private const string Hospital = "33AABCN1234F1Z3";
    private const string KeralaCompany = "32AAACH7409R1ZA";

    private static readonly BillPayerTerms KeralaPayer = new(PayerType.Corporate, Guid.CreateVersion7(), null, null, null, 0m, null);

    [Fact]
    public void Room_rent_billed_to_a_payer_registered_in_kerala_carries_cgst_and_sgst_with_tamil_nadu_as_the_place_of_supply()
    {
        var bill = RoomRentBill();

        // Not IGST: the place of supply of health care and of a room is the hospital's state, not the payer's.
        (bill.IsInterState, bill.PlaceOfSupply).ShouldBe((false, "33"));
        (bill.CgstAmount, bill.SgstAmount, bill.IgstAmount, bill.TaxAmount).ShouldBe((150m, 150m, 0m, 300m));
        bill.Lines.ShouldAllBe(l => l.IgstAmount == 0m);
        bill.Lines.Single(l => l.TaxRate == 5m).ShouldSatisfyAllConditions(l => l.CgstAmount.ShouldBe(150m), l => l.SgstAmount.ShouldBe(150m));
        (bill.NetAmount, bill.PayerAmount).ShouldBe((7_100m, 7_100m));
    }

    [Fact]
    public void Its_e_invoice_names_the_payer_in_kerala_as_the_buyer_and_tamil_nadu_as_the_place_of_supply_with_cgst_and_sgst()
    {
        var bill = RoomRentBill();

        // The invoice as Billing's e-invoice source makes it of a bill: the payer the buyer, the bill's place of supply.
        var (payload, problems) = InvoiceRegistrationPayload.Build(Seller, EInvoiceOf(bill));

        problems.ShouldBeEmpty("the portal takes CGST and SGST to a buyer in another state when the place of supply is the seller's");
        var json = payload.ShouldNotBeNull();
        var buyer = json["BuyerDtls"]!;
        (buyer["Gstin"]!.GetValue<string>(), buyer["Pos"]!.GetValue<string>(), buyer["Stcd"]!.GetValue<string>()).ShouldBe((KeralaCompany, "33", "32"));
        var room = json["ItemList"]!.AsArray().Single(i => i!["GstRt"]!.GetValue<decimal>() == 5m)!;
        (room["CgstAmt"]!.GetValue<decimal>(), room["SgstAmt"]!.GetValue<decimal>(), room["IgstAmt"]!.GetValue<decimal>()).ShouldBe((150m, 150m, 0m));
        var values = json["ValDtls"]!;
        (values["CgstVal"]!.GetValue<decimal>(), values["SgstVal"]!.GetValue<decimal>(), values["IgstVal"]!.GetValue<decimal>(), values["TotInvVal"]!.GetValue<decimal>())
            .ShouldBe((150m, 150m, 0m, 7_100m));
    }

    [Fact]
    public void In_gstr1_it_is_a_b2b_invoice_to_the_payer_within_tamil_nadu()
    {
        var bill = RoomRentBill();

        var gstr1 = GstReturnBuilder.Gstr1(Hospital, 2026, 9, [GstDocumentOf(bill)]);

        gstr1.Problems.ShouldBeEmpty();
        var invoice = gstr1.B2b.ShouldHaveSingleItem();
        (invoice.BuyerGstin, invoice.PlaceOfSupply).ShouldBe((KeralaCompany, "33"));
        invoice.Items.ShouldHaveSingleItem().ShouldBe(new Gstr1Item(5m, new GstReturnAmounts(6_000m, 0m, 150m, 150m, 0m)));
        gstr1.Nil.ShouldHaveSingleItem().ShouldBe(new Gstr1NilRow("INTRAB2B", 0m, 800m, 0m), "the exempt consultation, within the state");
        var item = GstReturnBuilder.Gstr1Json(gstr1)["b2b"]![0]!["inv"]![0]!;
        item["pos"]!.GetValue<string>().ShouldBe("33");
        var detail = item["itms"]![0]!["itm_det"]!.AsObject();
        (detail["camt"]!.GetValue<decimal>(), detail["samt"]!.GetValue<decimal>(), detail.ContainsKey("iamt")).ShouldBe((150m, 150m, false));
        GstReturnBuilder.Gstr3b(gstr1, []).OutwardTaxable.ShouldBe(new GstReturnAmounts(6_000m, 0m, 150m, 150m, 0m));
    }

    [Fact]
    public void A_service_taxed_at_18_percent_for_a_payer_in_another_state_is_split_between_the_centre_and_the_state()
    {
        // Was IGST at 18% to a Kerala payer; the supply is in Tamil Nadu, so the same ₹180 is CGST and SGST of ₹90 each.
        var bill = DraftBill(KeralaPayer, Charge(1000m, category: ServiceCategory.Miscellaneous, taxRate: 18m));

        bill.SetPlaceOfSupply(facilityState: "33");
        bill.Recalculate();

        (bill.IsInterState, bill.PlaceOfSupply).ShouldBe((false, "33"));
        (bill.CgstAmount, bill.SgstAmount, bill.IgstAmount, bill.NetAmount).ShouldBe((90m, 90m, 0m, 1180m));
    }

    [Fact]
    public void A_facility_without_a_gstin_gives_no_place_of_supply_and_still_cgst_and_sgst()
    {
        var bill = DraftBill(null, Charge(6000m, taxRate: 5m));
        bill.SetPlaceOfSupply(null);
        bill.Recalculate();

        (bill.PlaceOfSupply, bill.IsInterState, bill.CgstAmount, bill.SgstAmount, bill.IgstAmount).ShouldBe((null, false, 150m, 150m, 0m));
    }

    [Fact]
    public void A_finalized_bill_keeps_its_place_of_supply()
    {
        var bill = FinalizedBill(Charge(6000m, taxRate: 5m));
        var before = bill.PlaceOfSupply;
        bill.SetPlaceOfSupply("32");
        (bill.PlaceOfSupply, bill.IsInterState).ShouldBe((before, false));
    }

    [Fact]
    public void Igst_goes_to_its_own_output_ledger_in_the_day_book()
    {
        // Bills carry no IGST now, but the pharmacy's goods sent to another state do, and a bill finalized before still may.
        var map = new TallyLedgerMap();
        map.OutputIgst.ShouldBe("Output IGST");
        TallyLedgerMaps.Normalize(map with { OutputIgst = "  Output IGST 5%  " }).OutputIgst.ShouldBe("Output IGST 5%");
    }

    private static readonly EInvoiceSeller Seller = new(
        Hospital, "Nana Hospitals Private Limited",
        new Address { Line1 = "12 Anna Salai", Line2 = "Teynampet", City = "Chennai", State = "Tamil Nadu", PinCode = "600018" },
        "+914424340000", "accounts@nanahospitals.in");

    /// <summary>
    /// A finalized bill to a company registered in Kerala: a day of a room above ₹5,000 outside intensive care, taxed at 5%
    /// (RoomRentGst), and an exempt consultation; placed as Billing places every bill, in the hospital's state.
    /// </summary>
    private static Bill RoomRentBill()
    {
        var bill = DraftBill(KeralaPayer, Charge(6000m, category: ServiceCategory.BedCharge, room: RoomCharge.Room), Charge(800m, category: ServiceCategory.Consultation));
        bill.Lines.Select(l => l.TaxRate).ShouldBe([5m, 0m]);
        bill.SetPlaceOfSupply(facilityState: Gstin.StateCode(Hospital));
        bill.Recalculate();
        bill.Finalize("NHIB260000001", null, Now).IsSuccess.ShouldBeTrue();
        return bill;
    }

    /// <summary>The bill's e-invoice as <c>BillEInvoiceSource</c> makes it: each line at its taxable value and GST, to the payer.</summary>
    private static EInvoiceDocument EInvoiceOf(Bill bill) => new(
        "bill", bill.Id, bill.FacilityId, bill.BillNumber!, BillingTime.LocalDate(bill.FinalizedAt!.Value),
        new EInvoiceBuyer(KeralaCompany, "Malabar Traders Ltd", "Malabar Traders, 4/12 MG Road, Kochi, Kerala 682016", "9876543210", null),
        bill.PlaceOfSupply!,
        bill.Lines.OrderBy(l => l.SortOrder).Select(l => new EInvoiceItem(
            l.Description, IsService: true, l.SacCode, l.Quantity, l.UnitPrice, l.GrossAmount, l.GrossAmount - l.TaxableAmount, l.TaxableAmount, l.TaxRate,
            l.IgstAmount, l.CgstAmount, l.SgstAmount, l.TaxableAmount + l.IgstAmount + l.CgstAmount + l.SgstAmount)).ToList(),
        bill.RoundOffAmount, bill.NetAmount, IsCancelled: false);

    /// <summary>The bill in GSTR-1 as <c>BillingGstReturnSource</c> reports it: a B2B invoice to the payer, at the bill's place of supply.</summary>
    private static GstReturnDocument GstDocumentOf(Bill bill) => new(
        "bill", bill.Id, GstDocumentKind.Invoice, bill.BillNumber!, BillingTime.LocalDate(bill.FinalizedAt!.Value), KeralaCompany, "Malabar Traders Ltd",
        bill.PlaceOfSupply!, bill.NetAmount, IsCancelled: false,
        bill.Lines.OrderBy(l => l.SortOrder).Select(l => new GstReturnLine(
            l.SacCode, l.Description, IsService: true, "NA", 0m, l.TaxRate, l.TaxRate > 0m ? GstSupplyKind.Taxable : GstSupplyKind.Exempt, l.TaxableAmount,
            l.IgstAmount, l.CgstAmount, l.SgstAmount)).ToList(),
        "IP bills");
}
