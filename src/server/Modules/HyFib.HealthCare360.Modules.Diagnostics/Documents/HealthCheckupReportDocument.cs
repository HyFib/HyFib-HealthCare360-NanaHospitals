using HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Platform.Documents;
using BookingStatus = HyFib.HealthCare360.Modules.Clinical.Domain.BookingStatus;
using ItineraryStatus = HyFib.HealthCare360.Modules.Clinical.Domain.ItineraryStatus;
using OrderItemStatus = HyFib.HealthCare360.Modules.Clinical.Domain.OrderItemStatus;

namespace HyFib.HealthCare360.Modules.Diagnostics.Documents;

/// <summary>An investigation of the check-up whose result is not released yet. It is listed by name, never with a value.</summary>
internal sealed record PendingInvestigation(string Name, string Department, string Status)
{
    /// <summary>Nothing done yet (sample not taken, study not started).</summary>
    public const string NotStarted = "Pending";

    /// <summary>Started, or done and waiting for the report or its release.</summary>
    public const string Started = "In progress";
}

/// <summary>The diagnostics results of a check-up booking: its released reports, and the investigations still pending.</summary>
internal sealed record CheckupResults(
    IReadOnlyList<LabReportView> Lab,
    IReadOnlyList<RadiologyReportView> Radiology,
    IReadOnlyList<CardiologyReportView> Cardiology,
    IReadOnlyList<PendingInvestigation> Pending)
{
    public static CheckupResults None { get; } = new([], [], [], []);
}

/// <summary>
/// The full health check-up report (MHC-04), one A4 document: the check-up summary as the staff app printed it
/// (package, stations and their notes, investigations, the reviewing doctor's summary and recommendations), then every
/// released lab, radiology and cardiology result of the booking, laid out as in its own report, then the
/// investigations still pending, the signatories and the end line. Only released results are printed, whoever asks:
/// a test or report that is not released is listed as pending, never with a value.
/// </summary>
internal static class HealthCheckupReportDocument
{
    private const string Styles = """
        .report .body h2.part { font-size: 12pt; color: #045E80; border-bottom: 2px solid #045E80; margin: 16px 0 4px; }
        .result-meta { margin-top: 6px; }

        """;

    private const string AutoValidatedNote =
        "* Verified automatically under the laboratory auto-validation rules (in range, QC accepted, delta check passed).";

    public static Task<PrintableDocument> BuildAsync(CheckupReport checkup, CheckupResults results, ReportDocumentFrame frame, CancellationToken cancellationToken)
    {
        var booking = checkup.Booking;
        var name = $"Health check-up report {booking.BookingNumber}";
        var released = Released(results);
        return frame.BuildAsync(
            new ReportFrame(
                "Health check-up report",
                name,
                $"health-checkup-report-{booking.BookingNumber}",
                ReportDocumentFrame.Footer(Patient(checkup), name),
                Letterhead(checkup.Letterhead),
                PatientRows(checkup),
                Summary(checkup, released) + Results(released),
                Signatories(checkup, released),
                null,
                null,
                booking.Status == BookingStatus.Completed ? null : $"{StatusLabel(booking.Status)} — not final",
                Notes(released),
                LabReportDocument.Styles + RadiologyReportDocument.Styles + CardiologyReportDocument.Styles + Styles),
            cancellationToken);
    }

    /// <summary>Identity, the booking and its package, and when the check-up was done.</summary>
    public static IReadOnlyList<(string Label, string? Value)> PatientRows(CheckupReport checkup)
    {
        var booking = checkup.Booking;
        var patient = checkup.Patient;
        var rows = new List<(string Label, string? Value)>
        {
            ("Patient", patient.FullName),
            ("Age / sex", $"{patient.Age ?? DocumentFormat.Missing} / {GenderShort(patient.Gender)}"),
            ("UHID", patient.Uhid),
            ("Booking no.", booking.BookingNumber),
            ("Package", $"{booking.PackageName} ({booking.PackageCode})"),
            ("Check-up date", DocumentFormat.Date(booking.ScheduledDate)),
        };
        if (booking.CorporateName is { Length: > 0 } company)
        {
            rows.Add(("Company", company));
        }

        rows.Add(("Completed", booking.CompletedAt is { } completed ? DocumentFormat.DateTime(completed) : "Not completed"));
        return rows;
    }

