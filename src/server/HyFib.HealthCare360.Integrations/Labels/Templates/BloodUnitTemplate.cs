namespace HyFib.HealthCare360.Integrations.Labels.Templates;

/// <summary>
/// The blood unit label, as on the staff app's printed label:
/// <code>
/// AB NEG                         TTI        (or, in white on black: QUARANTINE
/// PRBC 350 ml           NON-REACTIVE                                 NOT FOR ISSUE)
/// ||||||||||||| unit number barcode |||||||||||||
/// NH-BU-2600123
/// Exp 05 Nov 2026 14:00
/// Store at 2-6 deg C
/// </code>
/// The group is written POS or NEG in full, so a small minus sign is never missed.
/// </summary>
internal static class BloodUnitTemplate
{
    public static void Draw(LabelCanvas c, BloodUnitLabel unit)
    {
        var plan = Code128Plan.For(unit.UnitNumber);
        var group = GroupText(LabelText.Printable(unit.BloodGroup, "The blood group"));
        var component = LabelText.Printable(unit.Component, "The component");
        var volume = unit.VolumeMl is { } ml ? $"{ml} ml" : string.Empty;
        var expiry = unit.ExpiresAt is { } at ? $"Exp {LabelFormat.DateTime(at)}" : string.Empty;
        var storage = LabelText.Printable(unit.Storage, "The storage condition");

        var s = LabelFormat.Scale(c.Media);
        var m = c.Mm(1.5);
        var inner = c.Width - (2 * m);
        var groupHeight = c.Font(c.Mm(5 * s)).Height;
        var small = c.Font(c.Mm(2.1 * s)).Height;
        var tiny = c.Font(c.Mm(1.8 * s)).Height;
        var gap = c.Mm(0.5 * s);
        var pad = Math.Max(2, c.Mm(0.5));

        // Top: the group with the component and volume under it, and the screening status at the end.
        var y = m;
        var (first, second) = unit.Cleared ? ("TTI", "NON-REACTIVE") : ("QUARANTINE", "NOT FOR ISSUE");
        var statusWidth = Math.Max(c.Measure(first, tiny), c.Measure(second, tiny)) + (2 * pad);
        var statusHeight = (2 * tiny) + gap + (2 * pad);
        var statusX = m + inner - statusWidth;
        c.Text(statusX, y + pad, tiny, first, LabelAlign.Center, statusWidth);
        c.Text(statusX, y + pad + tiny + gap, tiny, second, LabelAlign.Center, statusWidth);
        if (!unit.Cleared)
        {
            c.Reverse(statusX, y, statusWidth, statusHeight);
        }

        // The group is never shortened: a long one ("Bombay (hh) POS") is printed smaller instead.
        var left = statusX - c.Mm(1) - m;
        while (c.Measure(group, groupHeight) > left && groupHeight > small)
        {
            groupHeight = c.Font(Math.Max(small, groupHeight * 4 / 5)).Height;
        }

        if (c.Measure(group, groupHeight) > left)
        {
            throw new LabelLayoutException($"A {LabelCanvas.Describe(c.Media)} label is too narrow for the blood group. Use wider labels.");
        }

        c.Text(m, y, groupHeight, group);
        c.Text(m, y + groupHeight + gap, small, c.Fit(LabelFormat.Join(" ", component, volume), small, left));
        y += Math.Max(groupHeight + gap + small, statusHeight) + gap;

        // The unit number barcode takes the height left after the lines below it: the number, the expiry (on a line of
        // its own, so it is never shortened) and the storage condition.
        var below = (2 * (small + gap)) + (storage.Length > 0 ? tiny + gap : 0);
        var barHeight = Math.Min(c.Height - m - below - y, c.Mm(10));
        if (barHeight < c.Mm(4))
        {
            throw new LabelLayoutException($"A {LabelCanvas.Describe(c.Media)} label is too short for a blood unit label. Use taller labels.");
        }

        var module = c.ModuleWidth(plan.Modules, c.Width - 2, "The unit number");
        c.Barcode((c.Width - (plan.Modules * module)) / 2, y, plan, module, barHeight);
        y += barHeight + gap;

        c.Text(m, y, small, c.Fit(plan.Value, small, inner));
        y += small + gap;
        if (expiry.Length > 0 && c.Measure(expiry, small) > inner)
        {
            throw new LabelLayoutException($"A {LabelCanvas.Describe(c.Media)} label is too narrow for the expiry date. Use wider labels.");
        }

        c.Text(m, y, small, expiry);
        y += small + gap;
        c.Text(m, y, tiny, c.Fit(storage, tiny, inner));
    }

    /// <summary>"O+" becomes "O POS" and "AB−" (already "AB-") becomes "AB NEG"; anything else is printed as it is.</summary>
    internal static string GroupText(string group) => group switch
    {
        _ when group.EndsWith('+') => group[..^1].TrimEnd() + " POS",
        _ when group.EndsWith('-') => group[..^1].TrimEnd() + " NEG",
        _ => group,
    };
}
