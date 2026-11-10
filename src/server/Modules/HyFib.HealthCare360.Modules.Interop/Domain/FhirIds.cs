namespace HyFib.HealthCare360.Modules.Interop.Domain;

/// <summary>
/// Logical ids of the facade's resources. A resource type fed by one table uses the row's GUID. A type fed by several
/// sources prefixes it with the source (<c>visit-</c>, <c>enc-</c>, <c>lab-</c>, <c>rad-</c>, <c>card-</c>, <c>doc-</c>,
/// <c>ds-</c>, <c>note-</c>, <c>rpm-</c>; a Location's <c>ward-</c>, <c>room-</c>, <c>bed-</c>), and a vital-sign
/// observation adds the measurement (<c>vs-{guid}-bp</c>). Every id is therefore stable and resolves to exactly one row,
/// and a read never has to probe several tables.
/// </summary>
internal static class FhirIds
{
    public const string Visit = "visit-";
    public const string ClinicalEncounter = "enc-";
    public const string Vitals = "vs-";
    public const string Lab = "lab-";
    public const string Radiology = "rad-";
    public const string Cardiology = "card-";
    public const string PatientDocument = "doc-";
    public const string DischargeSummary = "ds-";
    public const string ClinicalNote = "note-";
    public const string DeviceReading = "rpm-";
    public const string Ward = "ward-";
    public const string Room = "room-";
    public const string Bed = "bed-";

    private const int GuidLength = 36;

    /// <summary>Canonical lower-case GUID ("D" format).</summary>
    public static string Of(Guid id) => id.ToString("D");

    public static string Of(string prefix, Guid id) => prefix + Of(id);

    public static bool TryParse(string? id, out Guid value) => Guid.TryParseExact(id, "D", out value);

    public static bool TryParse(string? id, string prefix, out Guid value)
    {
        value = Guid.Empty;
        return id is not null && id.StartsWith(prefix, StringComparison.Ordinal) && TryParse(id[prefix.Length..], out value);
    }

    /// <summary>Splits <c>{prefix}{guid}-{suffix}</c> (vital-sign observations).</summary>
    public static bool TryParse(string? id, string prefix, out Guid value, out string suffix)
    {
        value = Guid.Empty;
        suffix = string.Empty;
        if (id is null || !id.StartsWith(prefix, StringComparison.Ordinal) || id.Length < prefix.Length + GuidLength + 2
            || id[prefix.Length + GuidLength] != '-')
        {
            return false;
        }

        suffix = id[(prefix.Length + GuidLength + 1)..];
        return TryParse(id.Substring(prefix.Length, GuidLength), out value);
    }
}