    /// <summary>
    /// The check-up summary: the doctor's summary and recommendations, the investigations of the package with their
    /// status, and the stations of the day with their notes.
    /// </summary>
    public static Html Summary(CheckupReport checkup, CheckupResults results)
    {
        var booking = checkup.Booking;
        var tests = (checkup.Order?.Items ?? [])
            .Where(i => i.Status != OrderItemStatus.Cancelled)
            .Select(i => Html.Of($"<tr><td>{i.ServiceName}</td><td>{TestStatus(i.Status)}</td></tr>"))
            .ToList();
        var stops = booking.Itinerary
            .OrderBy(s => s.Sequence)
            .Select(s => Html.Of($"<tr><td>{s.DepartmentName}</td><td>{s.Description}</td><td>{StopStatus(s.Status)}</td><td>{s.Notes}</td></tr>"))
            .ToList();
        var anyResults = results.Lab.Count + results.Radiology.Count + results.Cardiology.Count > 0;

        var summary = Html.Paragraphs(booking.Summary);
        var recommendations = Html.Paragraphs(booking.Recommendations);
        var investigations = tests.Count == 0
            ? Html.Empty
            : Html.Of($"""
                <h2>Investigations</h2>
                <table><thead><tr><th>Test</th><th style="width:25%">Status</th></tr></thead><tbody>{tests}</tbody></table>
                <div class="small">{(anyResults ? "The released results follow this summary." : "No results have been released yet.")}</div>
                """);
        var stations = stops.Count == 0
            ? Html.Empty
            : Html.Of($"""
                <h2>Check-up stations</h2>
                <table><thead><tr><th style="width:20%">Department</th><th>Station</th><th style="width:12%">Status</th><th style="width:28%">Notes</th></tr></thead><tbody>{stops}</tbody></table>
                """);
        return Html.Of($"""
            <h2>Doctor's summary</h2>
            {(summary.IsEmpty ? Html.Of($"<p class=\"muted\">Not yet written.</p>") : summary)}
            {(recommendations.IsEmpty ? Html.Empty : Html.Of($"<h2>Recommendations</h2>{recommendations}"))}
            {investigations}
            {stations}
            """);
    }

    /// <summary>
    /// Every released result, laid out as in its own report under the report's number and release time, then the
    /// investigations still pending (names only).
    /// </summary>
    public static Html Results(CheckupResults results)
    {
        var parts = new List<Html>();
        if (results.Lab.Count > 0)
        {
            parts.Add(Part("Laboratory results", results.Lab.Select(v => Result(v.ReportNumber, v.ReleasedAt, LabReportDocument.Body(v, releasedOnly: true)))));
        }

        if (results.Radiology.Count > 0)
        {
            parts.Add(Part("Radiology reports", results.Radiology.Select(v => Result(v.AccessionNumber, v.ReleasedAt, RadiologyReportDocument.Body(v)))));
        }

        if (results.Cardiology.Count > 0)
        {
            parts.Add(Part("Cardiology reports", results.Cardiology.Select(v => Result(v.AccessionNumber, v.ReleasedAt, CardiologyReportDocument.Body(v)))));
        }

        if (results.Pending.Count > 0)
        {
            parts.Add(Html.Of($"""
                <h2 class="part">Results pending</h2>
                <table><thead><tr><th>Investigation</th><th style="width:20%">Department</th><th style="width:15%">Status</th></tr></thead><tbody>
                {results.Pending.Select(p => Html.Of($"<tr><td>{p.Name}</td><td>{p.Department}</td><td>{p.Status}</td></tr>"))}
                </tbody></table>
                """));
        }

        return Html.Join(parts);
    }

    /// <summary>
    /// The results with released content only. A lab test, radiology or cardiology report that is not released moves to
    /// the pending list, so a value that is not released never reaches this document, whatever it is given.
    /// </summary>
    public static CheckupResults Released(CheckupResults results)
    {
        var pending = new List<PendingInvestigation>(results.Pending);
        var lab = new List<LabReportView>();
        foreach (var view in results.Lab)
        {
            var sections = new List<LabReportSection>();
            foreach (var section in view.Sections)
            {
                pending.AddRange(section.Tests
                    .Where(t => t.Status is not (LabOrderItemStatus.Released or LabOrderItemStatus.Cancelled))
                    .Select(t => new PendingInvestigation(
                        t.Name, "Laboratory", t.Status == LabOrderItemStatus.Ordered ? PendingInvestigation.NotStarted : PendingInvestigation.Started)));
                var tests = section.Tests.Where(t => t.Status == LabOrderItemStatus.Released).ToList();
                if (tests.Count > 0)
                {
                    sections.Add(section with { Tests = tests });
                }
            }

            if (sections.Count > 0)
            {
                lab.Add(view with { Sections = sections });
            }
        }

        pending.AddRange(results.Radiology
            .Where(v => v.Status != DiagnosticReportStatus.Released)
            .Select(v => new PendingInvestigation(v.StudyName, "Radiology", PendingInvestigation.Started)));
        pending.AddRange(results.Cardiology
            .Where(v => v.Status != DiagnosticReportStatus.Released)
            .Select(v => new PendingInvestigation(v.StudyName, "Cardiology", PendingInvestigation.Started)));
        return new CheckupResults(
            lab,
            [.. results.Radiology.Where(v => v.Status == DiagnosticReportStatus.Released)],
            [.. results.Cardiology.Where(v => v.Status == DiagnosticReportStatus.Released)],
            pending);
    }

