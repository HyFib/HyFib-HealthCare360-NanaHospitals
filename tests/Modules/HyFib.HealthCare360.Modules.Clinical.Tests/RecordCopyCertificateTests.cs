using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// The certificate for a copy of the record (Bharatiya Sakshya Adhiniyam 2023, s.63(4) and its Schedule): Part A for the
/// person in charge of the computer and Part B for an expert, each with the server ticked as the source and SHA256 as the
/// algorithm, every file's hash stated, and the hash report the Schedule asks to be enclosed. What only the signatories
/// know is left to be written in.
/// </summary>
public sealed partial class RecordCopyCertificateTests
{
    // 30 Oct 2026, 14:05 IST.
    private static readonly DateTimeOffset IssuedAt = new(2026, 10, 30, 8, 35, 0, TimeSpan.Zero);

    private static readonly RecordCopyFile[] Files =
    [
        File(2, "Lab report LAB2600031", "lab-report-LAB2600031.pdf", "B1"),
        File(1, "Discharge summary", "discharge-summary.pdf", "A0"),
    ];

    [Fact]
    public void Both_parts_tick_the_server_and_sha256_and_state_every_files_hash_in_order()
    {
        var html = Certificate().ToHtml();

        html.ShouldContain("Certificate under section 63(4) of the Bharatiya Sakshya Adhiniyam, 2023");
        html.ShouldContain("Part A <span class=\"who\">(To be filled by the Party)</span>");
        html.ShouldContain("Part B <span class=\"who\">(To be filled by the Expert)</span>");
        html.ShouldContain("residing/employed at Nana Hospitals Pvt. Ltd., 12 MG Road, Pune");
        html.ShouldContain("was working properly and the relevant information was regularly fed into the computer during the ordinary course of business");

        // In each part, the server as the source and SHA256 as the algorithm, and nothing else ticked.
        Ticked().Matches(html).Count.ShouldBe(4);
        html.ShouldContain("<span class=\"box on\"></span>Server");
        html.ShouldContain("SHA256: <span class=\"box on\"></span>");
        html.ShouldContain("HC360 server ID 0b7c4f6e-node");
        html.ShouldContain("version 2026.10.1, on the");
        html.ShouldContain("server HC360-SRV01 (Microsoft Windows 10.0.20348)");

        // Every file's hash in each part and in the report, in the copy's order.
        var a = new string('A', 62) + "A0";
        var b = new string('B', 62) + "B1";
        Regex.Matches(html, a).Count.ShouldBe(3);
        Regex.Matches(html, b).Count.ShouldBe(3);
        html.IndexOf("discharge-summary.pdf", StringComparison.Ordinal).ShouldBeLessThan(html.IndexOf("lab-report-LAB2600031.pdf", StringComparison.Ordinal));
    }

    [Fact]
    public void The_hash_report_names_the_copy_and_its_files_and_the_signatories_fill_in_their_own_particulars()
    {
        var html = Certificate().ToHtml();

        html.ShouldContain("Hash report");
        html.ShouldContain("RR2600042");
        html.ShouldContain("Kavya Raman (UHID NH2600123)");
        html.ShouldContain("Judicial Magistrate, Saidapet");
        html.ShouldContain("30/10/2026 14:05 IST, by Priya (MRD)");
        html.ShouldContain("SHA-256 (FIPS 180-4), over each file&#x27;s bytes exactly as issued");
        html.ShouldContain("<td class=\"num\">1,048,576</td>");

        // Names, make, model and serial number, and the signing date, time and place are written in by hand.
        Regex.Matches(html, "<span class=\"blank\"></span>").Count.ShouldBeGreaterThanOrEqualTo(9);
        html.ShouldContain("(Name and signature)");
        html.ShouldContain("(Name, designation and signature)");
        Regex.Matches(html, "Time \\(IST\\): hh:mm \\(in 24 hours format\\)").Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("2026-10-30T08:35:00Z", "30/10/2026 14:05")]
    [InlineData("2026-12-31T19:00:00Z", "01/01/2027 00:30")]
    public void Dates_and_times_are_written_as_the_schedule_asks_in_ist(string instant, string expected) =>
        RecordCopyCertificate.Stamp(DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);

    private static PrintableDocument Certificate() => RecordCopyCertificate.Build(
        new RecordCopyCertificateModel("RR2600042", RecordRequestPurpose.Legal, "Judicial Magistrate, Saidapet", "Kavya Raman", "NH2600123", IssuedAt,
            "Priya (MRD)", "Nana Hospitals Pvt. Ltd., 12 MG Road, Pune", Files),
        new DocumentLetterhead("Nana Hospitals", "Nana Hospitals Pvt. Ltd.", "12 MG Road, Pune", null, null, null, null, null, null, null, null, null));

    private static RecordCopyFile File(int sequence, string title, string name, string end) => new()
    {
        Sequence = sequence,
        Title = title,
        FileName = name,
        DocumentDate = IssuedAt.AddDays(-3),
        SizeBytes = 1_048_576,
        Sha256 = new string(end[0], 62) + end,
        ProducedAt = IssuedAt,
        ServerId = "0b7c4f6e-node",
        MachineName = "HC360-SRV01",
        OsDescription = "Microsoft Windows 10.0.20348",
        SoftwareVersion = "2026.10.1",
    };

    [GeneratedRegex("class=\"box on\"")]
    private static partial Regex Ticked();
}
