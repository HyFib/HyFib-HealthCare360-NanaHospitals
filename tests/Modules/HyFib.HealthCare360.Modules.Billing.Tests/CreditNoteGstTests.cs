using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A credit note takes back the taxable value and GST of the bill lines it names, at each line's own rate (CGST Act
/// s.34), or, issued after the bill's GST could be reduced, the value alone.
/// </summary>
public sealed class CreditNoteGstTests
{
    private static BillLine Line(decimal net, decimal taxable, decimal rate, string description = "Single room") => new()
    {
        Description = description,
        SacCode = "999311",
        Quantity = 1m,
        UnitPrice = net,
        GrossAmount = net,
        TaxRate = rate,
        TaxableAmount = taxable,
        NetAmount = net,
    };

    [Fact]
    public void A_credit_note_takes_back_the_gst_of_each_service_it_names_at_its_own_rate()
    {
        var note = new CreditNote { Kind = CreditNoteKind.CreditNote, Amount = 1100m };

        var room = note.Credit(Line(6300m, 6000m, 5m), 0m, 1050m, interState: false, timeBarred: false);
        var dressing = note.Credit(Line(250m, 250m, 0m, "Dressing"), 0m, 50m, interState: false, timeBarred: false);

        (room.TaxableAmount, room.CgstAmount, room.SgstAmount, room.GstRate).ShouldBe((1000m, 25m, 25m, 5m));
        (dressing.TaxableAmount, dressing.GstAmount).ShouldBe((50m, 0m), "An exempt service gives back no GST.");
        (note.CgstAmount, note.SgstAmount, note.IgstAmount, note.GstAmount).ShouldBe((25m, 25m, 0m, 50m));
        note.TaxableAmount.ShouldBe(1050m);
        note.GstTimeBarred.ShouldBeFalse();
        note.Lines.Select(l => l.SortOrder).ShouldBe([0, 1]);
        note.Lines.ShouldAllBe(l => l.CreditNoteId == note.Id);
    }

    [Fact]
    public void A_line_credited_twice_gives_back_its_own_gst_and_no_more()
    {
        var line = Line(1180m, 1000m, 18m);
        var first = new CreditNote { Kind = CreditNoteKind.CreditNote, Amount = 590m };
        var second = new CreditNote { Kind = CreditNoteKind.CreditNote, Amount = 590m };

        first.Credit(line, 0m, 590m, interState: true, timeBarred: false);
        second.Credit(line, 590m, 590m, interState: true, timeBarred: false);

        (first.IgstAmount + second.IgstAmount).ShouldBe(180m);
        (first.TaxableAmount + second.TaxableAmount).ShouldBe(1000m);
        (first.CgstAmount, second.SgstAmount).ShouldBe((0m, 0m));
    }

    [Fact]
    public void A_credit_note_issued_after_the_bills_gst_could_be_reduced_takes_back_the_value_only()
    {
        var note = new CreditNote { Kind = CreditNoteKind.CreditNote, Amount = 1050m };

        var line = note.Credit(Line(6300m, 6000m, 5m), 0m, 1050m, interState: false, timeBarred: true);

        (line.TaxableAmount, line.GstAmount, line.GstRate).ShouldBe((1050m, 0m, 0m));
        (note.GstAmount, note.TaxableAmount).ShouldBe((0m, 1050m));
        note.GstTimeBarred.ShouldBeTrue();
    }
}
