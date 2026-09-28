using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace HyFib.HealthCare360.Integrations.Tally;

/// <summary>
/// Reads Tally's import response. Counters (<c>CREATED</c>, <c>ALTERED</c>, <c>ERRORS</c>) appear under
/// <c>RESPONSE</c> in older releases and under <c>BODY/DATA/IMPORTRESULT</c> in Tally Prime, so they are summed wherever
/// they occur; <c>LINEERROR</c> texts become the message. Tally emits character references XML 1.0 forbids (for
/// example <c>&amp;#4;</c>), so character checking is relaxed; DTDs are prohibited.
/// </summary>
internal static class TallyResponseParser
{
    public static AccountingExportResult Parse(string response)
    {
        if (!TryLoad(response, out var document))
        {
            return new AccountingExportResult(false, 0, 0, 1, "Tally returned a response that is not XML: " + IntegrationTrace.Truncate(response.Trim(), 200));
        }

        var created = Sum(document, "CREATED");
        var altered = Sum(document, "ALTERED");
        var errors = Sum(document, "ERRORS");
        var lineErrors = document.Descendants("LINEERROR")
            .Select(e => e.Value.Trim())
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (document.Root?.Element("HEADER")?.Element("STATUS")?.Value.Trim() == "0" && lineErrors.Count == 0)
        {
            lineErrors.Add(document.Root.Element("BODY")?.Value.Trim() is { Length: > 0 } detail ? detail : "Tally reported a failure.");
        }

        var errorCount = Math.Max(errors, lineErrors.Count);
        if (errorCount == 0)
        {
            return new AccountingExportResult(true, created, altered, 0,
                string.Create(CultureInfo.InvariantCulture, $"Tally created {created} and altered {altered} voucher(s)."));
        }

        var message = lineErrors.Count > 0 ? string.Join(" | ", lineErrors.Take(5)) : $"Tally reported {errorCount} error(s).";
        return new AccountingExportResult(false, created, altered, errorCount, IntegrationTrace.Truncate(message, 1000));
    }

    public static bool TryLoad(string xml, out XDocument document)
    {
        document = new XDocument();
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            {
                CheckCharacters = false,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            document = XDocument.Load(reader);
            return document.Root is not null;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static int Sum(XDocument document, string name) =>
        document.Descendants(name).Sum(e => int.TryParse(e.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0);
}
