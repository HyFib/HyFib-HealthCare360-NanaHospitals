using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Modules.HR.Domain.Statutory;

/// <summary>One member's month for the ECR, from the approved payslip.</summary>
public sealed record EcrMemberMonth(
    string? Uan, string EmployeeCode, string Name, DateOnly? DateOfBirth, decimal GrossWages, decimal EpfWages, decimal EmployeePf, decimal EmployerPf,
    decimal LopDays, bool EpsMember = true);

public sealed record EcrLine(string Uan, string Name, long Gross, long Epf, long Eps, long Edli, long EpfContribution, long EpsContribution, long Difference, long NcpDays);

public sealed record EcrResult(string Text, IReadOnlyList<EcrLine> Lines, IReadOnlyList<string> Problems)
{
    public long TotalEpf => Lines.Sum(l => l.EpfContribution);
    public long TotalEps => Lines.Sum(l => l.EpsContribution);
    public long TotalDifference => Lines.Sum(l => l.Difference);
}

/// <summary>
/// The EPFO ECR for a wage month. EPS takes 8.33 % of wages up to ₹15,000 from the employer's 12 % (para 3(2) of the EPF
/// Scheme; EPS 1995, para 3), and the rest of the employer's share is the "EPF EPS difference". A member who has turned 58
/// is out of the pension fund, so the whole employer share goes to EPF. EDLI wages stop at ₹15,000 as well. NCP
/// (non-contributing) days are the month's loss-of-pay days, rounded to whole days. Members without a UAN cannot be
/// filed and are listed as problems.
/// </summary>
public static class EcrFile
{
    public const decimal EpsRate = 0.0833m;
    public const decimal EpsWageCeiling = 15_000m;
    public const decimal EdliWageCeiling = 15_000m;
    public const int EpsAgeLimit = 58;

    public static EcrResult Build(IEnumerable<EcrMemberMonth> members, int year, int month)
    {
        ArgumentNullException.ThrowIfNull(members);
        var wageMonthStart = new DateOnly(year, month, 1);
        var lines = new List<EcrLine>();
        var problems = new List<string>();
        foreach (var m in members.Where(m => m.EmployeePf > 0 || m.EpfWages > 0))
        {
            if (!HrIdentifiers.IsValidUan(m.Uan))
            {
                problems.Add($"{m.EmployeeCode} {m.Name}: no valid UAN, so the member cannot be in the ECR.");
                continue;
            }

            lines.Add(Line(m, wageMonthStart));
        }

        var text = new StringBuilder();
        foreach (var l in lines)
        {
            text.Append(StatutoryLayouts.Ecr.Line(new Dictionary<string, string>
            {
                ["UAN"] = l.Uan,
                ["Member Name"] = l.Name,
                ["Gross Wages"] = Whole(l.Gross),
                ["EPF Wages"] = Whole(l.Epf),
                ["EPS Wages"] = Whole(l.Eps),
                ["EDLI Wages"] = Whole(l.Edli),
                ["EPF Contribution remitted"] = Whole(l.EpfContribution),
                ["EPS Contribution remitted"] = Whole(l.EpsContribution),
                ["EPF EPS Difference remitted"] = Whole(l.Difference),
                ["NCP Days"] = Whole(l.NcpDays),
                ["Refund of Advances"] = "0",
            })).Append('\n');
        }

        return new EcrResult(text.ToString(), lines, problems);
    }

    public static EcrLine Line(EcrMemberMonth m, DateOnly wageMonthStart)
    {
        ArgumentNullException.ThrowIfNull(m);
        var epf = Rupees(m.EpfWages);
        var inEps = m.EpsMember && !TurnedBefore(m.DateOfBirth, EpsAgeLimit, wageMonthStart);
        var eps = inEps ? Math.Min(epf, (long)EpsWageCeiling) : 0;
        var epsContribution = Rupees(eps * EpsRate);
        var employer = Rupees(m.EmployerPf);
        return new EcrLine(
            HrIdentifiers.Normalize(m.Uan!), Name(m.Name), Rupees(m.GrossWages), epf, eps, Math.Min(epf, (long)EdliWageCeiling), Rupees(m.EmployeePf),
            Math.Min(epsContribution, employer), Math.Max(0, employer - epsContribution), Rupees(m.LopDays));
    }

    /// <summary>Whether the member had turned <paramref name="age"/> before the wage month began.</summary>
    public static bool TurnedBefore(DateOnly? dateOfBirth, int age, DateOnly monthStart) => dateOfBirth is { } dob && dob.AddYears(age) < monthStart;

