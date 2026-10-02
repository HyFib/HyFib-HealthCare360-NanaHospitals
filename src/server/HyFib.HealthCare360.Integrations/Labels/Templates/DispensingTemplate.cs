namespace HyFib.HealthCare360.Integrations.Labels.Templates;

/// <summary>
/// The medicine label the pharmacy sticks on each strip or bottle, as on the staff app's printed label:
/// <code>
/// SHARMA RAVI KUMAR
/// Paracetamol 500 mg x 10 tab
/// 1 tab - twice a day - 5 days -
/// after food
/// B AB1234                     Exp Sep 2027
/// Nana Hospitals                02 Oct 2026
/// </code>
/// The medicine and the directions take up to two lines each. On a short label the hospital's name gives way first,
/// then the second line of the directions, then the second line of the medicine. The expiry is never shortened.
/// </summary>
internal static class DispensingTemplate
{
    public static void Draw(LabelCanvas c, DispensingLabel label)
    {
        var patient = LabelText.Printable(label.PatientName, "The patient's name");
        var medicine = LabelFormat.Join(" x ", LabelText.Printable(label.Medicine, "The medicine"), LabelText.Printable(label.Quantity, "The quantity"));
        var directions = LabelText.Printable(label.Directions, "The directions");
        var batch = LabelText.Printable(label.Batch, "The batch");
        var expiry = label.Expiry is { } date ? $"Exp {LabelFormat.MonthYear(date)}" : string.Empty;
        var dispensed = label.DispensedOn is { } on ? LabelFormat.Date(on) : string.Empty;
        var hospital = LabelText.Printable(label.Hospital, "The hospital's name");

        var s = LabelFormat.Scale(c.Media);
        var m = c.Mm(1.5);
        var inner = c.Width - (2 * m);
        var big = c.Font(c.Mm(2.8 * s)).Height;
        var small = c.Font(c.Mm(2.2 * s)).Height;
        var tiny = c.Font(c.Mm(1.9 * s)).Height;
        var gap = c.Mm(0.4 * s);

        var medicineLines = c.Wrap(medicine, big, inner, 2);
        var directionLines = directions.Length > 0 ? c.Wrap(directions, small, inner, 2) : [];
        var lastLine = hospital.Length > 0 || dispensed.Length > 0;
        int Needed() =>
            small + gap
            + (medicineLines.Count * (big + gap))
            + (directionLines.Count * (small + gap))
            + small + gap
            + (lastLine ? tiny : 0);

        var room = c.Height - (2 * m);
        if (Needed() > room && hospital.Length > 0)
        {
            hospital = string.Empty;
            lastLine = dispensed.Length > 0;
        }

        if (Needed() > room && directionLines.Count > 1)
        {
            directionLines = c.Wrap(directions, small, inner, 1);
        }

        if (Needed() > room && medicineLines.Count > 1)
        {
            medicineLines = c.Wrap(medicine, big, inner, 1);
        }

        if (Needed() > room)
        {
            throw new LabelLayoutException($"A {LabelCanvas.Describe(c.Media)} label is too short for a medicine label. Use taller labels.");
        }

        var y = m;
        c.Text(m, y, small, c.Fit(patient, small, inner));
        y += small + gap;
        foreach (var line in medicineLines)
        {
            c.Text(m, y, big, line);
            y += big + gap;
        }

        foreach (var line in directionLines)
        {
            c.Text(m, y, small, line);
            y += small + gap;
        }

        if (expiry.Length > 0 && c.Measure(expiry, small) > inner)
        {
            throw new LabelLayoutException($"A {LabelCanvas.Describe(c.Media)} label is too narrow for the expiry date. Use wider labels.");
        }

        var expiryWidth = expiry.Length > 0 ? c.Text(m, y, small, expiry, LabelAlign.End, inner) + c.Mm(1) : 0;
        c.Text(m, y, small, c.Fit(batch.Length > 0 ? $"B {batch}" : string.Empty, small, inner - expiryWidth));
        y += small + gap;

        if (lastLine)
        {
            var dateWidth = dispensed.Length > 0 ? c.Text(m, y, tiny, dispensed, LabelAlign.End, inner) + c.Mm(1) : 0;
            c.Text(m, y, tiny, c.Fit(hospital, tiny, inner - dateWidth));
        }
    }
}
