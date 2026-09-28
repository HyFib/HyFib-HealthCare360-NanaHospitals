using System.Globalization;
using HyFib.HealthCare360.Modules.Analytics.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Reports;

/// <summary>
/// Turns the raw key/value inputs of the report form, a schedule or a dashboard widget into <see cref="ReportArguments"/>
/// (pure and unit-tested). Only the report's declared parameters are read; anything else is ignored, so a report can
/// never be steered by an unexpected input. Errors are keyed by parameter so the form highlights the right field.
/// </summary>
public static class ReportParameterBinder
{
    /// <summary>Longest raw value accepted (ids, dates and choice codes are all far shorter).</summary>
    public const int MaxValueLength = 64;

    /// <summary>Earliest reportable date (guards against typing mistakes such as year 0202).</summary>
    public static readonly DateOnly EarliestDate = new(2000, 1, 1);

    private const string DateFormat = "yyyy-MM-dd";

    public static Result<ReportArguments> Bind(ReportDescriptor report, IReadOnlyDictionary<string, string?>? raw, DateOnly today)
    {
        var input = Normalize(raw);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        DateOnly from = today, to = today;
        string? period = null;
        string? facility = null;

        foreach (var parameter in report.Parameters)
        {
            input.TryGetValue(parameter.Key, out var value);
            switch (parameter.Kind)
            {
                case ReportParameterKind.Period:
                    (from, to, period) = BindPeriod(report, parameter, input, today, errors);
                    break;

                case ReportParameterKind.Facility:
                    if (value is not null)
                    {
                        if (value == ReportKeys.AllFacilities || Guid.TryParse(value, out _))
                        {
                            facility = value == ReportKeys.AllFacilities ? value : Guid.Parse(value).ToString();
                        }
                        else
                        {
                            errors[parameter.Key] = ["Choose a facility from the list."];
                        }
                    }

                    break;

                case ReportParameterKind.Department or ReportParameterKind.Practitioner or ReportParameterKind.Payer or ReportParameterKind.Ward
                    or ReportParameterKind.Store or ReportParameterKind.Theatre:
                    if (value is null)
                    {
                        if (parameter.Required)
                        {
                            errors[parameter.Key] = [$"Choose a {parameter.Label.ToLowerInvariant()}."];
                        }
                    }
                    else if (Guid.TryParse(value, out var id) && id != Guid.Empty)
                    {
                        values[parameter.Key] = id.ToString();
                    }
                    else
                    {
                        errors[parameter.Key] = [$"Choose a {parameter.Label.ToLowerInvariant()} from the list."];
                    }

                    break;

                case ReportParameterKind.Choice:
                {
                    var chosen = value ?? parameter.DefaultValue;
                    if (chosen is null)
                    {
                        if (parameter.Required)
                        {
                            errors[parameter.Key] = [$"Choose {parameter.Label.ToLowerInvariant()}."];
                        }
                    }
                    else if (parameter.Choices?.FirstOrDefault(c => string.Equals(c.Value, chosen, StringComparison.OrdinalIgnoreCase)) is { } match)
                    {
                        values[parameter.Key] = match.Value;
                    }
                    else
                    {
                        errors[parameter.Key] = [$"Choose {parameter.Label.ToLowerInvariant()} from the list."];
                    }

                    break;
                }

                case ReportParameterKind.Number:
                {
                    var text = value ?? parameter.DefaultValue;
                    if (text is null)
                    {
                        if (parameter.Required)
                        {
                            errors[parameter.Key] = [$"Enter {parameter.Label.ToLowerInvariant()}."];
                        }
                    }
                    else if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    {
                        errors[parameter.Key] = ["Enter a whole number."];
                    }
                    else if ((parameter.Min is { } min && number < min) || (parameter.Max is { } max && number > max))
                    {
                        errors[parameter.Key] = [$"Enter a number from {parameter.Min ?? int.MinValue} to {parameter.Max ?? int.MaxValue}."];
                    }
                    else
                    {
                        values[parameter.Key] = number.ToString(CultureInfo.InvariantCulture);
                    }

                    break;
                }
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("report.parameters", "Please correct the highlighted fields.", errors);
        }

        return new ReportArguments(from, to, period, facility, values);
    }

    /// <summary>Trims values, drops blanks and caps length (overlong values are treated as invalid by the typed checks).</summary>
    private static Dictionary<string, string> Normalize(IReadOnlyDictionary<string, string?>? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (raw is null)
        {
            return result;
        }

        foreach (var (key, value) in raw)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            result[key.Trim()] = trimmed.Length > MaxValueLength ? trimmed[..MaxValueLength] : trimmed;
        }

        return result;
    }

    private static (DateOnly From, DateOnly To, string? Period) BindPeriod(
        ReportDescriptor report, ReportParameter parameter, Dictionary<string, string> input, DateOnly today, Dictionary<string, string[]> errors)
    {
        input.TryGetValue(ReportKeys.Period, out var preset);
        input.TryGetValue(ReportKeys.From, out var fromText);
        input.TryGetValue(ReportKeys.To, out var toText);

        // Explicit dates without a preset mean custom dates; nothing at all means the report's default period.
        preset ??= fromText is not null || toText is not null ? ReportPeriods.Custom : parameter.DefaultValue ?? ReportPeriods.ThisMonth;
        if (!ReportPeriods.IsKnown(preset))
        {
            errors[ReportKeys.Period] = ["Choose a period from the list."];
            return (today, today, null);
        }

        DateOnly from, to;
        if (preset == ReportPeriods.Custom)
        {
            var fromOk = TryDate(fromText, out from);
            var toOk = TryDate(toText, out to);
            if (!fromOk)
            {
                errors[ReportKeys.From] = [fromText is null ? "Enter the start date." : "Enter a valid start date."];
            }

            if (!toOk)
            {
                errors[ReportKeys.To] = [toText is null ? "Enter the end date." : "Enter a valid end date."];
            }

            if (!fromOk || !toOk)
            {
                return (today, today, null);
            }
        }
        else
        {
            (from, to) = ReportPeriods.Resolve(preset, today);
        }

        if (from < EarliestDate)
        {
            errors[ReportKeys.From] = ["Choose a date from the year 2000 onwards."];
        }
        else if (to > today)
        {
            errors[ReportKeys.To] = ["The end date cannot be in the future."];
        }
        else if (from > to)
        {
            errors[ReportKeys.From] = ["The start date must be on or before the end date."];
        }
        else if (AnalyticsClock.Days(from, to) > report.MaxDays)
        {
            errors[ReportKeys.From] = [$"Choose a period of at most {report.MaxDays} days for this report."];
        }

        return (from, to, preset);
    }

    private static bool TryDate(string? text, out DateOnly date)
    {
        date = default;
        return text is not null && DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
