using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Modules.HR.Services;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;
using HyFib.HealthCare360.Modules.SupportServices.Features.Canteen;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// Staff canteen credit (CAN-03): balances, monthly statements, the payroll export HR uses for salary deduction, and
/// recording the deduction (idempotent per employee and month, so a re-run payroll never deducts twice).
/// </summary>
public sealed class StaffCreditService(
    HospitalDbContext db,
    IEmployeeDirectory employees,
    PayrollInputService payroll,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public async Task<IReadOnlyList<StaffBalanceDto>> BalancesAsync(bool outstandingOnly, CancellationToken ct)
    {
        var rows = await db.Set<StaffCreditEntry>().AsNoTracking()
            .GroupBy(e => e.EmployeeCode)
            .Select(g => new { EmployeeCode = g.Key, Balance = g.Sum(e => e.Amount), LastEntryDate = g.Max(e => e.EntryDate) })
            .ToListAsync(ct);
        var names = await LatestNamesAsync(ct);
        return rows
            .Where(r => !outstandingOnly || r.Balance != 0)
            .OrderBy(r => r.EmployeeCode, StringComparer.Ordinal)
            .Select(r => new StaffBalanceDto(r.EmployeeCode, names.GetValueOrDefault(r.EmployeeCode), r.Balance, r.LastEntryDate))
            .ToList();
    }

    public async Task<Result<StaffStatement>> StatementAsync(string employeeCode, string period, CancellationToken ct)
    {
        if (!StaffCreditRules.TryParsePeriod(period, out var firstDay))
        {
            return Error.Field("period", "Use the month as yyyy-MM.");
        }

        var code = StaffCreditRules.NormalizeEmployeeCode(employeeCode);
        var nextMonth = firstDay.AddMonths(1);
        var opening = await db.Set<StaffCreditEntry>().Where(e => e.EmployeeCode == code && e.EntryDate < firstDay).SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;
        var entries = await db.Set<StaffCreditEntry>().AsNoTracking()
            .Where(e => e.EmployeeCode == code && e.EntryDate >= firstDay && e.EntryDate < nextMonth)
            .OrderBy(e => e.EntryDate).ThenBy(e => e.CreatedAt)
            .Select(e => new { e.EntryDate, e.Kind, e.Amount, e.Reference, e.EmployeeName })
            .ToListAsync(ct);
        var name = entries.Select(e => e.EmployeeName).LastOrDefault(n => n != null) ?? (await LatestNamesAsync(ct)).GetValueOrDefault(code);
        return StaffCreditRules.BuildStatement(code, name, StaffCreditRules.PeriodOf(firstDay), opening,
            entries.Select(e => new CreditEntryData(e.EntryDate, e.Kind, e.Amount, e.Reference)));
    }

    /// <summary>
    /// Payroll export (CSV) of the month: opening balance, purchases, credits and the amount to deduct per employee.
    /// Fields are quoted and formula-like values neutralised (CSV injection), and the export is audited.
    /// </summary>
    public async Task<Result<(byte[] Content, string FileName)>> ExportAsync(string period, CancellationToken ct)
    {
        if (!StaffCreditRules.TryParsePeriod(period, out var firstDay))
        {
            return Error.Field("period", "Use the month as yyyy-MM.");
        }

        var nextMonth = firstDay.AddMonths(1);
        var rows = await db.Set<StaffCreditEntry>().AsNoTracking()
            .Where(e => e.EntryDate < nextMonth)
            .GroupBy(e => e.EmployeeCode)
            .Select(g => new
            {
                EmployeeCode = g.Key,
                Opening = g.Where(e => e.EntryDate < firstDay).Sum(e => e.Amount),
                Purchases = g.Where(e => e.EntryDate >= firstDay && e.Amount > 0).Sum(e => e.Amount),
                Credits = g.Where(e => e.EntryDate >= firstDay && e.Amount < 0).Sum(e => e.Amount),
            })
            .ToListAsync(ct);
        var names = await LatestNamesAsync(ct);
        var monthText = StaffCreditRules.PeriodOf(firstDay);

        var csv = new StringBuilder();
        csv.AppendLine("EmployeeCode,EmployeeName,Period,OpeningBalance,Purchases,Credits,AmountToDeduct");
        foreach (var row in rows.OrderBy(r => r.EmployeeCode, StringComparer.Ordinal))
        {
            var closing = row.Opening + row.Purchases + row.Credits;
            if (row.Purchases == 0 && closing == 0)
            {
                continue;
            }

            csv.AppendJoin(',', Field(row.EmployeeCode), Field(names.GetValueOrDefault(row.EmployeeCode)), Field(monthText), Amount(row.Opening),
                Amount(row.Purchases), Amount(-row.Credits), Amount(Math.Max(0, closing)));
            csv.AppendLine();
        }

        await audit.WriteAsync(new AuditEntry(AuditCategories.Data, "canteen.staff_credit.exported", $"Exported staff canteen credit for {monthText}")
        {
            EntityType = nameof(StaffCreditEntry),
            EntityId = monthText,
            Details = new { employees = rows.Count },
        }, ct);

        var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return (content, $"canteen-staff-credit-{monthText}.csv");
    }

    /// <summary>Records HR's payroll deduction of each employee's balance at the end of the month.</summary>
    public async Task<Result<SettlementResult>> SettleAsync(SettleStaffCreditRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (!StaffCreditRules.TryParsePeriod(request.Period, out var firstDay))
        {
            return Error.Field("period", "Use the month as yyyy-MM.");
        }

        var today = time.Today();
        var nextMonth = firstDay.AddMonths(1);
        if (nextMonth > today)
        {
            return Error.Rule("canteen.period_open", "A month can be settled only after it has ended.");
        }

        var period = StaffCreditRules.PeriodOf(firstDay);
        var codes = request.EmployeeCodes?.Select(StaffCreditRules.NormalizeEmployeeCode).ToList();
        var balances = await db.Set<StaffCreditEntry>().AsNoTracking()
            .Where(e => e.EntryDate < nextMonth && (codes == null || codes.Contains(e.EmployeeCode)))
            .GroupBy(e => e.EmployeeCode)
            .Select(g => new { EmployeeCode = g.Key, Balance = g.Sum(e => e.Amount) })
            .ToListAsync(ct);
        var settled = await db.Set<StaffCreditEntry>().AsNoTracking()
            .Where(e => e.Kind == StaffCreditEntryKind.PayrollDeduction && e.SettlesPeriod == period)
            .Select(e => e.EmployeeCode)
            .ToListAsync(ct);
        var names = await LatestNamesAsync(ct);

        var due = balances.Where(b => b.Balance > 0 && !settled.Contains(b.EmployeeCode)).ToList();

        // HR's deduction service saves as it goes, so the whole settlement runs in one transaction.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var skipped = new List<string>();
        var deducted = 0;
        var amount = 0m;
        foreach (var balance in due)
        {
            var employee = await employees.FindByCodeAsync(balance.EmployeeCode, ct);
            if (employee is null)
            {
                skipped.Add($"{balance.EmployeeCode}: not found in HR; collect Rs. {Money.FormatInr(balance.Balance)} at the counter.");
                continue;
            }

            var posted = await payroll.PostDeductionAsync(
                employee.EmployeeId,
                firstDay.Year,
                firstDay.Month,
                "CANTEEN",
                balance.Balance,
                $"Canteen staff credit {period}",
                $"canteen:{period}:{balance.EmployeeCode}",
                ct);
            if (posted.IsFailure)
            {
                skipped.Add($"{balance.EmployeeCode} ({employee.FullName}): {posted.Error!.Message}");
                continue;
            }

            deducted++;
            amount += balance.Balance;
            db.Add(new StaffCreditEntry
            {
                FacilityId = facilityId,
                EmployeeCode = balance.EmployeeCode,
                EmployeeName = names.GetValueOrDefault(balance.EmployeeCode),
                EntryDate = today,
                Kind = StaffCreditEntryKind.PayrollDeduction,
                Amount = -balance.Balance,
                Reference = request.Reference.Trim(),
                SettlesPeriod = period,
                Notes = $"Salary deduction for {period}",
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new SettlementResult(period, deducted, amount, skipped);
    }

    /// <summary>Cash/UPI repayment of a staff balance at the canteen counter.</summary>
    public async Task<Result<StaffBalanceDto>> RecordPaymentAsync(StaffPaymentRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var code = StaffCreditRules.NormalizeEmployeeCode(request.EmployeeCode);
        var balance = await db.Set<StaffCreditEntry>().Where(e => e.EmployeeCode == code).SumAsync(e => (decimal?)e.Amount, ct) ?? 0m;
        if (request.Amount > balance)
        {
            return Error.Rule("canteen.overpayment", $"The outstanding balance is Rs. {Money.FormatInr(balance)}.");
        }

        var names = await LatestNamesAsync(ct);
        var today = time.Today();
        db.Add(new StaffCreditEntry
        {
            FacilityId = facilityId,
            EmployeeCode = code,
            EmployeeName = names.GetValueOrDefault(code),
            EntryDate = today,
            Kind = StaffCreditEntryKind.Payment,
            Amount = -Money.Round(request.Amount),
            Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim(),
        });
        await db.SaveChangesAsync(ct);
        return new StaffBalanceDto(code, names.GetValueOrDefault(code), balance - Money.Round(request.Amount), today);
    }

    private async Task<Dictionary<string, string>> LatestNamesAsync(CancellationToken ct) =>
        (await db.Set<StaffCreditEntry>().AsNoTracking()
            .Where(e => e.EmployeeName != null)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new { e.EmployeeCode, e.EmployeeName })
            .ToListAsync(ct))
        .GroupBy(e => e.EmployeeCode)
        .ToDictionary(g => g.Key, g => g.Last().EmployeeName!);

    private static string Field(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
