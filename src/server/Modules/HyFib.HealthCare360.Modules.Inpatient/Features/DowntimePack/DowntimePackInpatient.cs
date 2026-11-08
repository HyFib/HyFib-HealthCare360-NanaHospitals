using System.Globalization;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.DowntimePrint;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.DowntimePack;

/// <summary>
/// Inpatient's part of the pre-downtime pack: the census of each ward (every patient in a bed with the diagnosis,
/// allergies and isolation), and each in-patient's medication sheet: the active orders with the doses due in the next
/// 24 hours and a box to sign each one given on paper. Its rows are the downtime ward sheet's
/// (<see cref="DowntimePrintService"/>), asked for every ward of the facility and for the pack's 24 hours, so the ward
/// sheet and the pack never disagree.
/// </summary>
internal sealed class DowntimePackInpatient(DowntimePrintService sheets) : IDowntimePackSource
{
    public async Task<IReadOnlyList<DowntimePackSection>> BuildAsync(DowntimePackContext context, CancellationToken ct)
    {
        var hours = (int)Math.Round((context.WindowEnd - context.Now).TotalHours);

        // The pack audits its own print (patients.downtime_pack_printed); the sheet is not audited again.
        var sheet = await sheets.BuildAsync(new DowntimeSheetRequest(context.FacilityId, null, hours, context.CanSeeRestricted, null, Audit: false), ct);

        var census = sheet.Wards.Where(w => w.Patients.Count > 0).Select(w =>
        {
            var isolated = w.Patients.Count(p => p.Isolation is not null);
            return new DowntimePackTable(
                $"{w.Name} ({w.Code})",
                $"{w.Patients.Count} {(w.Patients.Count == 1 ? "patient" : "patients")}{(isolated > 0 ? $", {isolated} in isolation" : "")}",
                w.Patients.Any(p => p.AllergyStatus == AllergyRecordingStatus.NotRecorded) ? "Allergies not recorded for some patients: ask before giving any medicine." : null,
                ["Bed", "Patient (UHID)", "Age / sex", "Diagnosis", "Allergies", "Isolation", "Consultant", "Admission"],
                [.. w.Patients.Select(p => (IReadOnlyList<string>)[
                    p.BedLabel ?? "",
                    $"{p.PatientName} ({p.Uhid})",
                    $"{p.Age ?? ""} / {p.Gender.ToString()[..1]}",
                    p.ProvisionalDiagnosis ?? "",
                    Allergies(p),
                    p.Isolation ?? "",
                    p.ConsultantName ?? "",
                    $"{p.AdmissionNumber}, day {p.DayOfStay}{(p.IsMlc ? ", MLC" : "")}",
                ])]);
        }).ToList();

        var sheetsByPatient = sheet.Wards.SelectMany(w => w.Patients.Select(p => (Ward: w.Name, Patient: p))).Select(x =>
            new DowntimePackTable(
                $"{x.Ward}, bed {x.Patient.BedLabel}: {x.Patient.PatientName} ({x.Patient.Uhid})",
                $"Allergies: {Allergies(x.Patient)}{(x.Patient.Isolation is { } iso ? $". Isolation: {iso}" : "")}",
                x.Patient.AllergyStatus == AllergyRecordingStatus.NotRecorded ? "ALLERGIES NOT RECORDED" : null,
                ["Medicine", "Dose", "Route", "Frequency", "Due (next 24 h)", "Flags", "Given: time, by (sign)"],
                [.. x.Patient.Medications.Select(m => (IReadOnlyList<string>)[
                    m.DrugName,
                    m.Dose,
                    m.Route ?? "",
                    m.IsAsNeeded ? $"{m.Frequency} (as needed)" : m.Frequency,
                    m.DueTimes.Count == 0 ? (m.IsAsNeeded ? "When needed" : "As ordered") : string.Join(", ", m.DueTimes.Select(Short)),
                    Flags(m),
                    "",
                ])])).ToList();

        return
        [
            new DowntimePackSection("census", "Census by ward",
                $"Every patient in a bed at {Local(context.Now)}: diagnosis (provisional, from the admission), allergies and isolation.", false, census),
            new DowntimePackSection("mar", "Medication sheets",
                $"Each in-patient's active medication orders and the doses due {Local(context.Now)} to {Local(context.WindowEnd)}. Sign each dose given on the sheet; it is entered on the MAR, with the time given, when HC360 is back. A medicine ordered after this print is on paper only.",
                true, sheetsByPatient),
        ];
    }

    private static string Allergies(DowntimePatient p) => p.AllergyStatus switch
    {
        AllergyRecordingStatus.NotRecorded => "ALLERGIES NOT RECORDED",
        _ when p.Allergies.Count == 0 => "No known allergies",
        _ => string.Join("; ", p.Allergies.Select(a => $"{a.Allergen} ({a.Severity.ToString().ToLowerInvariant()}{(a.Reaction is null ? "" : ", " + a.Reaction)})")),
    };

    private static string Flags(DowntimeMedication m)
    {
        var flags = new List<string>();
        if (m.IsHighAlert)
        {
            flags.Add("high-alert");
        }

        if (m.IsControlledDrug)
        {
            flags.Add("controlled");
        }

        if (m.DosesHeld)
        {
            flags.Add("held: awaiting doctor");
        }

        return string.Join(", ", flags);
    }

    private static string Short(DateTimeOffset at) => Platform.Numbering.NumberFormat.ToHospitalTime(at).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);

    private static string Local(DateTimeOffset at) => Platform.Numbering.NumberFormat.ToHospitalTime(at).ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
}
