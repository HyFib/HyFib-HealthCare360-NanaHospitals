namespace HyFib.HealthCare360.Integrations.Labels.Templates;

/// <summary>
/// The patient's wristband, printed along the band so the lines read along the wrist:
/// <code>
/// SHARMA RAVI KUMAR            |█ ALLERGY █|  ||||| UHID barcode |||||   (or a QR code)
/// UHID NH00012345  46Y/M       |███████████|
/// IP2600123  Ward 3 / Bed 12   |███████████|
/// </code>
/// The band's width runs across the print head, so the text and the barcode are turned a quarter turn. The black
/// "ALLERGY" mark is printed only when the patient has active allergies on record. When nobody has recorded allergies
/// or their absence, an outlined "ALLERGIES / NOT RECORDED" mark (black text in a frame) takes its place, so the band
/// never reads like one of a patient with no known allergies, who gets no mark.
/// </summary>
internal static class WristbandTemplate
{
    public static void Draw(LabelCanvas c, WristbandLabel band, WristbandCode code)
    {
        var uhid = LabelText.Printable(band.Uhid, "The UHID");
        var name = LabelText.Printable(band.PatientName, "The patient's name");
        var ageSex = LabelText.Printable(band.AgeSex, "Age and sex");
        var admission = LabelText.Printable(band.AdmissionNumber, "The admission number");
        var place = LabelFormat.Join(" / ", LabelText.Printable(band.Ward, "The ward"), LabelText.Printable(band.Bed, "The bed"));

        var s = Math.Clamp((double)c.Media.WidthMm / 25, 0.75, 1.3);
        var m = c.Mm(2);
        var across = c.Width - (2 * m);
        var lead = c.Mm(4);
        var gap = c.Mm(4);
        var big = c.Font(c.Mm(3.6 * s)).Height;
        var small = c.Font(c.Mm(2.6 * s)).Height;
        var lineGap = c.Mm(0.8 * s);

        // The code first, to know how much of the band is left for the text.
        Code128Plan? plan = null;
        int module = 0, barHeight = 0, qrModules = 0, magnification = 0, codeLength;
        if (code == WristbandCode.QrCode)
        {
            qrModules = QrSize.Modules(uhid);
            magnification = Math.Min(across / (qrModules + 4), Math.Max(1, c.Mm(0.6)));
            if (magnification * 25.4 / c.Media.Dpi < 0.33)
            {
                throw new LabelLayoutException("The band is too narrow for a QR code that scanners read. Use Code 128.");
            }

            codeLength = (qrModules + 4) * magnification;
        }
        else
        {
            plan = Code128Plan.For(uhid);
            module = c.ModuleWidth(plan.Modules, Math.Min(c.Mm(70), c.Height / 2), "The UHID");
            barHeight = across - c.Mm(2);
            codeLength = (plan.Modules + 20) * module;
        }

        // The mark's lines, top line first when the band is read; "NOT RECORDED" takes two lines across the band so
        // the mark stays short along it.
        string[] mark = band.Allergies switch
        {
            WristbandAllergies.Recorded => ["ALLERGY"],
            WristbandAllergies.NotRecorded => ["ALLERGIES", "NOT RECORDED"],
            _ => [],
        };
        var markHeight = c.Font(c.Mm(3.2 * s)).Height;
        var markPad = c.Mm(2);
        var markLength = mark.Length > 0 ? mark.Max(line => c.Measure(line, markHeight)) + (2 * markPad) : 0;
        var room = c.Height - lead - gap - codeLength - lead - (mark.Length > 0 ? markLength + gap : 0);
        var textLength = Math.Min(room, c.Mm(110));
        if (textLength < c.Mm(35))
        {
            throw new LabelLayoutException(
                $"A {LabelCanvas.Describe(c.Media)} band is too short for the wristband. Use longer bands, or the QR code.");
        }

        // Three lines down the band; the name is the top line when the band is read.
        var lines = new List<(int Height, string Text)>
        {
            (big, name),
            (small, LabelFormat.Join("  ", $"UHID {uhid}", ageSex)),
            (small, LabelFormat.Join("  ", admission, place)),
        };
        var x = c.Width - m;
        var used = 0;
        foreach (var (height, text) in lines.Where(line => line.Text.Length > 0))
        {
            x -= height;
            if (x < m)
            {
                break;
            }

            used = Math.Max(used, c.TextDown(x, lead, height, c.Fit(text, height, textLength)));
            x -= lineGap;
        }

        var y = lead + used + gap;
        if (band.Allergies == WristbandAllergies.Recorded)
        {
            c.TextDown(m + ((across - markHeight) / 2), y + markPad, markHeight, "ALLERGY");
            c.Reverse(m, y, across, markLength);
            y += markLength + gap;
        }
        else if (mark.Length > 0)
        {
            // Two lines centred across the band, inside a frame the width of the black mark.
            var block = (mark.Length * markHeight) + ((mark.Length - 1) * lineGap);
            var lineX = m + ((across + block) / 2);
            foreach (var line in mark)
            {
                lineX -= markHeight;
                c.TextDown(lineX, y + markPad, markHeight, line);
                lineX -= lineGap;
            }

            c.Box(m, y, across, markLength, Math.Max(2, c.Mm(0.5)));
            y += markLength + gap;
        }

        if (plan is not null)
        {
            c.Barcode(m + ((across - barHeight) / 2), y + (10 * module), plan, module, barHeight, LabelRotation.Clockwise);
        }
        else
        {
            var size = qrModules * magnification;
            c.Qr(m + ((across - size) / 2), y + (2 * magnification), uhid, qrModules, magnification);
        }
    }
}
