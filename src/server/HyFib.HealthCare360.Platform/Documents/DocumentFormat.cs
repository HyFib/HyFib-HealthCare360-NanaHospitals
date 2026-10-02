using System.Globalization;

namespace HyFib.HealthCare360.Platform.Documents;

/// <summary>
/// Dates, times and money as printed on documents, in India Standard Time and the staff app's style ("25 Sep 2026,
/// 3:45 pm", "₹1,23,456.00"). The output does not depend on the server's culture settings.
/// </summary>
public static class DocumentFormat
{
    // India has no daylight saving, so IST is a fixed offset.
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly NumberFormatInfo IndianNumbers = CultureInfo.GetCultureInfo("en-IN").NumberFormat;

    public const string Missing = "—";

    public static DateTimeOffset ToIst(DateTimeOffset instant) => instant.ToOffset(Ist);

    /// <summary>"25 Sep 2026" (IST).</summary>
    public static string Date(DateTimeOffset? instant) => instant is { } value ? Date(DateOnly.FromDateTime(ToIst(value).DateTime)) : Missing;

    /// <summary>"25 Sep 2026".</summary>
    public static string Date(DateOnly? date) =>
        date is { } d ? $"{d.Day:00} {Months[d.Month - 1]} {d.Year.ToString(CultureInfo.InvariantCulture)}" : Missing;

    /// <summary>"3:45 pm" (IST).</summary>
    public static string Time(DateTimeOffset? instant)
    {
        if (instant is not { } value)
        {
            return Missing;
        }

        var local = ToIst(value);
        var hour = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        return $"{hour.ToString(CultureInfo.InvariantCulture)}:{local.Minute:00} {(local.Hour < 12 ? "am" : "pm")}";
    }

    /// <summary>"25 Sep 2026, 3:45 pm" (IST).</summary>
    public static string DateTime(DateTimeOffset? instant) => instant is { } value ? $"{Date(value)}, {Time(value)}" : Missing;

    /// <summary>"₹1,23,456.00".</summary>
    public static string Money(decimal? amount) =>
        amount is { } value ? (value < 0 ? "-₹" : "₹") + Math.Abs(value).ToString("N2", IndianNumbers) : Missing;

    /// <summary>"1,23,456.00" with Indian digit grouping.</summary>
    public static string Amount(decimal? amount) => amount is { } value ? value.ToString("N2", IndianNumbers) : Missing;

    /// <summary>"1,23,456" with Indian digit grouping.</summary>
    public static string Number(long? value) => value is { } n ? n.ToString("N0", IndianNumbers) : Missing;
}
