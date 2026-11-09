namespace HyFib.HealthCare360.Integrations.Labels.Templates;

/// <summary>
/// The bio-medical waste bag label (BMW Rules 2016, Schedule IV as amended in 2018), as on the staff app's printed label,
/// on a label of at least 75 × 50 mm (the weight and the date are never cut short, so a smaller label is refused):
/// <code>
/// YELLOW  Biohazard / Cytotoxic              (the category white on black)
/// HCF TN-BMW-1234
/// ||||||||||| bag barcode |||||||||||
///              NHBW260000123
/// 2.45 kg - 08 Nov 2026
/// Ward 3 dressing room - Nana Hospitals
/// </code>
/// </summary>
internal static class WasteBagTemplate
{
    public static void Draw(LabelCanvas c, WasteBagLabelData bag)
    {
        var plan = Code128Plan.For(bag.Barcode);
        var category = LabelText.Printable(bag.Category, "The category").ToUpperInvariant();
        var symbol = LabelText.Printable(bag.Symbol, "The symbol");
        var hcf = LabelText.Printable($"HCF {bag.HcfId}", "The HCF identifier");
        var hospital = LabelText.Printable(bag.Hospital, "The hospital's name");
        var source = LabelText.Printable(bag.Source, "Where it was filled");
        var weighed = LabelFormat.Join(" - ", bag.WeightKg.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " kg", LabelFormat.Date(bag.CollectedOn));

        var s = LabelFormat.Scale(c.Media);
        var m = c.Mm(1.5);
        var inner = c.Width - (2 * m);
        var big = c.Font(c.Mm(2.6 * s)).Height;
        var small = c.Font(c.Mm(2.0 * s)).Height;
        var gap = c.Mm(0.5 * s);
        var pad = Math.Max(2, c.Mm(0.4));

        // The weight, the date and the HCF identifier are what the CBWTF checks the bag against: a label too narrow to print them whole is refused.
        if (c.Measure(weighed, big) > inner || c.Measure(hcf, small) > inner || c.Measure(category, big) + (2 * pad) > inner)
        {
            throw new LabelLayoutException($"A {LabelCanvas.Describe(c.Media)} label is too small for a waste bag label. Use labels of at least 75 × 50 mm.");
        }

        // The category in white on black, so the colour code is read before anything else; the symbol beside it.
        var y = m;
        var width = c.Measure(category, big);
        c.Text(m + pad, y + pad, big, category);
        c.Reverse(m, y, width + (2 * pad), big + (2 * pad));
        var after = m + width + (3 * pad);
        c.Text(after, y + pad, small, c.Fit(symbol, small, c.Width - m - after));
        y += big + (2 * pad) + gap;

        c.Text(m, y, small, hcf);
        y += small + gap;

        var below = (2 * big) + small + (3 * gap);
        var barHeight = Math.Min(c.Height - m - below - y, c.Mm(10));
        if (barHeight < c.Mm(4))
        {
            throw new LabelLayoutException($"A {LabelCanvas.Describe(c.Media)} label is too small for a waste bag label. Use labels of at least 75 × 50 mm.");
        }

        var module = c.ModuleWidth(plan.Modules, c.Width - 2, "The bag's bar code");
        c.Barcode((c.Width - (plan.Modules * module)) / 2, y, plan, module, barHeight);
        y += barHeight + gap;
        c.Text(m, y, big, c.Fit(plan.Value, big, inner), LabelAlign.Center, inner);
        y += big + gap;
        c.Text(m, y, big, weighed);
        y += big + gap;
        c.Text(m, y, small, c.Fit(LabelFormat.Join(" - ", source, hospital), small, inner));
    }
}
