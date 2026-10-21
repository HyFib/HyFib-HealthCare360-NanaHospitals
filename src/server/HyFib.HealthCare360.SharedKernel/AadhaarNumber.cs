namespace HyFib.HealthCare360.SharedKernel;

/// <summary>
/// Aadhaar numbers and Virtual IDs. An Aadhaar number has 12 digits, the first 2 to 9 and the last a Verhoeff check
/// digit. A Virtual ID (VID), which a resident can generate to give instead of the number, has 16 digits; UIDAI
/// publishes no check digit for it, so only its length is checked here and UIDAI decides the rest.
/// </summary>
public static class AadhaarNumber
{
    /// <summary>The digits of an Aadhaar number or VID as typed (spaces and dashes dropped), or null for anything else.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Any(c => !char.IsAsciiDigit(c) && c is not ' ' and not '-'))
        {
            return null;
        }

        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
        return IsAadhaar(digits) || IsVirtualId(digits) ? digits : null;
    }

    /// <summary>12 digits, the first 2 to 9, with a valid Verhoeff check digit.</summary>
    public static bool IsAadhaar(string? digits) =>
        digits is { Length: 12 } && digits.All(char.IsAsciiDigit) && digits[0] is >= '2' and <= '9' && Verhoeff.IsValid(digits);

    /// <summary>16 digits.</summary>
    public static bool IsVirtualId(string? digits) => digits is { Length: 16 } && digits.All(char.IsAsciiDigit);

    /// <summary>The last four digits, the only part HC360 shows or keeps.</summary>
    public static string Last4(string digits)
    {
        ArgumentException.ThrowIfNullOrEmpty(digits);
        return digits.Length <= 4 ? digits : digits[^4..];
    }

    /// <summary>The Verhoeff checksum (dihedral group D5) that Aadhaar numbers end with.</summary>
    private static class Verhoeff
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

        public static bool IsValid(string digits)
        {
            var check = 0;
            for (var i = 0; i < digits.Length; i++)
            {
                var digit = digits[digits.Length - 1 - i] - '0';
                check = D[check, P[i % 8, digit]];
            }

            return check == 0;
        }
    }
}
