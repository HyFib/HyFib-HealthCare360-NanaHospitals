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

    /// <summary>
    /// The studies a DICOM modality code covers. Projection radiography is one kind of work whatever the detector, so CR
    /// and DX both cover HC360's X-ray (XR) and DX studies: an X-ray order reaches the CR reader and the DR room alike,
    /// and scheduling a station keeps it on one of them. XR is accepted as another name for CR.
    /// </summary>
    public static IReadOnlyList<Modality> ModalitiesFor(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "CR" or "DX" or "XR" => [Modality.XR, Modality.DX],
        "CT" => [Modality.CT],
        "MR" => [Modality.MR],
        "US" => [Modality.US],
        "MG" => [Modality.MG],
        "RF" => [Modality.RF],
        _ => [],
    };

    /// <summary>The longest DICOM short string (SH): accession numbers and procedure ids.</summary>
    public const int MaxShortStringLength = ModalityWorklistItem.MaxShortStringLength;

    /// <summary>
    /// True when DICOM's Accession Number (SH, 16 characters) can hold the accession number. With a 6-letter facility code,
    /// or once a year's running number passes 9,999,999, <c>{FAC}RD{YY}{SEQ:7}</c> is 17 characters or more.
    /// </summary>
    public static bool AccessionFitsDicom(string? accessionNumber) => accessionNumber is { Length: > 0 and <= MaxShortStringLength };

    /// <summary>
    /// Requested Procedure ID and Scheduled Procedure Step ID (SH, 16 characters) on the worklist: the accession number
    /// when DICOM can carry it. Otherwise HC360 never shortens the accession number; it sends <see cref="StudyCode"/>
    /// instead, an id of its own that fits.
    /// </summary>
    public static string ProcedureId(string accessionNumber, Guid studyId) =>
        AccessionFitsDicom(accessionNumber) ? accessionNumber : StudyCode(studyId);

    /// <summary>
    /// 16 characters (Crockford base 32: digits and capital letters without I, L, O and U) made from the last 80 bits of
    /// the study's id, which hold its random part: the same for the study every time, and different for every study.
    /// </summary>
    public static string StudyCode(Guid studyId)
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var bytes = studyId.ToByteArray(bigEndian: true);
        var value = new BigInteger(bytes.AsSpan(6), isUnsigned: true, isBigEndian: true);
        var code = new char[MaxShortStringLength];
        for (var i = code.Length - 1; i >= 0; i--)
        {
            code[i] = Alphabet[(int)(value % 32)];
            value /= 32;
        }

        return new string(code);
    }

    /// <summary>Removes PN delimiters (^ = \) and control characters that would corrupt the DICOM value.</summary>
    private static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Trim().Where(c => c is not ('^' or '=' or '\\') && !char.IsControl(c)).ToArray());
}
