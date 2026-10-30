using System.Globalization;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Documents;

/// <summary>What the certificate of a copy says of it: the copy, whose record, the hospital, and the files with their hashes.</summary>
internal sealed record RecordCopyCertificateModel(
    string RequestNumber,
    RecordRequestPurpose Purpose,
    string RequestedBy,
    string PatientName,
    string Uhid,
    DateTimeOffset IssuedAt,
    string IssuedBy,
    string Hospital,
    IReadOnlyList<RecordCopyFile> Files);

/// <summary>
/// The certificate for a copy of the record issued as documents, in the form the Schedule to the Bharatiya Sakshya
/// Adhiniyam 2023 gives under s.63(4): Part A for the person in charge of the computer, Part B for an expert, and the hash
/// report it asks to be enclosed. HC360 fills in what it knows of the copy (the server ticked as the source, its
/// particulars, SHA-256 ticked and each file's value) and leaves the rest for the signatories to write in at signing.
/// </summary>
internal static class RecordCopyCertificate
{
    // A line for the signatory to write on.
    private static readonly Html Blank = Html.Of($"<span class=\"blank\"></span>");

    private static readonly Html Space = Html.Of($" ");

    private static readonly string[] Sources = ["Computer / Storage Media", "DVR", "Mobile", "Flash Drive", "CD/DVD", "Server", "Cloud", "Other"];

    private static readonly string[] Algorithms = ["SHA1", "SHA256", "MD5"];

    public static PrintableDocument Build(RecordCopyCertificateModel copy, DocumentLetterhead letterhead)
    {
        var files = copy.Files.OrderBy(f => f.Sequence).ToList();
        var server = files[0];
        var body = Html.Of($"""
            {DocumentParts.Letterhead(letterhead)}
            {DocumentParts.Title("Certificate under section 63(4) of the Bharatiya Sakshya Adhiniyam, 2023")}
            <p class="lead">For the electronic record: copy {copy.RequestNumber} of the medical record of {copy.PatientName} (UHID {copy.Uhid}),
            {Files(files.Count)}, issued on {Stamp(copy.IssuedAt)} IST.</p>
            <h2>Part A <span class="who">(To be filled by the Party)</span></h2>
            <p>I, {Blank} (Name), Son/daughter/spouse of {Blank} residing/employed at {copy.Hospital}
            do hereby solemnly affirm and sincerely state and submit as follows:&mdash;</p>
            <p>I have produced electronic record/output of the digital record taken from the following device/digital record source (tick mark):&mdash;</p>
            {Device(server)}
            <p>The digital device or the digital record source was under the lawful control for regularly creating, storing or processing
            information for the purposes of carrying out regular activities and during this period, the computer or the communication device
            was working properly and the relevant information was regularly fed into the computer during the ordinary course of business. If
            the computer/digital device at any point of time was not working properly or out of operation, then it has not affected the
            electronic/digital record or its accuracy. The digital device or the source of the digital record is:&mdash;</p>
            <p class="ticks">{Box(false)} Owned {Box(false)} Maintained {Box(false)} Managed {Box(false)} Operated by me (select as applicable).</p>
            {Hashes(files)}
            {Signature("Name and signature")}
            <h2 class="part-b">Part B <span class="who">(To be filled by the Expert)</span></h2>
            <p>I, {Blank} (Name), Son/daughter/spouse of {Blank} residing/employed at {Blank}
            do hereby solemnly affirm and sincerely state and submit as follows:&mdash;</p>
            <p>The produced electronic record/output of the digital record are obtained from the following device/digital record source (tick mark):&mdash;</p>
            {Device(server)}
            {Hashes(files)}
            {Signature("Name, designation and signature")}
            {Report(copy, files)}
            """);
        return new PrintableDocument
        {
            Title = $"BSA s.63 certificate, record copy {copy.RequestNumber}",
            FileName = $"bsa-63-certificate-{copy.RequestNumber}",
            Body = body,
            Styles = DocumentParts.Styles + Styles,
            Footer = $"{copy.RequestNumber} · Certificate under BSA 2023 s.63(4) · Medical record: confidential",
            Template = letterhead.Template,
        };
    }

    private const string Styles = """
        .lead { margin: 0 0 6px; }
        h2 { font-size: 11pt; margin: 10px 0 4px; border-bottom: 1px solid #999; }
        h2 .who { font-weight: 400; font-size: 9.5pt; }
        .part-b { break-before: page; }
        .blank { display: inline-block; min-width: 45mm; border-bottom: 1px solid #000; height: 1em; vertical-align: bottom; }
        .box { display: inline-block; width: 3.2mm; height: 3.2mm; border: 1px solid #000; vertical-align: -0.5mm; position: relative; margin: 0 1mm 0 3mm; }
        .box.on::after { content: ""; position: absolute; left: 0.9mm; top: 0; width: 1mm; height: 2mm; border: solid #000; border-width: 0 0.5mm 0.5mm 0; transform: rotate(45deg); }
        .ticks { margin: 4px 0; }
        .device td { padding: 1px 6px 1px 0; vertical-align: top; }
        .device td:first-child { color: #555; white-space: nowrap; }
        .hash { font-family: Consolas, "Courier New", monospace; font-size: 8.5pt; word-break: break-all; }
        .hashes { margin: 4px 0 4px 6mm; padding: 0; }
        .hashes li { margin-bottom: 2px; }
        .signature { margin-top: 10mm; display: grid; grid-template-columns: 1fr 1fr; gap: 3mm 12mm; break-inside: avoid; }
        .signature .line { border-top: 1px solid #000; padding-top: 2px; }
        .report { break-before: page; }
        table.files { width: 100%; border-collapse: collapse; font-size: 8.5pt; }
        table.files th, table.files td { border: 1px solid #999; padding: 2px 4px; text-align: left; vertical-align: top; }
        table.files td.num { text-align: right; white-space: nowrap; }
        .note { font-size: 8.5pt; color: #333; }
        """;

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    /// <summary>"30/10/2026 14:05": the Schedule's date (DD/MM/YYYY) and its time, in IST, in 24-hour form.</summary>
    internal static string Stamp(DateTimeOffset instant) =>
        DocumentFormat.ToIst(instant).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static Html Box(bool ticked) => ticked ? Html.Of($"<span class=\"box on\"></span>") : Html.Of($"<span class=\"box\"></span>");

