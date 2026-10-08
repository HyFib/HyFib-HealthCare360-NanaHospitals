using System.Globalization;

namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>
/// Reads the DICOM JSON model (PS3.18 §F) returned by QIDO-RS: objects keyed by 8-hex-digit tags, each with a
/// <c>vr</c> and a <c>Value</c> array. IS values arrive as numbers or strings depending on the PACS, and person names
/// as <c>{"Alphabetic":"DOE^JOHN"}</c>.
/// </summary>
internal static class DicomJson
{
    public const string StudyInstanceUid = "0020000D";
    public const string AccessionNumber = "00080050";
    public const string PatientId = "00100020";
    public const string PatientName = "00100010";
    public const string StudyDate = "00080020";
    public const string StudyTime = "00080030";
    public const string TimezoneOffset = "00080201";
    public const string ModalitiesInStudy = "00080061";
    public const string StudyDescription = "00081030";
    public const string NumberOfSeries = "00201206";
    public const string NumberOfInstances = "00201208";
    public const string SeriesInstanceUid = "0020000E";
    public const string Modality = "00080060";
    public const string SeriesNumber = "00200011";
    public const string SeriesDescription = "0008103E";
    public const string BodyPartExamined = "00180015";
    public const string NumberOfSeriesInstances = "00201209";
    public const string SopClassUid = "00080016";
    public const string SopInstanceUid = "00080018";
    public const string InstanceNumber = "00200013";
    public const string NumberOfFrames = "00280008";
    public const string Rows = "00280010";
    public const string Columns = "00280011";

    /// <summary>Series of objects that are not pictures: structured reports, key objects, presentation states, raw data.</summary>
    private static readonly HashSet<string> NonImageModalities = new(StringComparer.OrdinalIgnoreCase) { "SR", "KO", "PR", "REG", "RWV", "FID", "DOC", "PLAN" };

    /// <summary>
    /// SOP classes that are not pictures (structured reports and key objects, presentation states, waveforms, raw data,
    /// encapsulated documents), by UID prefix.
    /// </summary>
    private static readonly string[] NonImageSopClasses =
    [
        "1.2.840.10008.5.1.4.1.1.88.", "1.2.840.10008.5.1.4.1.1.11.", "1.2.840.10008.5.1.4.1.1.9.", "1.2.840.10008.5.1.4.1.1.66",
        "1.2.840.10008.5.1.4.1.1.104.",
    ];

    public static IReadOnlyList<ImagingStudyInfo> ParseStudies(string json, TimeZoneInfo zone) =>
        Parse(json, item => Text(item, StudyInstanceUid) is { Length: > 0 } uid
            ? new ImagingStudyInfo(
                uid,
                Text(item, AccessionNumber),
                Text(item, PatientId),
                PersonName(item, PatientName),
                StudyDateTime(item, zone),
                Joined(item, ModalitiesInStudy),
                Text(item, StudyDescription),
                Integer(item, NumberOfSeries),
                Integer(item, NumberOfInstances))
            : null);

    /// <summary>
    /// Series that hold pictures, by series number. Series of reports, key objects and presentation states are left out:
    /// they have no image to show.
    /// </summary>
    public static IReadOnlyList<ImagingSeriesInfo> ParseSeries(string json) =>
        Parse(json, item => Text(item, SeriesInstanceUid) is { Length: > 0 } uid && !NonImageModalities.Contains(Text(item, Modality) ?? string.Empty)
                ? new ImagingSeriesInfo(uid, Text(item, Modality), Integer(item, SeriesNumber), Text(item, SeriesDescription), Text(item, BodyPartExamined),
                    Integer(item, NumberOfSeriesInstances))
                : null)
            .OrderBy(s => s.SeriesNumber ?? int.MaxValue)
            .ThenBy(s => s.SeriesInstanceUid, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The pictures of a series. Objects that are not pictures (a report or presentation state filed in an image
    /// series, or an object without rows) are left out; the number of frames is at least 1.
    /// </summary>
    public static IReadOnlyList<ImagingInstanceInfo> ParseInstances(string json) =>
        Parse(json, item => Text(item, SopInstanceUid) is { Length: > 0 } uid && Integer(item, Rows) is not 0 && IsImageClass(Text(item, SopClassUid))
            ? new ImagingInstanceInfo(uid, Text(item, SopClassUid), Integer(item, InstanceNumber), Math.Max(1, Integer(item, NumberOfFrames) ?? 1),
                Integer(item, Rows), Integer(item, Columns))
            : null);

    private static bool IsImageClass(string? sopClassUid) =>
        sopClassUid is null || !NonImageSopClasses.Any(prefix => sopClassUid.StartsWith(prefix, StringComparison.Ordinal));

    private static List<T> Parse<T>(string json, Func<JsonElement, T?> read)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json) || !IntegrationHttp.TryParseJson(json, out var document))
        {
            return [];
        }

