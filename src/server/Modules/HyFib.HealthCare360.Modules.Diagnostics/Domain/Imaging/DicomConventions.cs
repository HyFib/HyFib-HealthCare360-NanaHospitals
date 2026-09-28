using System.Globalization;
using System.Numerics;

namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

/// <summary>
/// DICOM formatting for the modality worklist and PACS reconciliation (IMG-02; pure, unit-tested). UIDs use the
/// UUID-derived root <c>2.25</c> (ITU-T X.667). They are globally unique without a registered organisation root, which
/// hospitals rarely have.
/// </summary>
internal static class DicomConventions
{
    public const string UuidRoot = "2.25.";

    /// <summary>Maximum length of a DICOM UID.</summary>
    public const int MaxUidLength = 64;

    public static string NewUid() => UidFrom(Guid.NewGuid());

    public static string UidFrom(Guid guid) =>
        UuidRoot + new BigInteger(guid.ToByteArray(bigEndian: true), isUnsigned: true, isBigEndian: true).ToString(CultureInfo.InvariantCulture);

    public static bool IsValidUid(string? uid) =>
        uid is { Length: > 0 and <= MaxUidLength }
        && uid.All(c => char.IsAsciiDigit(c) || c == '.')
        && !uid.StartsWith('.') && !uid.EndsWith('.') && !uid.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// Person name (PN) as FAMILY^GIVEN^MIDDLE. Many Indian patients are registered without a surname; the whole
    /// registered name then goes in the family component, as DICOM recommends for mononyms, so the modality console
    /// shows it exactly as printed on the patient's wristband.
    /// </summary>
    public static string PersonName(string? firstName, string? middleName, string? lastName)
    {
        var first = Clean(firstName);
        var last = Clean(lastName);
        var middle = Clean(middleName);
        string[] components = last.Length == 0 ? [$"{first} {middle}".Trim()] : [last, first, middle];
        return string.Join('^', components).TrimEnd('^');
    }

    public static string Date(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public static string Time(TimeOnly time) => time.ToString("HHmmss", CultureInfo.InvariantCulture);

    public static string Sex(Gender gender) => gender switch
    {
        Gender.Male => "M",
        Gender.Female => "F",
        Gender.Other => "O",
        _ => string.Empty,
    };

    /// <summary>Modality code for the worklist (0008,0060): conventional radiography is sent as CR.</summary>
    public static string ModalityCode(Modality modality) => modality == Modality.XR ? "CR" : modality.ToString();

    /// <summary>Removes PN delimiters (^ = \) and control characters that would corrupt the DICOM value.</summary>
    private static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Trim().Where(c => c is not ('^' or '=' or '\\') && !char.IsControl(c)).ToArray());
}