    // The device block of either part: the server ticked as the source, and what HC360 knows of it. Its make, model,
    // colour and serial number are the hardware's, which HC360 cannot read reliably: the signatory writes them in.
    private static Html Device(RecordCopyFile server) => Html.Of($"""
        <p class="ticks">{Html.Join(Sources.Select(s => Html.Of($"{Box(s == "Server")}{s}")), Space)} {Blank}</p>
        <table class="device">
          <tr><td>Make &amp; Model:</td><td>{Blank} Color: {Blank}</td></tr>
          <tr><td>Serial Number:</td><td>{Blank}</td></tr>
          <tr><td>IMEI/UIN/UID/MAC/Cloud ID:</td><td>HC360 server ID {server.ServerId} (as applicable)</td></tr>
          <tr><td>Other relevant information:</td><td>HyFib HealthCare360 (HC360) hospital information system, version {server.SoftwareVersion}, on the
            server {server.MachineName} ({server.OsDescription}). The record is kept in the hospital's HC360 database; each file was made from it
            by HC360 when the copy was issued, and is kept on the server as it was handed over.</td></tr>
        </table>
        """);

    private static Html Hashes(IReadOnlyList<RecordCopyFile> files) => Html.Of($"""
        <p>I state that the HASH value/s of the electronic/digital record/s is/are:</p>
        <ol class="hashes">{Html.Join(files.Select(f => Html.Of($"<li>{f.FileName}: <span class=\"hash\">{f.Sha256}</span></li>")))}</ol>
        <p>obtained through the following algorithm:&mdash;</p>
        <p class="ticks">{Html.Join(Algorithms.Select(a => Html.Of($"{a}: {Box(a == "SHA256")}")), Space)} Other {Blank} (Legally acceptable standard)</p>
        <p>(Hash report enclosed with the certificate.)</p>
        """);

    private static Html Signature(string signer) => Html.Of($"""
        <div class="signature">
          <div class="line">({signer})</div>
          <div class="line">Date (DD/MM/YYYY):</div>
          <div class="line">Time (IST): hh:mm (in 24 hours format)</div>
          <div class="line">Place:</div>
        </div>
        """);

    private static Html Report(RecordCopyCertificateModel copy, IReadOnlyList<RecordCopyFile> files)
    {
        var server = files[0];
        var rows = files.Select(f => Html.Of($"""
            <tr><td class="num">{f.Sequence}</td><td>{f.Title}<br>{DocumentFormat.Date(f.DocumentDate)}</td><td>{f.FileName}</td>
            <td class="num">{f.SizeBytes.ToString("N0", CultureInfo.InvariantCulture)}</td><td class="hash">{f.Sha256}</td></tr>
            """));
        return Html.Of($"""
            <div class="report">
            {DocumentParts.Title("Hash report")}
            <p>Enclosed with the certificate under section 63(4) of the Bharatiya Sakshya Adhiniyam, 2023, for copy {copy.RequestNumber}.</p>
            {DocumentParts.Fields([
                ("Copy", copy.RequestNumber),
                ("Patient", $"{copy.PatientName} (UHID {copy.Uhid})"),
                ("Requested by", copy.RequestedBy),
                ("Purpose", copy.Purpose.ToString()),
                ("Issued", $"{Stamp(copy.IssuedAt)} IST, by {copy.IssuedBy}"),
                ("Made by", $"HC360 {server.SoftwareVersion} on {server.MachineName}, server ID {server.ServerId}"),
                ("Algorithm", "SHA-256 (FIPS 180-4), over each file's bytes exactly as issued"),
            ])}
            <table class="files">
              <thead><tr><th>#</th><th>Document</th><th>File</th><th>Size (bytes)</th><th>SHA-256</th></tr></thead>
              <tbody>{Html.Join(rows)}</tbody>
            </table>
            <p class="note">Each file was made by HC360 from the hospital's record when the copy was issued, and is kept on the server unchanged.
            A file whose SHA-256 differs from the value above is not the file issued. To check one: on Windows,
            <span class="hash">Get-FileHash -Algorithm SHA256 &lt;file&gt;</span> (PowerShell) or <span class="hash">certutil -hashfile &lt;file&gt; SHA256</span>;
            or ask the hospital's medical records department, which finds the copy by the value.</p>
            </div>
            """);
    }
}
