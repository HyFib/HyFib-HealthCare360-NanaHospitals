using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>
/// Formats, normalisation and masking for Indian employment identifiers. Masked forms are what every screen and
/// payslip shows; the full values exist only encrypted (and in the audited bank-transfer export).
/// </summary>
public static partial class HrIdentifiers
{
    /// <summary>PAN: five letters, four digits, one letter (ABCDE1234F).</summary>
    public static bool IsValidPan(string? value) => value is not null && PanRegex().IsMatch(Normalize(value));

    /// <summary>IFSC: four-letter bank code, a zero, six-character branch code (SBIN0001234).</summary>
    public static bool IsValidIfsc(string? value) => value is not null && IfscRegex().IsMatch(Normalize(value));

    /// <summary>Indian bank account numbers are 9 to 18 digits.</summary>
    public static bool IsValidBankAccount(string? value) =>
        value is not null && Normalize(value) is { Length: >= 9 and <= 18 } digits && digits.All(char.IsAsciiDigit);

    /// <summary>EPFO Universal Account Number: 12 digits.</summary>
    public static bool IsValidUan(string? value) =>
        value is not null && Normalize(value) is { Length: 12 } digits && digits.All(char.IsAsciiDigit);

    /// <summary>ESIC insurance number: 10 digits (17 for some employer-generated numbers).</summary>
    public static bool IsValidEsiNumber(string? value) =>
        value is not null && Normalize(value) is { Length: 10 or 17 } digits && digits.All(char.IsAsciiDigit);

    /// <summary>Upper-case letters and digits only.</summary>
    public static string Normalize(string value) => Masking.Normalize(value);

    /// <summary>"XXXXXXXX1234".</summary>
    public static string MaskAccount(string accountNumber) => Masking.MaskIdentifier(accountNumber, 4);

    /// <summary>Keeps the bank code, hides the branch: "SBIN0XXXXXX".</summary>
    public static string MaskIfsc(string ifsc)
    {
        var normalized = Normalize(ifsc);
        return normalized.Length <= 5 ? normalized : normalized[..5] + new string('X', normalized.Length - 5);
    }

    /// <summary>"XXXXXX234F".</summary>
    public static string MaskPan(string pan) => Masking.MaskIdentifier(pan, 4);

    [GeneratedRegex("^[A-Z]{5}[0-9]{4}[A-Z]$")]
    private static partial Regex PanRegex();

    [GeneratedRegex("^[A-Z]{4}0[A-Z0-9]{6}$")]
    private static partial Regex IfscRegex();
}
