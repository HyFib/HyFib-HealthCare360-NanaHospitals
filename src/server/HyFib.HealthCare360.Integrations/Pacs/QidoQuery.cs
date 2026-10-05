using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>QIDO-RS search and WADO-RS image URLs (relative to the DICOMweb root), and viewer launch URLs.</summary>
internal static class QidoQuery
{
    /// <summary>Most objects a series listing reads (four pages of <see cref="InstancePageSize"/>).</summary>
    public const int MaxInstances = 2000;

    public const int InstancePageSize = 500;

    /// <summary>Optional attributes requested explicitly because many PACS omit them by default.</summary>
    private static readonly string[] IncludeFields = ["00081030", "00201206", "00201208", "00080061", "00080201"];

    /// <summary>Series description, body part and the number of objects (not returned by default).</summary>
    private static readonly string[] SeriesFields = ["0008103E", "00180015", "00201209"];

    /// <summary>Number of frames, rows, columns and instance number.</summary>
    private static readonly string[] InstanceFields = ["00280008", "00280010", "00280011", "00200013"];

    /// <summary>
    /// <c>studies?AccessionNumber=…&amp;PatientID=…&amp;StudyDate=yyyyMMdd-yyyyMMdd&amp;ModalitiesInStudy=…</c>.
    /// DICOM wildcards (* and ?) are stripped: reconciliation queries are exact, and a stray "*" would turn one lookup
    /// into a scan of the whole archive.
    /// </summary>
    public static string Build(PacsStudyQuery query, int limit)
    {
        var parameters = new List<(string Name, string Value)>();
        if (IsValidUid(query.StudyInstanceUid))
        {
            parameters.Add(("StudyInstanceUID", query.StudyInstanceUid!));
        }

        Add(parameters, "AccessionNumber", query.AccessionNumber);
        Add(parameters, "PatientID", query.PatientId);
        if (DateRange(query.From, query.To) is { } range)
        {
            parameters.Add(("StudyDate", range));
        }

        Add(parameters, "ModalitiesInStudy", query.Modality?.ToUpperInvariant());
        parameters.AddRange(IncludeFields.Select(field => ("includefield", field)));
        parameters.Add(("limit", Math.Clamp(limit, 1, 500).ToString(CultureInfo.InvariantCulture)));
        return "studies?" + Join(parameters);
    }

    /// <summary><c>studies/{study}/series?includefield=…</c>.</summary>
    public static string Series(string studyInstanceUid) =>
        $"studies/{Uid(studyInstanceUid)}/series?" + Join([.. SeriesFields.Select(field => ("includefield", field)), ("limit", "500")]);

    /// <summary><c>studies/{study}/series/{series}/instances?includefield=…&amp;limit=…&amp;offset=…</c>.</summary>
    public static string Instances(string studyInstanceUid, string seriesInstanceUid, int offset) =>
        $"studies/{Uid(studyInstanceUid)}/series/{Uid(seriesInstanceUid)}/instances?" + Join(
        [
            .. InstanceFields.Select(field => ("includefield", field)),
            ("limit", InstancePageSize.ToString(CultureInfo.InvariantCulture)),
            ("offset", Math.Max(0, offset).ToString(CultureInfo.InvariantCulture)),
        ]);

    /// <summary>
    /// WADO-RS image resource: <c>…/instances/{i}/rendered</c>, <c>…/instances/{i}/frames/{f}/rendered</c> when a frame
    /// of a multi-frame object is asked for, or the series' <c>…/thumbnail</c>; <c>viewport</c> asks the PACS to fit the
    /// image in a square of that size.
    /// </summary>
    public static string Image(PacsImageRequest request)
    {
        var series = $"studies/{Uid(request.StudyInstanceUid)}/series/{Uid(request.SeriesInstanceUid)}";
        var size = Math.Clamp(request.Size, 16, 4096).ToString(CultureInfo.InvariantCulture);
        var viewport = $"?viewport={size},{size}";
        if (request.SopInstanceUid is null)
        {
            return series + "/thumbnail" + viewport;
        }

        var instance = $"{series}/instances/{Uid(request.SopInstanceUid)}";
        var resource = request.Kind == PacsImageKind.Thumbnail ? "thumbnail" : "rendered";
        return request.Frame is { } frame
            ? $"{instance}/frames/{Math.Max(1, frame).ToString(CultureInfo.InvariantCulture)}/{resource}{viewport}"
            : $"{instance}/{resource}{viewport}";
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

    /// <summary>
    /// Fills the viewer template; values are URL-encoded. Null when no template is configured, when it names no study
    /// (neither <c>{studyUid}</c> nor <c>{accession}</c>), or when it needs a value that is not given.
    /// </summary>
    public static string? ViewerUrl(string? template, string? studyInstanceUid, string? accessionNumber)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var text = template.Trim();
        var usesUid = text.Contains("{studyUid}", StringComparison.OrdinalIgnoreCase);
        var usesAccession = text.Contains("{accession}", StringComparison.OrdinalIgnoreCase);
        if ((!usesUid && !usesAccession)
            || (usesUid && string.IsNullOrWhiteSpace(studyInstanceUid))
            || (usesAccession && string.IsNullOrWhiteSpace(accessionNumber)))
        {
            return null;
        }

        var url = text
            .Replace("{studyUid}", Uri.EscapeDataString(studyInstanceUid?.Trim() ?? string.Empty), StringComparison.OrdinalIgnoreCase)
            .Replace("{accession}", Uri.EscapeDataString(accessionNumber?.Trim() ?? string.Empty), StringComparison.OrdinalIgnoreCase);
        return IntegrationHttp.TryCreateEndpoint(url, requireHttps: false, out var uri) ? uri.AbsoluteUri : null;
    }

    /// <summary>A DICOM UID: 1–64 characters, digits in dot-separated components, no empty component.</summary>
    public static bool IsValidUid(string? uid) =>
        uid is { Length: > 0 and <= 64 }
        && uid.All(c => char.IsAsciiDigit(c) || c == '.')
        && !uid.StartsWith('.') && !uid.EndsWith('.') && !uid.Contains("..", StringComparison.Ordinal);

    /// <summary>A UID for a URL path: refused unless valid, so a path can never be bent by what the caller passed.</summary>
    private static string Uid(string uid) =>
        IsValidUid(uid) ? uid : throw new IntegrationException("HC360 refused a DICOM UID that is not valid.");

    private static string Join(IEnumerable<(string Name, string Value)> parameters) =>
        new StringBuilder().AppendJoin('&', parameters.Select(p => Uri.EscapeDataString(p.Name) + "=" + Uri.EscapeDataString(p.Value))).ToString();

    private static void Add(List<(string Name, string Value)> parameters, string name, string? value)
    {
        var cleaned = new string((value ?? string.Empty).Where(c => c is not '*' and not '?' && !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > 0)
        {
            parameters.Add((name, IntegrationTrace.Truncate(cleaned, 64)!));
        }
    }
}