    /// <summary>Upper-case letters, spaces and dots only, as the portal accepts.</summary>
    public static string Name(string name) =>
        string.Join(' ', new string([.. (name ?? string.Empty).ToUpperInvariant().Select(c => char.IsAsciiLetter(c) || c == '.' ? c : ' ')])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static long Rupees(decimal amount) => (long)Math.Round(amount, 0, MidpointRounding.AwayFromZero);

    private static string Whole(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>ESIC's reason codes for an insured person with no working days (or leaving) in the month.</summary>
public enum EsicReason
{
    None = 0,
    OnLeave = 1,
    LeftService = 2,
    Retired = 3,
    OutOfCoverage = 4,
    Expired = 5,
    NonImplementedArea = 6,
    ComplianceByImmediateEmployer = 7,
    SuspensionOfWork = 8,
    StrikeOrLockout = 9,
    Retrenchment = 10,
    NoWork = 11,
    DoesNotBelongToThisEmployer = 12,
    DuplicateIp = 13,
}

public sealed record EsicMemberMonth(string? IpNumber, string EmployeeCode, string Name, decimal PayableDays, decimal EsiWage, decimal EmployeeEsi, decimal EmployerEsi,
    DateOnly? LeavingDate, SeparationReason? SeparationReason);

public sealed record EsicLine(string IpNumber, string Name, int Days, long Wages, EsicReason Reason, DateOnly? LastWorkingDay, decimal EmployeeEsi, decimal EmployerEsi);

public sealed record EsicResult(string Csv, IReadOnlyList<EsicLine> Lines, IReadOnlyList<string> Problems)
{
    public long TotalWages => Lines.Sum(l => l.Wages);
    public decimal TotalEmployee => Lines.Sum(l => l.EmployeeEsi);
    public decimal TotalEmployer => Lines.Sum(l => l.EmployerEsi);
}

/// <summary>
/// The ESIC monthly contribution file: each insured person's days paid and wages. A person with no paid days needs a reason
/// code, and one who left in the month needs the last working day (code 2, or 3 on retirement, 5 on death). ESIC works out
/// the contributions from the wages; HC360's own (0.75 % and 3.25 %, rounded up) are kept alongside to check the challan.
/// </summary>
public static class EsicContributionFile
{
    public static EsicResult Build(IEnumerable<EsicMemberMonth> members, int year, int month)
    {
        ArgumentNullException.ThrowIfNull(members);
        var monthStart = new DateOnly(year, month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var lines = new List<EsicLine>();
        var problems = new List<string>();
        foreach (var m in members)
        {
            if (!HrIdentifiers.IsValidEsiNumber(m.IpNumber))
            {
                problems.Add($"{m.EmployeeCode} {m.Name}: no valid ESIC IP number, so the person cannot be in the file.");
                continue;
            }

            var left = m.LeavingDate is { } lwd && lwd >= monthStart && lwd <= monthEnd ? lwd : (DateOnly?)null;
            var days = (int)Math.Floor(Math.Max(0, m.PayableDays));
            var reason = left is not null ? ReasonForLeaving(m.SeparationReason) : days == 0 ? EsicReason.OnLeave : EsicReason.None;
            lines.Add(new EsicLine(HrIdentifiers.Normalize(m.IpNumber!), EcrFile.Name(m.Name), days, (long)Math.Round(m.EsiWage, 0, MidpointRounding.AwayFromZero),
                reason, left, m.EmployeeEsi, m.EmployerEsi));
        }

        var csv = new StringBuilder();
        var layout = StatutoryLayouts.EsicMonthly;
        csv.Append(string.Join(layout.Separator, layout.Fields.Select(BankTransferFile.Escape))).Append("\r\n");
        foreach (var l in lines)
        {
            csv.Append(layout.Line(new Dictionary<string, string>
            {
                ["IP Number"] = l.IpNumber,
                ["IP Name"] = l.Name,
                ["No of Days for which wages paid/payable during the month"] = l.Days.ToString(CultureInfo.InvariantCulture),
                ["Total Monthly Wages"] = l.Wages.ToString(CultureInfo.InvariantCulture),
                ["Reason Code for Zero workings days"] = ((int)l.Reason).ToString(CultureInfo.InvariantCulture),
                ["Last Working Day"] = l.LastWorkingDay?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
            })).Append("\r\n");
        }

        return new EsicResult(csv.ToString(), lines, problems);
    }

    public static EsicReason ReasonForLeaving(SeparationReason? reason) => reason switch
    {
        SeparationReason.Retirement => EsicReason.Retired,
        SeparationReason.Death => EsicReason.Expired,
        _ => EsicReason.LeftService,
    };
}

/// <summary>
/// ESI contribution periods run April to September and October to March. Someone covered at any time in a period stays
/// covered to its end even when their pay rises above the ceiling (ESI Act s.2(9), proviso; Central Rules r.50).
/// </summary>
public static class EsiContributionPeriod
{
    /// <summary>The first month of the period <paramref name="month"/> falls in.</summary>
    public static (int Year, int Month) Start(int year, int month) => month >= 10 ? (year, 10) : month >= 4 ? (year, 4) : (year - 1, 10);
}
