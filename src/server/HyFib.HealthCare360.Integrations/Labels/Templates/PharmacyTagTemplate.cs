using System.Globalization;

namespace HyFib.HealthCare360.Integrations.Labels.Templates;

/// <summary>
/// The pharmacy tag (INT-BAR), as on the staff app's printed tag:
/// <code>
///          Paracetamol 500 mg tab
///        B AB1234   Exp Sep 2027
///            MRP Rs 2.50/tab
/// ||||||||||| item and batch barcode |||||||||||
///              PCM500#AB1234
/// </code>
/// On a short label the MRP line gives way first, then the batch and expiry line; the barcode and the medicine never do.
/// </summary>
internal static class PharmacyTagTemplate
{
    public static void Draw(LabelCanvas c, PharmacyTagLabel tag)
    {
        var plan = Code128Plan.For(tag.Barcode);
        var medicine = LabelText.Printable(tag.Medicine, "The medicine");
        var details = new List<string>(2);
        var batchAndExpiry = LabelFormat.Join("   ",
            tag.Batch is { Length: > 0 } batch ? $"B {LabelText.Printable(batch, "The batch")}" : null,
            tag.Expiry is { } expiry ? $"Exp {LabelFormat.MonthYear(expiry)}" : null);
        if (batchAndExpiry.Length > 0)
        {
            details.Add(batchAndExpiry);
        }

        if (tag.Mrp is { } mrp)
        {
            var unit = tag.Unit is { Length: > 0 } per ? $"/{LabelText.Printable(per, "The unit")}" : string.Empty;
            details.Add(string.Create(CultureInfo.InvariantCulture, $"MRP Rs {mrp:0.00}{unit}"));
        }

        var s = LabelFormat.Scale(c.Media);
        var m = c.Mm(1.5);
        var inner = c.Width - (2 * m);
        var big = c.Font(c.Mm(2.8 * s)).Height;
        var small = c.Font(c.Mm(2.1 * s)).Height;
        var gap = c.Mm(0.5 * s);

        int BarRoom() => c.Height - (2 * m) - small - gap - big - gap - (details.Count * (small + gap));
        while (details.Count > 0 && BarRoom() < c.Mm(4))
        {
            details.RemoveAt(details.Count - 1);
        }

        var barHeight = Math.Min(BarRoom(), c.Mm(10));
        if (barHeight < c.Mm(4))
        {
            throw new LabelLayoutException($"A {LabelCanvas.Describe(c.Media)} label is too short for a pharmacy tag. Use taller labels.");
        }

        var y = m;
        foreach (var line in (string[])[medicine, .. details])
        {
            c.Text(m, y, small, c.Fit(line, small, inner), LabelAlign.Center, inner);
            y += small + gap;
        }

        var module = c.ModuleWidth(plan.Modules, c.Width - 2, "The pharmacy tag");
        c.Barcode((c.Width - (plan.Modules * module)) / 2, y, plan, module, barHeight);
        y += barHeight + gap;
        c.Text(m, y, big, c.Fit(plan.Value, big, inner), LabelAlign.Center, inner);
    }
}
