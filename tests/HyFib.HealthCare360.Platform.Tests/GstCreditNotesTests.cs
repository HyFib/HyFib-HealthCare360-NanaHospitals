using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// A GST credit note (CGST Act s.34): until when it can reduce a supply's GST, and how a credit on a line splits into
/// taxable value and GST, so a line's credits always add up to its own taxable value and GST.
/// </summary>
public sealed class GstCreditNotesTests
{
    [Theory]
    [InlineData("2026-04-01", "2027-11-30")]
    [InlineData("2027-03-31", "2027-11-30")]
    [InlineData("2026-03-31", "2026-11-30")]
    [InlineData("2026-12-15", "2027-11-30")]
    public void A_supplys_gst_can_be_reduced_until_30_november_after_its_financial_year(string supplied, string lastDay) =>
        GstCreditNotes.LastDay(DateOnly.Parse(supplied, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(DateOnly.Parse(lastDay, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void A_whole_line_credited_gives_back_its_own_taxable_value_and_gst()
    {
        // Room rent above ₹5,000 at 5%: ₹6,000 taxable, ₹300 GST in halves.
        var share = GstCreditNotes.Share(6300m, 6000m, 0m, 6300m, interState: false);

        share.ShouldBe(new GstShare(6000m, 150m, 150m, 0m));
        share.Gst.ShouldBe(300m);
    }

    [Fact]
    public void A_part_of_a_line_gives_back_gst_in_proportion_at_the_lines_rate()
    {
        var share = GstCreditNotes.Share(1180m, 1000m, 0m, 118m, interState: false);

        (share.Taxable, share.Gst).ShouldBe((100m, 18m));
        (share.Cgst, share.Sgst).ShouldBe((9m, 9m));
    }

    [Fact]
    public void To_another_state_the_gst_given_back_is_igst()
    {
        var share = GstCreditNotes.Share(1180m, 1000m, 0m, 590m, interState: true);

        share.ShouldBe(new GstShare(500m, 0m, 0m, 90m));
    }

    [Fact]
    public void An_exempt_line_gives_back_no_gst()
    {
        var share = GstCreditNotes.Share(250m, 250m, 0m, 100m, interState: false);

        share.ShouldBe(new GstShare(100m, 0m, 0m, 0m));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void However_a_line_is_credited_its_credits_add_up_to_its_own_taxable_value_and_gst(bool interState)
    {
        // A line of ₹1,049.99 at 12%: ₹937.49 taxable and ₹112.50 GST (₹56.25 each half), credited in uneven parts.
        const decimal net = 1049.99m;
        const decimal taxable = 937.49m;
        var (lineCgst, lineSgst) = Money.SplitGst(net - taxable);
        foreach (var parts in new[]
                 {
                     new[] { 0.01m, 0.01m, 1049.97m },
                     new[] { 333.33m, 333.33m, 383.33m },
                     new[] { 100m, 0.03m, 949.96m },
                     new[] { 524.99m, 525m },
                 })
        {
            var credited = 0m;
            var total = new GstShare(0m, 0m, 0m, 0m);
            foreach (var part in parts)
            {
                var share = GstCreditNotes.Share(net, taxable, credited, part, interState);
                (share.Taxable + share.Gst).ShouldBe(part, "Each credit is its taxable value and its GST.");
                if (!interState)
                {
                    Math.Abs(share.Cgst - share.Sgst).ShouldBeLessThanOrEqualTo(0.01m, "CGST and SGST are halves, to the paisa.");
                }

                total = new GstShare(total.Taxable + share.Taxable, total.Cgst + share.Cgst, total.Sgst + share.Sgst, total.Igst + share.Igst);
                credited += part;
            }

            credited.ShouldBe(net);
            total.Taxable.ShouldBe(taxable, $"Credited as {string.Join(" + ", parts)}");
            if (interState)
            {
                (total.Cgst, total.Sgst, total.Igst).ShouldBe((0m, 0m, net - taxable));
            }
            else
            {
                (total.Cgst, total.Sgst, total.Igst).ShouldBe((lineCgst, lineSgst, 0m));
            }
        }
    }
}
