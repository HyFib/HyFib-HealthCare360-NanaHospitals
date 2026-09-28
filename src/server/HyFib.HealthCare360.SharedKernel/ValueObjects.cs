using System.Globalization;

namespace HyFib.HealthCare360.SharedKernel;

/// <summary>Postal address (Indian format by default). Stored inline as a complex type.</summary>
public sealed record Address
{
    public string? Line1 { get; init; }
    public string? Line2 { get; init; }
    public string? City { get; init; }
    public string? District { get; init; }
    public string? State { get; init; }
    public string? PinCode { get; init; }
    public string? Country { get; init; } = "India";

    public override string ToString() =>
        string.Join(", ", new[] { Line1, Line2, City, District, State, PinCode }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
}

/// <summary>Phone number helpers. Numbers are stored in E.164 form (e.g. +919876543210).</summary>
public static class Phone
{
    /// <summary>
    /// Normalises Indian and international numbers to E.164. Ten-digit numbers get the default
    /// country code; a leading trunk "0" is dropped. Returns <c>null</c> when the input is not a phone number.
    /// </summary>
    public static string? Normalize(string? input, string defaultCountryCode = "91")
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim();
        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0)
        {
            return null;
        }

        if (trimmed.StartsWith('+'))
        {
            return digits.Length is >= 8 and <= 15 ? "+" + digits : null;
        }

        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
            return digits.Length is >= 8 and <= 15 ? "+" + digits : null;
        }

        return digits.Length switch
        {
            10 => $"+{defaultCountryCode}{digits}",
            11 when digits[0] == '0' => $"+{defaultCountryCode}{digits[1..]}",
            12 when digits.StartsWith(defaultCountryCode, StringComparison.Ordinal) => "+" + digits,
            _ => null,
        };
    }

    /// <summary>True for a valid Indian mobile number (+91 followed by 10 digits starting 6–9).</summary>
    public static bool IsIndianMobile(string? e164) =>
        e164 is { Length: 13 } && e164.StartsWith("+91", StringComparison.Ordinal) && e164[3] is >= '6' and <= '9'
        && e164.Skip(1).All(char.IsAsciiDigit);

    /// <summary>Masks all but the last four digits: +91******3210.</summary>
    public static string Mask(string? e164)
    {
        if (string.IsNullOrEmpty(e164) || e164.Length <= 4)
        {
            return e164 ?? string.Empty;
        }

        var prefixLength = e164.StartsWith("+91", StringComparison.Ordinal) ? 3 : 1;
        return string.Concat(e164.AsSpan(0, prefixLength), new string('*', e164.Length - prefixLength - 4), e164.AsSpan(e164.Length - 4));
    }
}

/// <summary>Age calculations used on banners, prints and reference-range selection.</summary>
public static class Age
{
    public static (int Years, int Months, int Days) Between(DateOnly dateOfBirth, DateOnly asOf)
    {
        if (asOf < dateOfBirth)
        {
            return (0, 0, 0);
        }

        var years = asOf.Year - dateOfBirth.Year;
        var months = asOf.Month - dateOfBirth.Month;
        var days = asOf.Day - dateOfBirth.Day;

        if (days < 0)
        {
            months--;
            var previousMonth = asOf.AddMonths(-1);
            days += DateTime.DaysInMonth(previousMonth.Year, previousMonth.Month);
        }

        if (months < 0)
        {
            years--;
            months += 12;
        }

        return (years, months, days);
    }

    public static int InYears(DateOnly dateOfBirth, DateOnly asOf) => Between(dateOfBirth, asOf).Years;

    public static int InDays(DateOnly dateOfBirth, DateOnly asOf) => Math.Max(asOf.DayNumber - dateOfBirth.DayNumber, 0);

    /// <summary>Compact display used in Indian hospitals: "34Y 2M", "5M 12D" or "9D".</summary>
    public static string Format(DateOnly dateOfBirth, DateOnly asOf)
    {
        var (years, months, days) = Between(dateOfBirth, asOf);
        return years >= 1
            ? months > 0 ? $"{years}Y {months}M" : $"{years}Y"
            : months >= 1 ? $"{months}M {days}D" : $"{days}D";
    }

    /// <summary>Estimated date of birth when only an age is known (common at registration).</summary>
    public static DateOnly EstimateDateOfBirth(int years, int months, DateOnly asOf) =>
        asOf.AddYears(-Math.Max(years, 0)).AddMonths(-Math.Max(months, 0));
}

/// <summary>Currency arithmetic helpers (2 decimals, half away from zero, as used on Indian invoices).</summary>
public static class Money
{
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static decimal PercentOf(decimal amount, decimal percent) => Round(amount * percent / 100m);

    /// <summary>Splits an intra-state GST amount into equal CGST and SGST (SGST takes any rounding paisa).</summary>
    public static (decimal Cgst, decimal Sgst) SplitGst(decimal gstAmount)
    {
        var cgst = Round(gstAmount / 2m);
        return (cgst, Round(gstAmount - cgst));
    }

    /// <summary>Amount in Indian digit grouping, e.g. 12,34,567.50.</summary>
    public static string FormatInr(decimal amount) =>
        amount.ToString("#,##,##,##0.00", CultureInfo.GetCultureInfo("en-IN"));
}
