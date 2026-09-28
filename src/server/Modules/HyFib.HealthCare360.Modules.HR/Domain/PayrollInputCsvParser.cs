using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Modules.HR.Domain;

public sealed record PayrollInputCsvLine(int LineNumber, string EmployeeCode, string ComponentCode, decimal Amount, string? Description);

public sealed record PayrollInputCsvResult(IReadOnlyList<PayrollInputCsvLine> Lines, IReadOnlyList<string> Errors);

/// <summary>
/// Parses bulk payroll inputs (for example the canteen's monthly staff-credit statement):
/// <c>employeeCode,componentCode,amount[,description]</c>, header row optional, quoted fields allowed.
/// Every bad line is reported with its line number; good lines are still returned so the caller decides
/// whether to import partially (it does not — imports are all-or-nothing).
/// </summary>
public static class PayrollInputCsvParser
{
    public const int MaxLines = 5000;
    public const decimal MaxAmount = 10_000_000m;

    public static PayrollInputCsvResult Parse(string? csv)
    {
        var lines = new List<PayrollInputCsvLine>();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(csv))
        {
            errors.Add("The file is empty.");
            return new PayrollInputCsvResult(lines, errors);
        }

        var rows = csv.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (rows.Length > MaxLines + 1)
        {
            errors.Add($"Import at most {MaxLines} lines at a time.");
            return new PayrollInputCsvResult(lines, errors);
        }

        for (var index = 0; index < rows.Length; index++)
        {
            var lineNumber = index + 1;
            var row = rows[index].Trim();
            if (row.Length == 0)
            {
                continue;
            }

            var fields = SplitCsvLine(row);
            if (index == 0 && fields.Count > 0 && string.Equals(fields[0].Trim(), "employeeCode", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fields.Count < 3)
            {
                errors.Add($"Line {lineNumber}: expected employee code, component code and amount.");
                continue;
            }

            var employeeCode = fields[0].Trim().ToUpperInvariant();
            var componentCode = fields[1].Trim().ToUpperInvariant();
            var description = fields.Count > 3 && !string.IsNullOrWhiteSpace(fields[3]) ? fields[3].Trim() : null;

            if (employeeCode.Length is 0 or > 24)
            {
                errors.Add($"Line {lineNumber}: invalid employee code.");
                continue;
            }

            if (componentCode.Length is 0 or > 24 || !componentCode.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                errors.Add($"Line {lineNumber}: invalid component code.");
                continue;
            }

            if (!decimal.TryParse(fields[2].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0 || amount > MaxAmount)
            {
                errors.Add($"Line {lineNumber}: the amount must be a positive number.");
                continue;
            }

            if (description is { Length: > 200 })
            {
                description = description[..200];
            }

            lines.Add(new PayrollInputCsvLine(lineNumber, employeeCode, componentCode, Money.Round(amount), description));
        }

        if (lines.Count == 0 && errors.Count == 0)
        {
            errors.Add("The file has no data lines.");
        }

        return new PayrollInputCsvResult(lines, errors);
    }

    /// <summary>RFC 4180 field splitting for one line (quoted fields may contain commas and doubled quotes).</summary>
    public static IReadOnlyList<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