    /// <summary>
    /// Whoever signed the results printed here (pathologists, radiologists, cardiologists), each once, then the doctor
    /// who reviewed the check-up, or a space for the reviewing doctor to sign.
    /// </summary>
    public static IReadOnlyList<ReportSignatory> Signatories(CheckupReport checkup, CheckupResults results)
    {
        var doctor = checkup.ReviewingDoctor;
        var signatories = new List<ReportSignatory>();
        var signed = results.Lab.SelectMany(v => v.Signatories)
            .Concat(results.Radiology.SelectMany(v => v.Signatories))
            .Concat(results.Cardiology.SelectMany(v => v.Signatories));
        foreach (var signatory in signed)
        {
            var isReviewer = doctor is not null && signatory.Name == doctor.DisplayName && signatory.RegistrationNumber == doctor.RegistrationNumber;
            if (!isReviewer && signatories.TrueForAll(s => s.UserId != signatory.UserId))
            {
                signatories.Add(signatory);
            }
        }

        signatories.Add(doctor is null
            ? new ReportSignatory(Guid.Empty, "Reviewing doctor", null, null, null)
            : new ReportSignatory(doctor.PractitionerId, doctor.DisplayName, doctor.Qualification, Registration(doctor), doctor.SignatureFileId));
        return signatories;
    }

    /// <summary>What the printed results need explained: auto-verified values, amended lab reports, imaging opinions, device recordings.</summary>
    public static IReadOnlyList<string> Notes(CheckupResults results)
    {
        var notes = new List<string>();
        var lines = results.Lab.SelectMany(v => v.Sections).SelectMany(s => s.Tests).SelectMany(t => t.Results);
        if (lines.Any(l => l.AutoValidated && l.Status == LabResultStatus.Verified))
        {
            notes.Add(AutoValidatedNote);
        }

        if (results.Lab.Any(v => v.Status == LabReportStatus.Amended))
        {
            notes.Add("A laboratory report was amended after release. The amendments and their reasons are listed with its results.");
        }

        if (results.Radiology.Count > 0)
        {
            notes.Add("Radiology reports are a professional opinion and should be correlated clinically.");
        }

        if (results.Cardiology.Any(v => v.Files.Count > 0))
        {
            notes.Add("Device recordings of the cardiology tests are stored with their reports.");
        }

        return notes;
    }

    public static string StatusLabel(BookingStatus status) => status switch
    {
        BookingStatus.Booked => "Booked",
        BookingStatus.CheckedIn => "Checked in",
        BookingStatus.InProgress => "In progress",
        BookingStatus.Completed => "Completed",
        BookingStatus.Cancelled => "Cancelled",
        BookingStatus.NoShow => "Did not come",
        _ => status.ToString(),
    };

    private static Html Part(string title, IEnumerable<Html> results) => Html.Of($"<h2 class=\"part\">{title}</h2>{results}");

    // One report's results under its number and release time.
    private static Html Result(string number, DateTimeOffset? releasedAt, Html body) =>
        Html.Of($"<section class=\"result\"><div class=\"small result-meta keep-next\">Report {number} · Released {DocumentFormat.DateTime(releasedAt)}</div>{body}</section>");

    private static string TestStatus(OrderItemStatus status) => status switch
    {
        OrderItemStatus.Completed => "Reported",
        OrderItemStatus.InProgress => "In progress",
        _ => "Pending",
    };

    private static string StopStatus(ItineraryStatus status) => status switch
    {
        ItineraryStatus.Done => "Done",
        ItineraryStatus.Skipped => "Not done",
        _ => "Pending",
    };

    // As the check-up report has always printed it: an unknown sex is a dash.
    private static string GenderShort(Gender gender) => gender switch
    {
        Gender.Male => "M",
        Gender.Female => "F",
        Gender.Other => "O",
        _ => DocumentFormat.Missing,
    };

    // "12345 (TCMC)": the council goes with the number, as on prescriptions.
    private static string? Registration(PrescriberInfo doctor) =>
        doctor.RegistrationNumber is not { Length: > 0 } number ? null
        : doctor.RegistrationCouncil is { Length: > 0 } council ? $"{number} ({council})"
        : number;

    private static ReportPatient Patient(CheckupReport checkup) =>
        new(checkup.Patient.Id, checkup.Patient.Uhid, checkup.Patient.FullName, checkup.Patient.Gender, checkup.Patient.Age, checkup.Patient.DateOfBirth);

    private static ReportLetterhead Letterhead(FacilityLetterhead? letterhead) =>
        letterhead is null
            ? new ReportLetterhead("Hospital", null, null, null, null, null, null)
            : new ReportLetterhead(
                letterhead.Name, Blank(letterhead.Address), Blank(letterhead.Phone), Blank(letterhead.Email), Blank(letterhead.NabhNumber),
                Blank(letterhead.LetterheadNote), letterhead.LogoFileId, letterhead.FacilityId);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
