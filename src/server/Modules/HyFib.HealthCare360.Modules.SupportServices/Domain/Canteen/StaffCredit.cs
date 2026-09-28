using System.Globalization;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;

public enum StaffCreditEntryKind
{
    Sale = 0,
    Cancellation,
    PayrollDeduction,
    Payment,
}

/// <summary>
/// Staff canteen credit ledger (CAN-03), keyed by employee code: credit sales add to the balance; cancellations,
/// payroll deductions and cash payments reduce it. HR deducts each month's closing balance from salary using the
/// monthly export, then records the settlement, which is idempotent per employee and month.
/// </summary>
public sealed class StaffCreditEntry : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string EmployeeCode { get; set; } = string.Empty;
    public string? EmployeeName { get; set; }
    public DateOnly EntryDate { get; set; }
    public StaffCreditEntryKind Kind { get; set; }

    /// <summary>Signed amount: positive adds to what the employee owes.</summary>
    public decimal Amount { get; set; }

    public Guid? SaleId { get; set; }
    public string? Reference { get; set; }

    /// <summary>For payroll deductions: the month ("yyyy-MM") whose balance was deducted.</summary>
    public string? SettlesPeriod { get; set; }

    public string? Notes { get; set; }
}

public sealed record StaffStatementLine(DateOnly Date, StaffCreditEntryKind Kind, decimal Amount, string? Reference, decimal Balance);

public sealed record StaffStatement(
    string EmployeeCode,
    string? EmployeeName,
    string Period,
    decimal OpeningBalance,
    decimal Purchases,
    decimal Credits,
    decimal ClosingBalance,
    IReadOnlyList<StaffStatementLine> Lines);

public sealed record CreditEntryData(DateOnly Date, StaffCreditEntryKind Kind, decimal Amount, string? Reference);

public static partial class StaffCreditRules
{
    public static string PeriodOf(DateOnly date) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>Parses "yyyy-MM" into the first day of the month.</summary>
    public static bool TryParsePeriod(string? period, out DateOnly firstDay) =>
        DateOnly.TryParseExact((period ?? string.Empty) + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out firstDay);

    public static string NormalizeEmployeeCode(string code) => code.Trim().ToUpperInvariant();

    public static bool IsValidEmployeeCode(string? code) => code is not null && EmployeeCodeRegex().IsMatch(code.Trim());

    /// <summary>A credit sale must keep the outstanding balance within the limit (a limit of zero or less means no limit).</summary>
    public static Result CheckLimit(decimal outstanding, decimal saleAmount, decimal limit)
    {
        if (limit > 0 && outstanding + saleAmount > limit)
        {
            return Error.Rule("canteen.credit_limit",
                $"Staff credit limit of Rs. {Money.FormatInr(limit)} would be exceeded (outstanding Rs. {Money.FormatInr(outstanding)}). Pay by cash, UPI or card.");
        }

        return Result.Success();
    }

    public static StaffStatement BuildStatement(string employeeCode, string? employeeName, string period, decimal openingBalance, IEnumerable<CreditEntryData> entries)
    {
        var balance = openingBalance;
        var lines = new List<StaffStatementLine>();
        foreach (var entry in entries.OrderBy(e => e.Date))
        {
            balance += entry.Amount;
            lines.Add(new StaffStatementLine(entry.Date, entry.Kind, entry.Amount, entry.Reference, balance));
        }

        return new StaffStatement(employeeCode, employeeName, period, openingBalance,
            lines.Where(l => l.Amount > 0).Sum(l => l.Amount), -lines.Where(l => l.Amount < 0).Sum(l => l.Amount), balance, lines);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9/-]{1,19}$")]
    private static partial Regex EmployeeCodeRegex();
}
