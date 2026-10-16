using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.SharedKernel;

/// <summary>
/// The Verhoeff check digit (dihedral group D5), which catches every single-digit error and every swap of two adjacent
/// digits. Aadhaar numbers end in one (UIDAI). ABHA numbers do not: see <see cref="Abha"/>.
/// </summary>
public static class Verhoeff
{
    private static readonly int[,] D =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 2, 3, 4, 0, 6, 7, 8, 9, 5 }, { 2, 3, 4, 0, 1, 7, 8, 9, 5, 6 },
        { 3, 4, 0, 1, 2, 8, 9, 5, 6, 7 }, { 4, 0, 1, 2, 3, 9, 5, 6, 7, 8 }, { 5, 9, 8, 7, 6, 0, 4, 3, 2, 1 },
        { 6, 5, 9, 8, 7, 1, 0, 4, 3, 2 }, { 7, 6, 5, 9, 8, 2, 1, 0, 4, 3 }, { 8, 7, 6, 5, 9, 3, 2, 1, 0, 4 },
        { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 },
    };

    private static readonly int[,] P =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, { 1, 5, 7, 6, 2, 8, 3, 0, 9, 4 }, { 5, 8, 0, 3, 7, 9, 6, 1, 4, 2 },
        { 8, 9, 1, 6, 0, 4, 3, 5, 2, 7 }, { 9, 4, 5, 3, 1, 2, 6, 8, 7, 0 }, { 4, 2, 8, 6, 5, 7, 3, 9, 0, 1 },
        { 2, 7, 9, 3, 8, 0, 6, 4, 1, 5 }, { 7, 0, 4, 6, 9, 1, 3, 2, 5, 8 },
    };

    private static readonly int[] Inverse = [0, 4, 3, 2, 1, 5, 6, 7, 8, 9];

    /// <summary>True when <paramref name="digits"/> (digits only) ends in its correct check digit.</summary>
    public static bool IsValid(string digits)
    {
        if (digits.Length < 2 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        var check = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            check = D[check, P[i % 8, digits[digits.Length - 1 - i] - '0']];
        }

        return check == 0;
    }

    /// <summary>The check digit to append to <paramref name="digits"/> (digits only).</summary>
    public static char CheckDigit(string digits)
    {
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Only digits have a Verhoeff check digit.", nameof(digits));
        }

        var check = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            check = D[check, P[(i + 1) % 8, digits[digits.Length - 1 - i] - '0']];
        }

        return (char)('0' + Inverse[check]);
    }
}

/// <summary>
/// The identifiers of an Ayushman Bharat Health Account (ABDM): the ABHA number, 14 digits written
/// <c>xx-xxxx-xxxx-xxxx</c>, and the ABHA address, a handle such as <c>meera.k@abdm</c> (<c>@sbx</c> on the ABDM
/// sandbox). No NHA or ABDM specification gives the ABHA number a check digit (the NRCeS implementation guide's own
/// example patients carry numbers that fail a Verhoeff check), so only its length and characters are checked here;
/// whether a number belongs to a real account is for ABDM to say when it is verified through the gateway.
/// </summary>
public static partial class Abha
{
    public const int NumberLength = 14;

    /// <summary>The digits of a number as typed ("91-1234 5678-9012" → "91123456789012").</summary>
    public static string Digits(string? value) => value is null ? string.Empty : new string(value.Where(char.IsAsciiDigit).ToArray());

    /// <summary>
    /// 14 digits, grouped with spaces or hyphens or run together. Anything else (letters, other separators, 13 or 15
    /// digits) is not an ABHA number. Any 14 digits are accepted: there is no published check digit to test.
    /// </summary>
    public static bool IsValidNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => !char.IsAsciiDigit(c) && c is not ' ' and not '-'))
        {
            return false;
        }

        var digits = Digits(value);
        return digits.Length == NumberLength;
    }

    /// <summary>The display form <c>xx-xxxx-xxxx-xxxx</c>; a value that is not 14 digits is returned trimmed.</summary>
    public static string Format(string value)
    {
        var digits = Digits(value);
        return digits.Length == NumberLength ? $"{digits[..2]}-{digits[2..6]}-{digits[6..10]}-{digits[10..]}" : value.Trim();
    }

    /// <summary>The masked form shown where the full number is not needed: <c>XX-XXXX-XXXX-1234</c>.</summary>
    public static string Mask(string value)
    {
        var digits = Digits(value);
        return "XX-XXXX-XXXX-" + (digits.Length >= 4 ? digits[^4..] : digits);
    }

    /// <summary>
    /// An ABHA address: 3 to 32 letters or digits, which may be joined by single dots or underscores, then <c>@abdm</c>
    /// (or <c>@sbx</c> for the ABDM sandbox). Letter case does not matter; addresses are stored in lower case.
    /// </summary>
    public static bool IsValidAddress(string? value) => value is not null && AddressPattern().IsMatch(value.Trim());

    /// <summary>The stored form of an address: trimmed and lower case; null for an empty value.</summary>
    public static string? NormalizeAddress(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    [GeneratedRegex(@"^(?=[A-Za-z0-9._]{3,32}@)[A-Za-z0-9]+(?:[._][A-Za-z0-9]+)*@(?:abdm|sbx)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex AddressPattern();
}