        using (document)
        {
            return document.RootElement.ValueKind != JsonValueKind.Array
                ? []
                : document.RootElement.EnumerateArray().Select(read).OfType<T>().ToList();
        }
    }

    internal static string? Text(JsonElement item, string tag) => Values(item, tag).Select(IntegrationHttp.Text).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    internal static string? Joined(JsonElement item, string tag)
    {
        var values = Values(item, tag).Select(IntegrationHttp.Text).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        return values.Count == 0 ? null : string.Join(",", values);
    }

    internal static int? Integer(JsonElement item, string tag)
    {
        var text = Text(item, tag);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>"DOE^JOHN^^^" → "DOE JOHN" (component order kept; empty components dropped).</summary>
    internal static string? PersonName(JsonElement item, string tag)
    {
        var first = Values(item, tag).FirstOrDefault();
        var alphabetic = first.ValueKind switch
        {
            JsonValueKind.Object => IntegrationHttp.Property(first, "Alphabetic"),
            JsonValueKind.String => first.GetString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(alphabetic))
        {
            return null;
        }

        return string.Join(' ', alphabetic.Split('=')[0].Split('^', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>StudyDate (DA) + StudyTime (TM) in the PACS's local zone, or its explicit offset (00080201, "+0530").</summary>
    internal static DateTimeOffset? StudyDateTime(JsonElement item, TimeZoneInfo zone)
    {
        var date = Text(item, StudyDate);
        if (date is null || !DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return null;
        }

        var time = Text(item, StudyTime)?.Split('.')[0].Replace(":", string.Empty, StringComparison.Ordinal) ?? string.Empty;
        var local = day;
        if (time.Length >= 2 && int.TryParse(time.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && hours < 24)
        {
            local = local.AddHours(hours);
            if (time.Length >= 4 && int.TryParse(time.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes < 60)
            {
                local = local.AddMinutes(minutes);
            }

            if (time.Length >= 6 && int.TryParse(time.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds < 60)
            {
                local = local.AddSeconds(seconds);
            }
        }

        var offset = ParseOffset(Text(item, TimezoneOffset)) ?? zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    private static TimeSpan? ParseOffset(string? value)
    {
        if (value is not { Length: 5 } || value[0] is not ('+' or '-')
            || !int.TryParse(value.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(value.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || hours > 14 || minutes > 59)
        {
            return null;
        }

        var span = new TimeSpan(hours, minutes, 0);
        return value[0] == '-' ? -span : span;
    }

    /// <summary>The items of a sequence attribute (SQ): its values that are objects.</summary>
    internal static IEnumerable<JsonElement> Items(JsonElement item, string tag) => Values(item, tag).Where(v => v.ValueKind == JsonValueKind.Object);

    private static IEnumerable<JsonElement> Values(JsonElement item, string tag)
    {
        if (item.ValueKind == JsonValueKind.Object
            && (item.TryGetProperty(tag, out var attribute) || item.TryGetProperty(tag.ToLowerInvariant(), out attribute))
            && attribute.ValueKind == JsonValueKind.Object
            && attribute.TryGetProperty("Value", out var values)
            && values.ValueKind == JsonValueKind.Array)
        {
            return values.EnumerateArray();
        }

        return [];
    }
}
