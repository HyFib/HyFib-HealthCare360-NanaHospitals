namespace HyFib.HealthCare360.Integrations.Labels.Templates;

/// <summary>
/// The sterile pack label, as on the staff app's printed label:
/// <code>
/// General surgery set
/// ||||||||||| pack number barcode |||||||||||
///               CS2600000123
/// Steam - Use by 30 Oct 2026     (or, in white on black: NOT RELEASED)
/// </code>
/// </summary>
internal static class CssdPackTemplate
{
    public static void Draw(LabelCanvas c, CssdPackLabel pack)
    {
        var plan = Code128Plan.For(pack.PackNumber);
        var set = LabelText.Printable(pack.SetName, "The set's name");
        var method = LabelText.Printable(pack.Method, "The method");

        var s = LabelFormat.Scale(c.Media);
        var m = c.Mm(1.5);
        var inner = c.Width - (2 * m);
        var big = c.Font(c.Mm(2.6 * s)).Height;
        var small = c.Font(c.Mm(2.1 * s)).Height;
        var gap = c.Mm(0.5 * s);
        var pad = Math.Max(2, c.Mm(0.4));

        var y = m;
        c.Text(m, y, big, c.Fit(set.Length > 0 ? set : "Instrument set", big, inner));
        y += big + gap;

        var below = (2 * (small + gap)) + (2 * pad);
        var barHeight = Math.Min(c.Height - m - below - y, c.Mm(10));
        if (barHeight < c.Mm(4))
        {
            throw new LabelLayoutException($"A {LabelCanvas.Describe(c.Media)} label is too short for a pack label. Use taller labels.");
        }

        var module = c.ModuleWidth(plan.Modules, c.Width - 2, "The pack number");
        c.Barcode((c.Width - (plan.Modules * module)) / 2, y, plan, module, barHeight);
        y += barHeight + gap;

        c.Text(m, y, small, c.Fit(plan.Value, small, inner), LabelAlign.Center, inner);
        y += small + gap + pad;

        if (pack.SterileUntil is { } until)
        {
            c.Text(m, y, small, c.Fit(LabelFormat.Join(" - ", method, $"Use by {LabelFormat.Date(until)}"), small, inner));
        }
        else
        {
            // Not sterile yet: white on black, so nobody takes it for a released pack.
            const string NotReleased = "NOT RELEASED";
            var width = c.Measure(NotReleased, small);
            c.Text(m + pad, y, small, NotReleased);
            c.Reverse(m, y - pad, width + (2 * pad), small + (2 * pad));
        }
    }
}
