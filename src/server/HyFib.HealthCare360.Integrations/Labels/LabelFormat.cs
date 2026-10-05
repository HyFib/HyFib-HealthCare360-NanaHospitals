using System.Globalization;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Integrations.Labels;

/// <summary>
/// Dates and times as labels print them, in India Standard Time like the rest of HC360, and the sizes labels share.
/// Labels use the 24-hour clock: no "am" or "pm" to misread on a small label.
/// </summary>
internal static class LabelFormat
{
    private static readonly string[] Months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    /// <summary>"26 Sep 10:32".</summary>
    public static string DayTime(DateTimeOffset instant)
    {
        var local = DocumentFormat.ToIst(instant);
        return string.Create(CultureInfo.InvariantCulture, $"{local.Day:00} {Months[local.Month - 1]} {local.Hour:00}:{local.Minute:00}");
    }

    /// <summary>"05 Nov 2026 14:00".</summary>
    public static string DateTime(DateTimeOffset instant)
    {
        var local = DocumentFormat.ToIst(instant);
        return string.Create(CultureInfo.InvariantCulture, $"{Date(DateOnly.FromDateTime(local.DateTime))} {local.Hour:00}:{local.Minute:00}");
    }

    /// <summary>"02 Oct 2026".</summary>
    public static string Date(DateOnly date) =>
        string.Create(CultureInfo.InvariantCulture, $"{date.Day:00} {Months[date.Month - 1]} {date.Year}");

    /// <summary>"Sep 2027", as medicine expiry dates are printed.</summary>
    public static string MonthYear(DateOnly date) => string.Create(CultureInfo.InvariantCulture, $"{Months[date.Month - 1]} {date.Year}");

    /// <summary>
    /// How much bigger than on the 50 × 25 mm label the text on this label is: smaller labels get smaller text (down to
    /// 80 %), bigger ones bigger text (up to 160 %).
    /// </summary>
    public static double Scale(LabelMedia media) =>
        Math.Clamp(Math.Min((double)media.WidthMm / 50, (double)media.HeightMm / 25), 0.8, 1.6);

    /// <summary>The parts that have text, joined.</summary>
    public static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
