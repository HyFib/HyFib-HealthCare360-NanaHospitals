using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>QIDO-RS study search URLs and viewer launch URLs.</summary>
internal static class QidoQuery
{
    /// <summary>Optional attributes requested explicitly because many PACS omit them by default.</summary>
    private static readonly string[] IncludeFields = ["00081030", "00201206", "00201208", "00080061", "00080201"];

    /// <summary>
    /// <c>studies?AccessionNumber=…&amp;PatientID=…&amp;StudyDate=yyyyMMdd-yyyyMMdd&amp;ModalitiesInStudy=…</c>.
    /// DICOM wildcards (* and ?) are stripped: reconciliation queries are exact, and a stray "*" would turn one lookup
    /// into a scan of the whole archive.
    /// </summary>
    public static string Build(PacsStudyQuery query, int limit)
    {
        var parameters = new List<(string Name, string Value)>();
        Add(parameters, "AccessionNumber", query.AccessionNumber);
        Add(parameters, "PatientID", query.PatientId);
        if (DateRange(query.From, query.To) is { } range)
        {
            parameters.Add(("StudyDate", range));
        }

        Add(parameters, "ModalitiesInStudy", query.Modality?.ToUpperInvariant());
        parameters.AddRange(IncludeFields.Select(field => ("includefield", field)));
        parameters.Add(("limit", Math.Clamp(limit, 1, 500).ToString(CultureInfo.InvariantCulture)));

        var builder = new StringBuilder("studies?");
        builder.AppendJoin('&', parameters.Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value)));
        return builder.ToString();
    }

    /// <summary>DICOM date range matching: <c>from-to</c>, <c>from-</c> or <c>-to</c>.</summary>
    public static string? DateRange(DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null)
        {
            return null;
        }

        return $"{Format(from)}-{Format(to)}";

        static string Format(DateOnly? date) => date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>Fills the viewer template; values are URL-encoded. Null when no template is configured.</summary>
    public static string? ViewerUrl(string? template, string studyInstanceUid, string? accessionNumber)
    {
        if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(studyInstanceUid))
        {
            return null;
        }

        var url = template.Trim()
            .Replace("{studyUid}", Uri.EscapeDataString(studyInstanceUid.Trim()), StringComparison.OrdinalIgnoreCase)
            .Replace("{accession}", Uri.EscapeDataString(accessionNumber?.Trim() ?? string.Empty), StringComparison.OrdinalIgnoreCase);
        return IntegrationHttp.TryCreateEndpoint(url, requireHttps: false, out var uri) ? uri.AbsoluteUri : null;
    }

    private static void Add(List<(string Name, string Value)> parameters, string name, string? value)
    {
        var cleaned = new string((value ?? string.Empty).Where(c => c is not '*' and not '?' && !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > 0)
        {
            parameters.Add((name, IntegrationTrace.Truncate(cleaned, 64)!));
        }
    }
}
