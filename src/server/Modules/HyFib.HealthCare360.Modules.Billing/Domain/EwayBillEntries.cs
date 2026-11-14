using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>
/// An e-way bill generated outside HC360 (on the e-way bill portal, by the transporter, or on the e-invoice portal's own
/// screens) and entered by hand from its print (form EWB-01), checked against what the e-way bill system itself allows
/// before it is put on the invoice's registration (CGST rule 138).
/// <list type="bullet">
/// <item>The number is the portal's, read as it prints it: 12 digits, or 14 for one generated from 1 January 2027 (GSTN
/// advisory 687 of 4 November 2026). No check digit or inner structure is published, so it is read no further.</item>
/// <item>Since 1 January 2025 the portal generates an e-way bill only for a document at most 180 days old (its error 820),
/// and extends one to at most 360 days after its generation (821), by the GSTN advisory of 17 December 2024.</item>
/// <item>A bill with only Part-A filled has no validity until its Part-B is: its valid-until is then left blank.</item>
/// </list>
/// </summary>
public static class EwayBillEntries
{
    /// <summary>The first day (IST) of 14-digit e-way bill numbers.</summary>
    public static readonly DateOnly FourteenDigitsFrom = new(2027, 1, 1);

    /// <summary>The first day (IST) the portal refuses a document more than <see cref="MaxDocumentAgeDays"/> days old.</summary>
    public static readonly DateOnly DocumentAgeLimitFrom = new(2025, 1, 1);

    public const int MaxDocumentAgeDays = 180;

    public const int MaxValidityDays = 360;

    /// <summary>
    /// The e-way bill number in <paramref name="text"/> as its print shows it, with the spaces some prints put between groups
    /// of digits; null when it is not 12 or 14 digits (a leading zero included: the portal gives the number as a number).
    /// </summary>
    public static long? Number(string? text)
    {
        var digits = (text ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal);
        return digits.Length is 12 or 14 && digits[0] != '0' && digits.All(char.IsAsciiDigit)
            ? long.Parse(digits, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// What stops the e-way bill entered from being one the portal could have generated for an invoice of
    /// <paramref name="invoiceDate"/>, at <paramref name="now"/>; empty when nothing does.
    /// </summary>
    public static IReadOnlyList<string> Problems(
        string number, DateTimeOffset generatedAt, DateTimeOffset? validUntil, DateOnly invoiceDate, DateTimeOffset now)
    {
        var problems = new List<string>();
        var generated = BillingTime.LocalDate(generatedAt);
        var digits = Number(number) is { } read ? read.ToString(CultureInfo.InvariantCulture).Length : 0;
        if (digits == 0)
        {
            problems.Add("The e-way bill number is the portal's 12 digits (14 for one generated from 1 January 2027).");
        }
        else if (digits == 14 && generated < FourteenDigitsFrom)
        {
            problems.Add($"E-way bill numbers have 14 digits only from 1 January 2027: one generated on {Day(generated)} has 12.");
        }

        if (generatedAt > now.AddMinutes(10))
        {
            problems.Add("The e-way bill is dated in the future.");
        }

        if (generated < invoiceDate)
        {
            problems.Add($"The e-way bill is dated {Day(generated)}, before the invoice of {Day(invoiceDate)} it names.");
        }
        else if (generated >= DocumentAgeLimitFrom && generated.DayNumber - invoiceDate.DayNumber > MaxDocumentAgeDays)
        {
            problems.Add($"The portal generates an e-way bill only for an invoice at most {MaxDocumentAgeDays} days old: "
                + $"this one is dated {Day(generated)}, {generated.DayNumber - invoiceDate.DayNumber} days after the invoice.");
        }

        if (validUntil is { } until && until <= generatedAt)
        {
            problems.Add("The e-way bill must be valid until a time after it was generated.");
        }
        else if (validUntil is { } far && far > generatedAt.AddDays(MaxValidityDays))
        {
            problems.Add($"The portal extends an e-way bill to at most {MaxValidityDays} days after its generation.");
        }

        return problems;
    }

    private static string Day(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
