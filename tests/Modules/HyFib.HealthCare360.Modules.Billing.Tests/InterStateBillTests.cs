using HyFib.HealthCare360.Modules.Billing.Domain;
using static HyFib.HealthCare360.Modules.Billing.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>A bill to a payer registered in another state carries IGST in place of CGST and SGST, at the same total.</summary>
public sealed class InterStateBillTests
{
    [Fact]
    public void A_payer_in_another_state_is_billed_igst_for_the_same_tax()
    {
        var bill = DraftBill(null, Charge(6000m, taxRate: 5m), Charge(800m));
        var intra = (bill.CgstAmount, bill.SgstAmount, bill.TaxAmount, bill.NetAmount);
        intra.CgstAmount.ShouldBe(150m);

        bill.SetPlaceOfSupply(facilityState: "33", payerState: "32");
        bill.Recalculate();

        (bill.IsInterState, bill.PlaceOfSupply).ShouldBe((true, "32"));
        (bill.CgstAmount, bill.SgstAmount, bill.IgstAmount).ShouldBe((0m, 0m, 300m));
        (bill.TaxAmount, bill.NetAmount).ShouldBe((intra.TaxAmount, intra.NetAmount));
        bill.Lines.Sum(l => l.IgstAmount).ShouldBe(300m);
        bill.Lines.ShouldAllBe(l => l.CgstAmount == 0 && l.SgstAmount == 0);
    }

    [Fact]
    public void A_payer_in_the_same_state_or_without_a_gstin_is_billed_cgst_and_sgst()
    {
        var bill = DraftBill(null, Charge(6000m, taxRate: 5m));
        bill.SetPlaceOfSupply("33", "33");
        bill.Recalculate();
        (bill.IsInterState, bill.CgstAmount, bill.SgstAmount, bill.IgstAmount).ShouldBe((false, 150m, 150m, 0m));

        bill.SetPlaceOfSupply("33", null);
        bill.Recalculate();
        (bill.IsInterState, bill.PlaceOfSupply).ShouldBe((false, "33"));
    }

    [Fact]
    public void A_finalized_bill_keeps_its_place_of_supply()
    {
        var bill = FinalizedBill(Charge(6000m, taxRate: 5m));
        var before = bill.PlaceOfSupply;
        bill.SetPlaceOfSupply("33", "32");
        (bill.PlaceOfSupply, bill.IsInterState).ShouldBe((before, false));
    }

    [Fact]
    public void Igst_goes_to_its_own_output_ledger_in_the_day_book()
    {
        var map = new TallyLedgerMap();
        map.OutputIgst.ShouldBe("Output IGST");
        TallyLedgerMaps.Normalize(map with { OutputIgst = "  Output IGST 5%  " }).OutputIgst.ShouldBe("Output IGST 5%");
    }
}
