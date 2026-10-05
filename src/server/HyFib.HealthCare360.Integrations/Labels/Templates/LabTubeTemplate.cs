namespace HyFib.HealthCare360.Integrations.Labels.Templates;

/// <summary>
/// The sample tube label, as on the staff app's printed label:
/// <code>
/// SHARMA RAVI KUMAR                46Y/M
/// UHID NH00012345           [STAT] or LA2600123
/// ||||||||||||| sample barcode |||||||||||||
/// LS2600000123                  EDTA - HAEM
/// CBC, ESR +2                  26 Sep 10:32
/// </code>
/// </summary>
internal static class LabTubeTemplate
{
    public static void Draw(LabelCanvas c, LabTubeLabel label)
    {
        var plan = Code128Plan.For(label.Barcode);
        var name = LabelText.Printable(label.PatientName, "The patient's name");
        var uhid = LabelText.Printable(label.Uhid, "The UHID");
        var ageSex = LabelText.Printable(label.AgeSex, "Age and sex");
        var accession = LabelText.Printable(label.AccessionNumber, "The accession number");
        var tube = LabelFormat.Join(" - ", LabelText.Printable(label.Tube, "The tube"), LabelText.Printable(label.Bench, "The bench"));
        var tests = (label.Tests ?? []).Select(test => LabelText.Printable(test, "A test name")).Where(test => test.Length > 0).ToList();
        var time = label.CollectedAt is { } collected ? LabelFormat.DayTime(collected) : string.Empty;

        var s = LabelFormat.Scale(c.Media);
        var m = c.Mm(1.5);
        var inner = c.Width - (2 * m);
        var big = c.Font(c.Mm(2.8 * s)).Height;
        var small = c.Font(c.Mm(2.1 * s)).Height;
        var gap = c.Mm(0.5 * s);
        var pad = Math.Max(2, c.Mm(0.4));

        // Name, with age and sex at the end of the line.
        var y = m;
        var ageWidth = ageSex.Length > 0 ? c.Measure(ageSex, small) + c.Mm(1) : 0;
        c.Text(m, y, big, c.Fit(name, big, inner - ageWidth));
        c.Text(m, y + big - small, small, ageSex, LabelAlign.End, inner);
        y += big + gap;

        // UHID, with STAT in white on black (or the accession number) at the end.
        var markWidth = 0;
        if (label.Stat)
        {
            var width = c.Measure("STAT", small);
            markWidth = width + (2 * pad);
            c.Text(m + inner - width - pad, y, small, "STAT");
            c.Reverse(m + inner - markWidth, y - pad, markWidth, small + (2 * pad));
        }
        else if (accession.Length > 0)
        {
            markWidth = c.Text(m, y, small, c.Fit(accession, small, inner / 2), LabelAlign.End, inner);
        }

        c.Text(m, y, small, c.Fit($"UHID {uhid}", small, inner - markWidth - c.Mm(1)));
        y += small + gap + (label.Stat ? pad : 0);

        // The barcode takes the height left after the two lines below it.
        var below = (2 * small) + (2 * gap);
        var barHeight = Math.Min(c.Height - m - below - gap - y, c.Mm(12));
        if (barHeight < c.Mm(4))
        {
            throw new LabelLayoutException($"A {LabelCanvas.Describe(c.Media)} label is too short for a sample label. Use taller labels.");
        }

        var module = c.ModuleWidth(plan.Modules, c.Width - 2, "The sample number");
        c.Barcode((c.Width - (plan.Modules * module)) / 2, y, plan, module, barHeight);
        y += barHeight + gap;

        // The barcode's characters, then the tube and bench.
        var tubeWidth = tube.Length > 0 ? c.Text(m, y, small, c.Fit(tube, small, inner / 2), LabelAlign.End, inner) + c.Mm(1) : 0;
        c.Text(m, y, small, c.Fit(plan.Value, small, inner - tubeWidth));
        y += small + gap;

        // The tests ("+2" when more share the tube), and when the sample was drawn.
        var timeWidth = time.Length > 0 ? c.Text(m, y, small, time, LabelAlign.End, inner) + c.Mm(1) : 0;
        c.Text(m, y, small, TestsText(c, tests, small, inner - timeWidth));
    }

    /// <summary>As many test names as fit, then "+n" for the rest, so the phlebotomist sees that more share the tube.</summary>
    internal static string TestsText(LabelCanvas c, IReadOnlyList<string> tests, int height, int width)
    {
        for (var shown = tests.Count; shown > 0; shown--)
        {
            var text = string.Join(", ", tests.Take(shown)) + (shown < tests.Count ? $" +{tests.Count - shown}" : string.Empty);
            if (c.Measure(text, height) <= width)
            {
                return text;
            }
        }

        if (tests.Count == 0)
        {
            return string.Empty;
        }

        var more = tests.Count > 1 ? $" +{tests.Count - 1}" : string.Empty;
        var first = c.Fit(tests[0], height, width - c.Measure(more, height));
        return first.Length > 0 ? first + more : c.Fit(more.Trim(), height, width);
    }
}
