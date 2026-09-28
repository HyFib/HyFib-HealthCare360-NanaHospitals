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

    public static IReadOnlyList<ImagingStudyInfo> ParseStudies(string json, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(json) || !IntegrationHttp.TryParseJson(json, out var document))
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var studies = new List<ImagingStudyInfo>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var uid = Text(item, StudyInstanceUid);
                if (string.IsNullOrWhiteSpace(uid))
                {
                    continue;
                }

                studies.Add(new ImagingStudyInfo(
                    uid,
                    Text(item, AccessionNumber),
                    Text(item, PatientId),
                    PersonName(item, PatientName),
                    StudyDateTime(item, zone),
                    Joined(item, ModalitiesInStudy),
                    Text(item, StudyDescription),
                    Integer(item, NumberOfSeries),
                    Integer(item, NumberOfInstances)));
            }

            return studies;
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
