using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>
/// Whether a DSC is the prescribing doctor's own (ERX-04): the certificate's common name, the name an Indian DSC is issued
/// in (as on the holder's PAN or Aadhaar), against the doctor's name in HC360. Case, spacing, punctuation and titles ("Dr",
/// "Prof", "Shri"…) do not count, nor the order of the words, and the certificate may carry more words (a middle name);
/// every word of the doctor's name must be in it. So a colleague's DSC, or an organisation's certificate on a shared
/// workstation, is refused, and the printed copy never pairs a certificate with another doctor's registration number.
/// An initial in HC360 ("Dr A. K. Sharma") does not match a full name on the DSC: the administrator writes the doctor's
/// name in full under Hospital setup › Doctors and clinical staff.
/// </summary>
public static partial class DscSignerName
{
    private static readonly HashSet<string> Titles = new(StringComparer.Ordinal)
    {
        "DR", "DOCTOR", "PROF", "PROFESSOR", "MR", "MRS", "MS", "MISS", "SHRI", "SRI", "SMT",
    };

    public static bool Matches(string? certificateName, string? doctorName)
    {
        var certificate = Words(certificateName);
        var doctor = Words(doctorName);
        return doctor.Count > 0 && doctor.All(certificate.Contains);
    }

    /// <summary>The name's words in capitals, without punctuation or titles.</summary>
    internal static IReadOnlyList<string> Words(string? name) =>
        [.. Separators().Split((name ?? string.Empty).ToUpperInvariant()).Where(word => word.Length > 0 && !Titles.Contains(word))];

    // Letters with their combining marks (Devanagari vowel signs among them) and digits make words; anything else parts them.
    [GeneratedRegex(@"[^\p{L}\p{M}\p{Nd}]+")]
    private static partial Regex Separators();
}
