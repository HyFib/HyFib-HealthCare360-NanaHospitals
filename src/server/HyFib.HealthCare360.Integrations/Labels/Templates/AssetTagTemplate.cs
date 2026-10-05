namespace HyFib.HealthCare360.Integrations.Labels.Templates;

/// <summary>
/// The asset tag, as on the staff app's printed tag:
/// <code>
///             Nana Hospitals
///      Multipara monitor, ICU bed 4
/// ||||||||||| asset tag barcode |||||||||||
///              AST-2600123
/// </code>
/// </summary>
internal static class AssetTagTemplate
{
    public static void Draw(LabelCanvas c, AssetTagLabel tag)
    {
        var plan = Code128Plan.For(tag.AssetTag);
        var hospital = LabelText.Printable(tag.Hospital, "The hospital's name");
        var asset = LabelText.Printable(tag.AssetName, "The asset's name");

        var s = LabelFormat.Scale(c.Media);
        var m = c.Mm(1.5);
        var inner = c.Width - (2 * m);
        var big = c.Font(c.Mm(2.8 * s)).Height;
        var small = c.Font(c.Mm(2.1 * s)).Height;
        var tiny = c.Font(c.Mm(1.9 * s)).Height;
        var gap = c.Mm(0.5 * s);

        var y = m;
        if (hospital.Length > 0)
        {
            c.Text(m, y, tiny, c.Fit(hospital, tiny, inner), LabelAlign.Center, inner);
            y += tiny + gap;
        }

        c.Text(m, y, small, c.Fit(asset, small, inner), LabelAlign.Center, inner);
        y += small + gap;

        var barHeight = Math.Min(c.Height - m - big - gap - y, c.Mm(10));
        if (barHeight < c.Mm(4))
        {
            throw new LabelLayoutException($"A {LabelCanvas.Describe(c.Media)} label is too short for an asset tag. Use taller labels.");
        }

        var module = c.ModuleWidth(plan.Modules, c.Width - 2, "The asset tag");
        c.Barcode((c.Width - (plan.Modules * module)) / 2, y, plan, module, barHeight);
        y += barHeight + gap;
        c.Text(m, y, big, c.Fit(plan.Value, big, inner), LabelAlign.Center, inner);
    }
}
