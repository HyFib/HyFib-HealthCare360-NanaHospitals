using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;

/// <summary>GST of a group of lines (one rate and one HSN/SAC code), as SQL returns it.</summary>
internal sealed record GstGroup(decimal Rate, string? Code, int Lines, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst, decimal Value)
{
    public GstAmounts Amounts => new(Taxable, Cgst, Sgst, Igst);
}

/// <summary>GST totals of the documents themselves (bill and invoice headers), to reconcile the lines to.</summary>
internal sealed record GstHeader(int Documents, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst)
{
    public GstAmounts Amounts => new(Taxable, Cgst, Sgst, Igst);
}

/// <summary>
/// GST summary by rate and HSN/SAC (GSTR-1 table 12 and the rate-wise outward supplies of GSTR-3B): hospital bills and
/// pharmacy invoices, less GST credit notes and pharmacy returns, reconciled to the bills and invoices they come from.
/// </summary>
internal sealed class GstSummaryReport : ReportDefinition
{
    public const string ByRate = "rate";
    public const string ByCode = "hsn";
    public const string SourceKey = "source";
    public const string Hospital = "hospital";
    public const string Pharmacy = "pharmacy";

    public override ReportDescriptor Descriptor { get; } = new(
        "gst-summary",
        "GST summary by rate and HSN/SAC",
        ReportCategory.Statutory,
        "Taxable value and GST (CGST, SGST, IGST) of hospital bills and pharmacy invoices by GST rate and HSN/SAC code, less credit notes and returns, reconciled to the bills.",
        "Invoices = lines of hospital bills finalized in the period (cancelled bills excluded) and of pharmacy invoices dated in the period (cancelled invoices excluded). " +
        "Credit notes = GST credit notes issued in the period, by the lines they correct (write-offs and notes issued after the bill's GST could be reduced carry no GST). " +
        "Returns = pharmacy sales returns in the period, at the returned line's rate and in its proportion of taxable value and GST. " +
        "Net = invoices − credit notes − returns. Reconciliation: the lines are added up again from the bill and invoice totals; any difference is shown in the notes.",
        Audience.Finance,
        [
            P.Period(ReportPeriods.LastMonth),
            P.Facility(),
            P.Choice(ReportKeys.GroupBy, "Show by", [new(ByCode, "GST rate and HSN/SAC"), new(ByRate, "GST rate")], ByCode),
            P.Choice(SourceKey, "Supplies", [new(Hospital, "Hospital bills"), new(Pharmacy, "Pharmacy invoices")]),
        ],
        [
            C.Text("supplies", "Supplies"),
            C.Text("document", "Document"),
            C.Pct("rate", "GST rate"),
            C.Text("code", "HSN / SAC"),
            C.Int("lines", "Lines"),
            C.Money("taxable", "Taxable value"),
            C.Money("cgst", "CGST"),
            C.Money("sgst", "SGST"),
            C.Money("igst", "IGST"),
            C.Money("gst", "Total GST"),
            C.Money("value", "Value incl. GST"),
        ],
        new ReportChartHint(ReportChartType.Bar, "rate", ["taxable", "gst"]),
        DefaultSort: "rate");

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var byCode = ctx.Args.Text(ReportKeys.GroupBy) != ByRate;
        var source = ctx.Args.Text(SourceKey);
        var db = ctx.Db;
        var sections = new List<GstSection>();
        var checks = new List<GstReconciliation>();
        var notes = new List<string>();

        if (source is null or Hospital)
        {
            var bills = RevenueData.FinalizedBills(ctx);
            var billLines = await ctx.ListAsync(
                (from l in db.Set<BillLine>().AsNoTracking()
                 join b in bills on l.BillId equals b.Id
                 select l)
                .GroupBy(l => new { l.TaxRate, l.SacCode })
                .Select(g => new GstGroup(g.Key.TaxRate, g.Key.SacCode, g.Count(), g.Sum(l => l.TaxableAmount), g.Sum(l => l.CgstAmount), g.Sum(l => l.SgstAmount), g.Sum(l => l.IgstAmount), g.Sum(l => l.NetAmount))),
                ct);
            var billHeaders = await ctx.SingleAsync(
                bills.GroupBy(b => 1).Select(g => new GstHeader(g.Count(), g.Sum(b => b.TaxableAmount), g.Sum(b => b.CgstAmount), g.Sum(b => b.SgstAmount), g.Sum(b => b.IgstAmount))),
                ct);

            var notesInPeriod = db.Set<CreditNote>().AsNoTracking()
                .Where(c => c.Kind == CreditNoteKind.CreditNote && c.IssuedAt >= start && c.IssuedAt < end);
            var creditLines = await ctx.ListAsync(
                (from cl in db.Set<CreditNoteLine>().AsNoTracking()
                 join c in notesInPeriod on cl.CreditNoteId equals c.Id
                 select cl)
                .GroupBy(l => new { l.GstRate, l.SacCode })
                .Select(g => new GstGroup(g.Key.GstRate, g.Key.SacCode, g.Count(), g.Sum(l => l.TaxableAmount), g.Sum(l => l.CgstAmount), g.Sum(l => l.SgstAmount), g.Sum(l => l.IgstAmount), g.Sum(l => l.Amount))),
                ct);
            var creditHeaders = await ctx.SingleAsync(
                notesInPeriod.Where(c => db.Set<CreditNoteLine>().Any(l => l.CreditNoteId == c.Id))
                    .GroupBy(c => 1)
                    .Select(g => new GstHeader(
                        g.Count(),
                        g.Sum(c => c.Amount - c.CgstAmount - c.SgstAmount - c.IgstAmount),
                        g.Sum(c => c.CgstAmount),
                        g.Sum(c => c.SgstAmount),
                        g.Sum(c => c.IgstAmount))),
                ct);
            var notesWithoutLines = await ctx.SingleAsync(
                notesInPeriod.Where(c => !db.Set<CreditNoteLine>().Any(l => l.CreditNoteId == c.Id))
                    .GroupBy(c => 1).Select(g => new CountAmount(g.Count(), g.Sum(c => c.Amount))),
                ct);

            sections.Add(new GstSection("Hospital services", "Invoices (bills)", 1, billLines));
            sections.Add(new GstSection("Hospital services", "Credit notes", -1, creditLines));
            checks.Add(new GstReconciliation("Hospital bills", Sum(billLines), billHeaders?.Amounts ?? GstAmounts.Zero));
            checks.Add(new GstReconciliation("GST credit notes", Sum(creditLines), creditHeaders?.Amounts ?? GstAmounts.Zero));
            if (notesWithoutLines is { Count: > 0 })
            {
                notes.Add($"{notesWithoutLines.Count} credit note(s) for ₹{notesWithoutLines.Amount:N2} name no bill lines (issued before lines were kept); they lower what patients owe but not the GST, so they are not in this summary.");
            }
        }

        if (source is null or Pharmacy)
        {
            var sales = db.Set<PharmacySale>().AsNoTracking().Where(s => s.SoldAt >= start && s.SoldAt < end && s.Status != SaleStatus.Cancelled);
            var saleLines = await ctx.ListAsync(
                (from i in db.Set<PharmacySaleItem>().AsNoTracking()
                 join s in sales on i.PharmacySaleId equals s.Id
                 select i)
                .GroupBy(i => new { i.GstRate, i.HsnCode })
                .Select(g => new GstGroup(g.Key.GstRate, g.Key.HsnCode, g.Count(), g.Sum(i => i.TaxableAmount), g.Sum(i => i.CgstAmount), g.Sum(i => i.SgstAmount), g.Sum(i => i.IgstAmount), g.Sum(i => i.NetAmount))),
                ct);
            var saleHeaders = await ctx.SingleAsync(
                sales.GroupBy(s => 1).Select(g => new GstHeader(g.Count(), g.Sum(s => s.TaxableAmount), g.Sum(s => s.CgstAmount), g.Sum(s => s.SgstAmount), g.Sum(s => s.IgstAmount))),
                ct);
            var returnLines = await ctx.ListAsync(
                (from ri in db.Set<PharmacySaleReturnItem>().AsNoTracking()
                 join r in db.Set<PharmacySaleReturn>().AsNoTracking() on ri.SaleReturnId equals r.Id
                 join i in db.Set<PharmacySaleItem>().AsNoTracking() on ri.SaleItemId equals i.Id
                 where r.ReturnedAt >= start && r.ReturnedAt < end
                 select new { i.GstRate, i.HsnCode, ri.RefundAmount, i.NetAmount, i.TaxableAmount, i.CgstAmount, i.SgstAmount, i.IgstAmount })
                .GroupBy(x => new { x.GstRate, x.HsnCode })
                .Select(g => new GstGroup(
                    g.Key.GstRate,
                    g.Key.HsnCode,
                    g.Count(),
                    g.Sum(x => x.NetAmount == 0m ? 0m : x.RefundAmount * x.TaxableAmount / x.NetAmount),
                    g.Sum(x => x.NetAmount == 0m ? 0m : x.RefundAmount * x.CgstAmount / x.NetAmount),
                    g.Sum(x => x.NetAmount == 0m ? 0m : x.RefundAmount * x.SgstAmount / x.NetAmount),
                    g.Sum(x => x.NetAmount == 0m ? 0m : x.RefundAmount * x.IgstAmount / x.NetAmount),
                    g.Sum(x => x.RefundAmount))),
                ct);

            sections.Add(new GstSection("Pharmacy", "Invoices", 1, saleLines));
            sections.Add(new GstSection("Pharmacy", "Returns", -1, returnLines.Select(Round).ToList()));
            checks.Add(new GstReconciliation("Pharmacy invoices", Sum(saleLines), saleHeaders?.Amounts ?? GstAmounts.Zero));
        }

        var rows = GstSummary.Rows(sections, byCode);
        var net = sections.Aggregate(GstAmounts.Zero, (total, s) => total.Plus(s.Sign > 0 ? Sum(s.Groups) : Sum(s.Groups).Negate()));
        var outward = sections.Where(s => s.Sign > 0).Aggregate(GstAmounts.Zero, (total, s) => total.Plus(Sum(s.Groups)));
        var reduced = sections.Where(s => s.Sign < 0).Aggregate(GstAmounts.Zero, (total, s) => total.Plus(Sum(s.Groups)));
        var netValue = sections.Sum(s => s.Sign * s.Groups.Sum(g => g.Value));

        foreach (var check in checks)
        {
            notes.Add(check.Reconciles
                ? $"{check.Source}: the lines add up to the documents' totals (taxable value ₹{check.Headers.Taxable:N2}, GST ₹{check.Headers.Gst:N2})."
                : $"{check.Source}: the lines differ from the documents' totals by ₹{check.TaxableDifference:N2} taxable value and ₹{check.GstDifference:N2} GST; check the bills of the period before filing.");
        }

        notes.Add("Values incl. GST are the lines' net amounts; a bill's round-off is not part of any line.");
        return new ReportData(rows.Select(r => r.Cells).ToList())
        {
            Totals = ["Net", null, null, null, rows.Sum(r => r.Lines), net.Taxable, net.Cgst, net.Sgst, net.Igst, net.Gst, netValue],
            Summary =
            [
                new("taxable", "Net taxable value", net.Taxable, ReportValueType.Money),
                new("gst", "Net GST", net.Gst, ReportValueType.Money),
                new("outward", "GST on invoices", outward.Gst, ReportValueType.Money),
                new("reduced", "GST reduced by credit notes and returns", reduced.Gst, ReportValueType.Money),
                new("reconciled", "Reconciles to bills", checks.All(c => c.Reconciles) ? "Yes" : "No — see notes", ReportValueType.Text),
            ],
            Notes = notes,
        };
    }

    private static GstAmounts Sum(IEnumerable<GstGroup> groups) => groups.Aggregate(GstAmounts.Zero, (total, g) => total.Plus(g.Amounts));

    private static GstGroup Round(GstGroup g) => g with
    {
        Taxable = Figures.Round2(g.Taxable),
        Cgst = Figures.Round2(g.Cgst),
        Sgst = Figures.Round2(g.Sgst),
        Igst = Figures.Round2(g.Igst),
    };

    private sealed record CountAmount(int Count, decimal Amount);
}

/// <summary>The lines of one kind of document: invoices add (+1), credit notes and returns take away (−1).</summary>
internal sealed record GstSection(string Supplies, string Document, int Sign, IReadOnlyList<GstGroup> Groups);

/// <summary>A row of the GST summary, with its cells in column order.</summary>
internal sealed record GstRow(object?[] Cells, int Lines);

/// <summary>Builds the GST summary's rows from its sections (pure, unit-tested).</summary>
internal static class GstSummary
{
    public const string NoCode = "Not recorded";

    /// <summary>
    /// One row per section, rate and (when <paramref name="byCode"/>) HSN/SAC code; credit notes and returns are shown as
    /// negative amounts so every column adds up to the net.
    /// </summary>
    public static IReadOnlyList<GstRow> Rows(IEnumerable<GstSection> sections, bool byCode)
    {
        var rows = new List<GstRow>();
        foreach (var section in sections)
        {
            var groups = section.Groups
                .GroupBy(g => (g.Rate, Code: byCode ? Clean(g.Code) : null))
                .OrderBy(g => g.Key.Rate).ThenBy(g => g.Key.Code, StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var taxable = group.Sum(g => g.Taxable) * section.Sign;
                var cgst = group.Sum(g => g.Cgst) * section.Sign;
                var sgst = group.Sum(g => g.Sgst) * section.Sign;
                var igst = group.Sum(g => g.Igst) * section.Sign;
                var lines = group.Sum(g => g.Lines);
                rows.Add(new GstRow(
                    [section.Supplies, section.Document, group.Key.Rate, byCode ? group.Key.Code : "All", lines, taxable, cgst, sgst, igst, cgst + sgst + igst, group.Sum(g => g.Value) * section.Sign],
                    lines));
            }
        }

        return rows;
    }

    private static string Clean(string? code) => string.IsNullOrWhiteSpace(code) ? NoCode : code.Trim();
}

/// <summary>TDS deducted in a period by section and deductee kind.</summary>
internal sealed record TdsLine(string Section, string Nature, Guid? DeducteeId, string? DeducteeName, int Payments, decimal Amount, decimal Tds);

/// <summary>
/// TDS deducted by section (Form 26Q / 24Q): professional fees paid to doctors (194J) and salaries (192). Vendor payments
/// are not recorded in HC360 yet, so the report says so rather than showing nothing under their sections.
/// </summary>
internal sealed class TdsSummaryReport : ReportDefinition
{
    public const string BySection = "section";
    public const string ByDeductee = "deductee";
    public const string Section194J = "194J";
    public const string Section192 = "192";

    public override ReportDescriptor Descriptor { get; } = new(
        "tds-summary",
        "TDS deducted by section",
        ReportCategory.Statutory,
        "Tax deducted at source on doctors' professional fees (section 194J) and salaries (section 192), by section or by deductee.",
        "194J = doctor payouts approved in the period (the date the fee is credited to the doctor), draft payouts excluded: amount = gross share less recoveries, TDS as deducted on the payout. " +
        "192 = payslips of payroll months in the period whose payroll is approved, paid or locked: amount = gross earnings, TDS as deducted on the payslip. " +
        "Effective rate = TDS ÷ amount × 100. Vendor payments are not recorded in HC360, so TDS on them is not in this report.",
        Audience.Finance,
        [
            P.Period(ReportPeriods.LastMonth),
            P.Facility(),
            P.Choice(ReportKeys.GroupBy, "Show by", [new(BySection, "Section"), new(ByDeductee, "Deductee")], BySection),
        ],
        [
            C.Text("section", "Section"),
            C.Text("nature", "Nature of payment"),
            C.Text("deductee", "Deductee"),
            C.Int("deductees", "Deductees"),
            C.Int("payments", "Payments"),
            C.Money("amount", "Amount paid or credited"),
            C.Money("tds", "TDS deducted"),
            C.Pct("rate", "Effective rate"),
        ],
        new ReportChartHint(ReportChartType.Bar, "section", ["tds"]),
        DefaultSort: "section");

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var db = ctx.Db;
        var byDeductee = ctx.Args.Text(ReportKeys.GroupBy) == ByDeductee;

        var payouts = await ctx.ListAsync(
            db.Set<DoctorPayout>().AsNoTracking()
                .Where(p => p.Status != PayoutStatus.Draft && p.ApprovedAt >= start && p.ApprovedAt < end)
                .GroupBy(p => p.PractitionerId)
                .Select(g => new DeducteeTotal(g.Key, null, g.Count(), g.Sum(p => p.GrossAmount - p.DeductionAmount), g.Sum(p => p.TdsAmount))),
            ct);

        var (firstMonth, lastMonth) = (StatutoryMath.MonthNumber(ctx.Args.From.Year, ctx.Args.From.Month), StatutoryMath.MonthNumber(ctx.Args.To.Year, ctx.Args.To.Month));
        var payslips = await ctx.ListAsync(
            (from s in db.Set<Payslip>().AsNoTracking()
             join r in db.Set<PayrollRun>().AsNoTracking() on s.PayrollRunId equals r.Id
             where (r.Status == PayrollRunStatus.Approved || r.Status == PayrollRunStatus.Paid || r.Status == PayrollRunStatus.Locked)
                   && (s.Year * 12) + s.Month - 1 >= firstMonth && (s.Year * 12) + s.Month - 1 <= lastMonth
             select s)
            .GroupBy(s => new { s.EmployeeId, s.EmployeeName })
            .Select(g => new DeducteeTotal(g.Key.EmployeeId, g.Key.EmployeeName, g.Count(), g.Sum(s => s.GrossEarnings), g.Sum(s => s.Tds))),
            ct);

        var doctors = await Names.PractitionersAsync(ctx, payouts.Select(p => (Guid?)p.Id), ct);
        var lines = new List<TdsLine>();
        lines.AddRange(payouts.Select(p => new TdsLine(Section194J, "Fees for professional services (doctors)", p.Id, Labels.Name(doctors, p.Id), p.Payments, p.Amount, p.Tds)));
        lines.AddRange(payslips.Select(p => new TdsLine(Section192, "Salaries", p.Id, p.Name, p.Payments, p.Amount, p.Tds)));

        var rows = TdsSummary.Rows(lines, byDeductee);
        var totalAmount = lines.Sum(l => l.Amount);
        var totalTds = lines.Sum(l => l.Tds);
        return new ReportData(rows)
        {
            Totals = ["Total", null, null, lines.Select(l => (l.Section, l.DeducteeId)).Distinct().Count(), lines.Sum(l => l.Payments), totalAmount, totalTds, StatutoryMath.EffectiveRate(totalTds, totalAmount)],
            Summary =
            [
                new("tds", "TDS deducted", totalTds, ReportValueType.Money),
                new("tds194j", "On doctors' fees (194J)", lines.Where(l => l.Section == Section194J).Sum(l => l.Tds), ReportValueType.Money),
                new("tds192", "On salaries (192)", lines.Where(l => l.Section == Section192).Sum(l => l.Tds), ReportValueType.Money),
            ],
            Notes =
            [
                "Vendor payments (sections 194C, 194I, 194Q and others) are not recorded in HC360, so TDS on them is deducted and reported from the accounts.",
                "Deposit the TDS by the 7th of the next month (30 April for March) and file Form 26Q (194J) and Form 24Q (192) quarterly.",
            ],
        };
    }

    private sealed record DeducteeTotal(Guid Id, string? Name, int Payments, decimal Amount, decimal Tds);
}

/// <summary>Builds the TDS report's rows (pure, unit-tested).</summary>
internal static class TdsSummary
{
    /// <summary>One row per section, or per section and deductee; sections in their number order (192 before 194J).</summary>
    public static List<object?[]> Rows(IReadOnlyList<TdsLine> lines, bool byDeductee)
    {
        if (byDeductee)
        {
            return lines
                .OrderBy(l => l.Section, StringComparer.Ordinal).ThenBy(l => l.DeducteeName, StringComparer.OrdinalIgnoreCase)
                .Select(l => new object?[] { l.Section, l.Nature, l.DeducteeName, 1, l.Payments, l.Amount, l.Tds, StatutoryMath.EffectiveRate(l.Tds, l.Amount) })
                .ToList();
        }

        return lines
            .GroupBy(l => (l.Section, l.Nature))
            .OrderBy(g => g.Key.Section, StringComparer.Ordinal)
            .Select(g =>
            {
                var amount = g.Sum(l => l.Amount);
                var tds = g.Sum(l => l.Tds);
                return new object?[] { g.Key.Section, g.Key.Nature, null, g.Select(l => l.DeducteeId).Distinct().Count(), g.Sum(l => l.Payments), amount, tds, StatutoryMath.EffectiveRate(tds, amount) };
            })
            .ToList();
    }
}

/// <summary>One group of the payroll cost (a department, a cost centre or a component).</summary>
internal sealed record PayrollCostGroup(string Label, int Employees, decimal Earnings, decimal EmployerPf, decimal EmployerEsi)
{
    public decimal Cost => StatutoryMath.EmployerCost(Earnings, EmployerPf, EmployerEsi);
}

/// <summary>
/// Payroll cost (what employees cost the hospital: gross earnings plus the employer's PF and ESI) by department, cost
/// centre or salary component, for the payroll months in the period.
/// </summary>
internal sealed class PayrollCostReport : ReportDefinition
{
    public const string ByDepartment = "department";
    public const string ByCostCentre = "costCentre";
    public const string ByComponent = "component";

    public override ReportDescriptor Descriptor { get; } = new(
        "payroll-cost",
        "Payroll cost by department, cost centre and component",
        ReportCategory.Statutory,
        "What staff cost the hospital (gross earnings plus the employer's PF and ESI) by department, cost centre or salary component.",
        "Payslips of the payroll months in the period whose payroll is approved, paid or locked (draft and processed payrolls are excluded). " +
        "Cost = gross earnings + employer PF + employer ESI. Department = the department on the payslip. " +
        "Cost centre = the kind of the employee's department (clinical, diagnostic, support, administrative); HC360 has no separate cost-centre master. " +
        "By component, each earning component is a row and the employer's PF and ESI are rows of their own; employees = payslips with that component.",
        Audience.Finance,
        [
            P.Period(ReportPeriods.LastMonth),
            P.Facility(),
            P.Choice(ReportKeys.GroupBy, "Show by", [new(ByDepartment, "Department"), new(ByCostCentre, "Cost centre"), new(ByComponent, "Salary component")], ByDepartment),
        ],
        [
            C.Text("group", "Department"),
            C.Int("employees", "Employees"),
            C.Money("earnings", "Gross earnings"),
            C.Money("pf", "Employer PF"),
            C.Money("esi", "Employer ESI"),
            C.Money("cost", "Cost to the hospital"),
            C.Pct("share", "Share %"),
        ],
        new ReportChartHint(ReportChartType.Bar, "group", ["cost"]),
        DefaultSort: "cost",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var db = ctx.Db;
        var groupBy = ctx.Args.Text(ReportKeys.GroupBy) ?? ByDepartment;
        var (firstMonth, lastMonth) = (StatutoryMath.MonthNumber(ctx.Args.From.Year, ctx.Args.From.Month), StatutoryMath.MonthNumber(ctx.Args.To.Year, ctx.Args.To.Month));
        var payslips =
            from s in db.Set<Payslip>().AsNoTracking()
            join r in db.Set<PayrollRun>().AsNoTracking() on s.PayrollRunId equals r.Id
            where (r.Status == PayrollRunStatus.Approved || r.Status == PayrollRunStatus.Paid || r.Status == PayrollRunStatus.Locked)
                  && (s.Year * 12) + s.Month - 1 >= firstMonth && (s.Year * 12) + s.Month - 1 <= lastMonth
            select s;

        List<PayrollCostGroup> groups;
        string column;
        if (groupBy == ByComponent)
        {
            column = "Salary component";
            var components = await ctx.ListAsync(
                (from l in db.Set<PayslipLine>().AsNoTracking()
                 join s in payslips on l.PayslipId equals s.Id
                 where l.Kind == SalaryComponentKind.Earning
                 select l)
                .GroupBy(l => new { l.Code, l.Name })
                .Select(g => new ComponentTotal(g.Key.Code, g.Key.Name, g.Count(), g.Sum(l => l.Amount))),
                ct);
            var employer = await ctx.SingleAsync(
                payslips.GroupBy(s => 1).Select(g => new EmployerTotal(g.Count(s => s.EmployerPf > 0), g.Sum(s => s.EmployerPf), g.Count(s => s.EmployerEsi > 0), g.Sum(s => s.EmployerEsi))),
                ct);
            groups = PayrollCost.ByComponent(
                components.Select(c => (c.Name + " (" + c.Code + ")", c.Payslips, c.Amount)).ToList(),
                employer?.PfPayslips ?? 0, employer?.Pf ?? 0m, employer?.EsiPayslips ?? 0, employer?.Esi ?? 0m);
        }
        else if (groupBy == ByCostCentre)
        {
            column = "Cost centre";
            var kinds = await ctx.ListAsync(
                (from s in payslips
                 join e in db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]) on s.EmployeeId equals e.Id
                 join d in db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]) on e.DepartmentId equals d.Id into departments
                 from d in departments.DefaultIfEmpty()
                 select new { Kind = d == null ? (DepartmentKind?)null : d.Kind, s.EmployeeId, s.GrossEarnings, s.EmployerPf, s.EmployerEsi })
                .GroupBy(x => x.Kind)
                .Select(g => new KindTotal(g.Key, g.Select(x => x.EmployeeId).Distinct().Count(), g.Sum(x => x.GrossEarnings), g.Sum(x => x.EmployerPf), g.Sum(x => x.EmployerEsi))),
                ct);
            groups = kinds.Select(k => new PayrollCostGroup(PayrollCost.CostCentre(k.Kind), k.Employees, k.Earnings, k.Pf, k.Esi)).ToList();
        }
        else
        {
            column = "Department";
            var departments = await ctx.ListAsync(
                payslips.GroupBy(s => s.DepartmentName)
                    .Select(g => new NameTotal(g.Key, g.Select(s => s.EmployeeId).Distinct().Count(), g.Sum(s => s.GrossEarnings), g.Sum(s => s.EmployerPf), g.Sum(s => s.EmployerEsi))),
                ct);
            groups = departments.Select(d => new PayrollCostGroup(string.IsNullOrWhiteSpace(d.Name) ? "Not assigned" : d.Name, d.Employees, d.Earnings, d.Pf, d.Esi)).ToList();
        }

        var totals = await ctx.SingleAsync(
            payslips.GroupBy(s => 1).Select(g => new NameTotal(null, g.Select(s => s.EmployeeId).Distinct().Count(), g.Sum(s => s.GrossEarnings), g.Sum(s => s.EmployerPf), g.Sum(s => s.EmployerEsi))),
            ct);
        var totalCost = StatutoryMath.EmployerCost(totals?.Earnings ?? 0m, totals?.Pf ?? 0m, totals?.Esi ?? 0m);
        var columns = Descriptor.Columns.Select((c, i) => i == 0 ? c with { Label = column } : c).ToList();
        return new ReportData(groups.Select(g => new object?[] { g.Label, g.Employees, g.Earnings, g.EmployerPf, g.EmployerEsi, g.Cost, Figures.Share(g.Cost, totalCost) }).ToList())
        {
            Columns = columns,
            Totals = ["Total", groupBy == ByComponent ? null : totals?.Employees ?? 0, totals?.Earnings ?? 0m, totals?.Pf ?? 0m, totals?.Esi ?? 0m, totalCost, totalCost == 0m ? null : 100m],
            Summary =
            [
                new("cost", "Cost to the hospital", totalCost, ReportValueType.Money),
                new("earnings", "Gross earnings", totals?.Earnings ?? 0m, ReportValueType.Money),
                new("employer", "Employer PF and ESI", (totals?.Pf ?? 0m) + (totals?.Esi ?? 0m), ReportValueType.Money),
                new("employees", "Employees paid", totals?.Employees ?? 0, ReportValueType.Integer),
            ],
            Notes = groupBy == ByCostCentre ? ["Cost centres are the kinds of departments; an employee with no department is under \"Not assigned\"."] : [],
        };
    }

    private sealed record ComponentTotal(string Code, string Name, int Payslips, decimal Amount);

    private sealed record EmployerTotal(int PfPayslips, decimal Pf, int EsiPayslips, decimal Esi);

    private sealed record KindTotal(DepartmentKind? Kind, int Employees, decimal Earnings, decimal Pf, decimal Esi);

    private sealed record NameTotal(string? Name, int Employees, decimal Earnings, decimal Pf, decimal Esi);
}

/// <summary>Payroll cost grouping (pure, unit-tested).</summary>
internal static class PayrollCost
{
    public static string CostCentre(DepartmentKind? kind) => kind switch
    {
        DepartmentKind.Clinical => "Clinical (patient care)",
        DepartmentKind.Diagnostic => "Diagnostics",
        DepartmentKind.Support => "Support services",
        DepartmentKind.Administrative => "Administration",
        _ => "Not assigned",
    };

    /// <summary>Earning components as they are, then the employer's PF and ESI as rows of their own (when any was paid).</summary>
    public static List<PayrollCostGroup> ByComponent(IReadOnlyList<(string Label, int Payslips, decimal Amount)> earnings, int pfPayslips, decimal pf, int esiPayslips, decimal esi)
    {
        var groups = earnings.Select(e => new PayrollCostGroup(e.Label, e.Payslips, e.Amount, 0m, 0m)).ToList();
        if (pf != 0m)
        {
            groups.Add(new PayrollCostGroup("Employer PF contribution", pfPayslips, 0m, pf, 0m));
        }

        if (esi != 0m)
        {
            groups.Add(new PayrollCostGroup("Employer ESI contribution", esiPayslips, 0m, 0m, esi));
        }

        return groups;
    }
}

/// <summary>The insurance claims reports' shared inputs and queries.</summary>
internal static class ClaimsData
{
    public const string Definition =
        "Claims are counted by the date they were submitted to the insurer or TPA. Settled = fully settled; rejected = rejected; open = submitted, under query, approved or partly settled. ";

    /// <summary>Claims submitted in the period, optionally for one insurer.</summary>
    public static IQueryable<InsuranceClaim> SubmittedInPeriod(ReportContext ctx)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var claims = ctx.Db.Set<InsuranceClaim>().AsNoTracking().Where(c => c.SubmittedAt >= start && c.SubmittedAt < end);
        if (ctx.Args.PayerId is { } payerId)
        {
            claims = claims.Where(c => c.PayerId == payerId);
        }

        return claims;
    }
}

/// <summary>Open insurance claims by insurer and age (days since submission), as of today (INS-07).</summary>
internal sealed class ClaimsAgeingReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "claims-ageing",
        "Insurance claims ageing",
        ReportCategory.Statutory,
        "What insurers and TPAs still owe on open claims today, by insurer and by days since the claim was submitted.",
        "Open claims = submitted, under query, approved or partly settled. Outstanding = claimed − settled − deducted (never below nothing). " +
        "Age = days from submission (IST) to today, in 0–30, 31–60, 61–90, 91–180 and over 180 days.",
        Audience.Finance,
        [P.Facility(), P.Payer()],
        [
            C.Text("insurer", "Insurer / TPA"),
            C.Int("claims", "Open claims"),
            C.Money("d0", "0–30 days"),
            C.Money("d31", "31–60 days"),
            C.Money("d61", "61–90 days"),
            C.Money("d91", "91–180 days"),
            C.Money("d181", "Over 180 days"),
            C.Money("total", "Outstanding"),
            C.Days("oldest", "Oldest (days)"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "insurer", ["d0", "d31", "d61", "d91", "d181"]),
        DefaultSort: "total",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var today = ctx.Today.DayNumber;
        var claims = ctx.Db.Set<InsuranceClaim>().AsNoTracking()
            .Where(c => c.Status == ClaimStatus.Submitted || c.Status == ClaimStatus.Query || c.Status == ClaimStatus.Approved || c.Status == ClaimStatus.PartiallySettled);
        if (ctx.Args.PayerId is { } payerId)
        {
            claims = claims.Where(c => c.PayerId == payerId);
        }

        var open = await ctx.ListAsync(
            claims.Select(c => new
                {
                    c.PayerId,
                    Due = c.ClaimedAmount - c.SettledAmount - c.DeductionAmount,
                    Age = today - DateOnly.FromDateTime(c.SubmittedAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber,
                })
                .GroupBy(x => x.PayerId)
                .Select(g => new ClaimAgeing(
                    g.Key,
                    g.Count(),
                    g.Sum(x => x.Age <= 30 && x.Due > 0 ? x.Due : 0m),
                    g.Sum(x => x.Age > 30 && x.Age <= 60 && x.Due > 0 ? x.Due : 0m),
                    g.Sum(x => x.Age > 60 && x.Age <= 90 && x.Due > 0 ? x.Due : 0m),
                    g.Sum(x => x.Age > 90 && x.Age <= 180 && x.Due > 0 ? x.Due : 0m),
                    g.Sum(x => x.Age > 180 && x.Due > 0 ? x.Due : 0m),
                    g.Max(x => x.Age))),
            ct);

        var names = await Names.PayersAsync(ctx, open.Select(o => (Guid?)o.PayerId), ct);
        var rows = open.Select(o => new object?[] { Labels.Name(names, o.PayerId), o.Claims, o.D0, o.D31, o.D61, o.D91, o.D181, o.Total, (decimal)o.Oldest }).ToList();
        var total = open.Sum(o => o.Total);
        return new ReportData(rows)
        {
            RowKeys = open.Select(o => o.PayerId.ToString()).ToList(),
            DrillDown = new ReportDrillDown("claims-settlement", ReportKeys.Payer, "See this insurer's settlements"),
            Totals = ["Total", open.Sum(o => o.Claims), open.Sum(o => o.D0), open.Sum(o => o.D31), open.Sum(o => o.D61), open.Sum(o => o.D91), open.Sum(o => o.D181), total, open.Count == 0 ? null : (decimal)open.Max(o => o.Oldest)],
            Summary =
            [
                new("total", "Outstanding on open claims", total, ReportValueType.Money),
                new("claims", "Open claims", open.Sum(o => o.Claims), ReportValueType.Integer),
                new("over90", "Older than 90 days", open.Sum(o => o.D91 + o.D181), ReportValueType.Money),
            ],
            Notes = ["Amounts are balances as of now; the age is counted from the date each claim was submitted."],
        };
    }

    private sealed record ClaimAgeing(Guid PayerId, int Claims, decimal D0, decimal D31, decimal D61, decimal D91, decimal D181, int Oldest)
    {
        public decimal Total => D0 + D31 + D61 + D91 + D181;
    }
}

/// <summary>Claim settlement ratio, deductions and rejections by insurer, for the claims submitted in the period.</summary>
internal sealed class ClaimsSettlementReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "claims-settlement",
        "Insurance claim settlement ratio by insurer",
        ReportCategory.Statutory,
        "For the claims submitted in the period: how many each insurer settled, rejected or left open, what it paid and deducted, and its settlement ratio.",
        ClaimsData.Definition +
        "Settlement ratio (claims) = settled ÷ (settled + rejected) × 100. Settlement ratio (amount) = amount settled ÷ amount claimed on settled and rejected claims × 100. " +
        "Deductions = itemised deductions recorded on the claims' settlements. Days to settle = average days from submission to final settlement.",
        Audience.Finance,
        [P.Period(ReportPeriods.ThisFinancialYear), P.Facility(), P.Payer()],
        [
            C.Text("insurer", "Insurer / TPA"),
            C.Int("claims", "Claims"),
            C.Money("claimed", "Claimed"),
            C.Int("settled", "Settled"),
            C.Int("rejected", "Rejected"),
            C.Int("open", "Open"),
            C.Money("paid", "Amount settled"),
            C.Money("deducted", "Deductions"),
            C.Money("rejectedAmount", "Claimed on rejected"),
            C.Pct("ratioCount", "Settlement ratio (claims)"),
            C.Pct("ratioAmount", "Settlement ratio (amount)"),
            C.Days("days", "Days to settle"),
        ],
        new ReportChartHint(ReportChartType.Bar, "insurer", ["ratioCount", "ratioAmount"]),
        DefaultSort: "claimed",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var groups = await ctx.ListAsync(
            ClaimsData.SubmittedInPeriod(ctx)
                .GroupBy(c => c.PayerId)
                .Select(g => new InsurerClaims(
                    g.Key,
                    g.Count(),
                    g.Sum(c => c.ClaimedAmount),
                    g.Count(c => c.Status == ClaimStatus.Settled),
                    g.Count(c => c.Status == ClaimStatus.Rejected),
                    g.Sum(c => c.Status == ClaimStatus.Settled ? c.ClaimedAmount : 0m),
                    g.Sum(c => c.SettledAmount),
                    g.Sum(c => c.DeductionAmount),
                    g.Sum(c => c.Status == ClaimStatus.Rejected ? c.ClaimedAmount : 0m),
                    g.Sum(c => c.Status == ClaimStatus.Settled ? c.SettledAmount : 0m),
                    g.Sum(c => c.Status == ClaimStatus.Settled && c.SettledAt != null
                        ? DateOnly.FromDateTime(c.SettledAt.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber
                          - DateOnly.FromDateTime(c.SubmittedAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber
                        : 0))),
            ct);

        var names = await Names.PayersAsync(ctx, groups.Select(g => (Guid?)g.PayerId), ct);
        var rows = groups.Select(g => Row(Labels.Name(names, g.PayerId), g)).ToList();
        var total = new InsurerClaims(
            Guid.Empty, groups.Sum(g => g.Claims), groups.Sum(g => g.Claimed), groups.Sum(g => g.Settled), groups.Sum(g => g.Rejected),
            groups.Sum(g => g.SettledClaimed), groups.Sum(g => g.Paid), groups.Sum(g => g.Deducted), groups.Sum(g => g.RejectedClaimed), groups.Sum(g => g.SettledPaid),
            groups.Sum(g => g.SettleDays));
        return new ReportData(rows)
        {
            RowKeys = groups.Select(g => g.PayerId.ToString()).ToList(),
            DrillDown = new ReportDrillDown("claims-deductions", ReportKeys.Payer, "See deductions and rejections"),
            Totals = Row("Total", total),
            Summary =
            [
                new("ratioCount", "Settlement ratio (claims)", StatutoryMath.SettlementRatioByCount(total.Outcomes), ReportValueType.Percent),
                new("ratioAmount", "Settlement ratio (amount)", StatutoryMath.SettlementRatioByAmount(total.Outcomes), ReportValueType.Percent),
                new("deducted", "Deductions", total.Deducted, ReportValueType.Money),
                new("rejected", "Claims rejected", total.Rejected, ReportValueType.Integer),
            ],
            Notes = ["Ratios count only decided claims (settled or rejected); open claims are left out until they are decided."],
        };
    }

    private static object?[] Row(string label, InsurerClaims g) =>
    [
        label, g.Claims, g.Claimed, g.Settled, g.Rejected, g.Claims - g.Settled - g.Rejected, g.Paid, g.Deducted, g.RejectedClaimed,
        StatutoryMath.SettlementRatioByCount(g.Outcomes), StatutoryMath.SettlementRatioByAmount(g.Outcomes),
        g.Settled == 0 ? null : Math.Round((decimal)g.SettleDays / g.Settled, 1, MidpointRounding.AwayFromZero),
    ];

    private sealed record InsurerClaims(
        Guid PayerId, int Claims, decimal Claimed, int Settled, int Rejected, decimal SettledClaimed, decimal Paid, decimal Deducted, decimal RejectedClaimed,
        decimal SettledPaid, int SettleDays)
    {
        /// <summary>The ratio counts money settled on settled claims only: part-payments of open claims wait for the decision.</summary>
        public ClaimOutcomes Outcomes => new(Settled, Rejected, Claims - Settled - Rejected, SettledClaimed, SettledPaid, RejectedClaimed);
    }
}

/// <summary>Claim deductions and rejections by insurer and reason, for the claims submitted in the period.</summary>
internal sealed class ClaimsDeductionsReport : ReportDefinition
{
    public const string ByReason = "reason";
    public const string ByInsurer = "insurer";

    public override ReportDescriptor Descriptor { get; } = new(
        "claims-deductions",
        "Insurance claim deductions and rejections by reason",
        ReportCategory.Statutory,
        "Why insurers paid less than claimed: itemised deductions and rejections by reason, for all insurers or each one.",
        ClaimsData.Definition +
        "Deductions = itemised deductions recorded on the claims' settlements, by the reason the insurer gave. Rejections = rejected claims by the rejection reason, at the amount claimed. " +
        "Reasons are grouped regardless of capitals and spaces. Share = the amount ÷ all deductions and rejections × 100.",
        Audience.Finance,
        [
            P.Period(ReportPeriods.ThisFinancialYear),
            P.Facility(),
            P.Payer(),
            P.Choice(ReportKeys.GroupBy, "Show by", [new(ByReason, "Reason"), new(ByInsurer, "Insurer and reason")], ByReason),
        ],
        [
            C.Text("insurer", "Insurer / TPA"),
            C.Text("kind", "Type"),
            C.Text("reason", "Reason"),
            C.Int("claims", "Claims"),
            C.Money("amount", "Amount"),
            C.Pct("share", "Share %"),
        ],
        new ReportChartHint(ReportChartType.Bar, "reason", ["amount"]),
        DefaultSort: "amount",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var claims = ClaimsData.SubmittedInPeriod(ctx);
        var deductions = await ctx.ListAsync(
            (from d in ctx.Db.Set<ClaimDeduction>().AsNoTracking()
             join c in claims on d.ClaimId equals c.Id
             select new { c.PayerId, d.Reason, d.ClaimId, d.Amount })
            .GroupBy(x => new { x.PayerId, x.Reason })
            .Select(g => new ReasonTotal(g.Key.PayerId, ClaimReasons.Deduction, g.Key.Reason, g.Select(x => x.ClaimId).Distinct().Count(), g.Sum(x => x.Amount))),
            ct);
        var rejections = await ctx.ListAsync(
            claims.Where(c => c.Status == ClaimStatus.Rejected)
                .GroupBy(c => new { c.PayerId, c.RejectionReason })
                .Select(g => new ReasonTotal(g.Key.PayerId, ClaimReasons.Rejection, g.Key.RejectionReason, g.Count(), g.Sum(c => c.ClaimedAmount))),
            ct);

        var byInsurer = ctx.Args.Text(ReportKeys.GroupBy) == ByInsurer;
        var names = await Names.PayersAsync(ctx, deductions.Concat(rejections).Select(r => (Guid?)r.PayerId), ct);
        var lines = ClaimReasons.Group(deductions.Concat(rejections).ToList(), byInsurer, id => Labels.Name(names, id));
        var total = lines.Sum(l => l.Amount);
        return new ReportData(lines.Select(l => new object?[] { l.Insurer, l.Kind, l.Reason, l.Claims, l.Amount, Figures.Share(l.Amount, total) }).ToList())
        {
            Totals = ["Total", null, null, null, total, total == 0m ? null : 100m],
            Summary =
            [
                new("deducted", "Deductions", lines.Where(l => l.Kind == ClaimReasons.Deduction).Sum(l => l.Amount), ReportValueType.Money),
                new("rejected", "Claimed on rejected claims", lines.Where(l => l.Kind == ClaimReasons.Rejection).Sum(l => l.Amount), ReportValueType.Money),
                new("topReason", "Largest reason", lines.OrderByDescending(l => l.Amount).FirstOrDefault()?.Reason ?? "None", ReportValueType.Text),
            ],
        };
    }
}

/// <summary>A deduction or rejection reason of one insurer, as SQL returns it.</summary>
internal sealed record ReasonTotal(Guid PayerId, string Kind, string? Reason, int Claims, decimal Amount);

/// <summary>A row of the deductions report.</summary>
internal sealed record ReasonLine(string Insurer, string Kind, string Reason, int Claims, decimal Amount);

/// <summary>Groups deduction and rejection reasons (pure, unit-tested).</summary>
internal static class ClaimReasons
{
    public const string Deduction = "Deduction";
    public const string Rejection = "Rejection";
    public const string AllInsurers = "All insurers";
    public const string NoReason = "No reason recorded";

    /// <summary>
    /// Reasons are the insurer's own words, typed at settlement: grouped ignoring capitals and runs of spaces, shown in the
    /// spelling used most (then the first in order). Without <paramref name="byInsurer"/> all insurers are added together.
    /// </summary>
    public static List<ReasonLine> Group(IReadOnlyList<ReasonTotal> totals, bool byInsurer, Func<Guid, string> insurerName) =>
        totals
            .GroupBy(t => (Payer: byInsurer ? t.PayerId : Guid.Empty, t.Kind, Key: Normalise(t.Reason)))
            .Select(g => new ReasonLine(
                byInsurer ? insurerName(g.Key.Payer) : AllInsurers,
                g.Key.Kind,
                g.GroupBy(t => Display(t.Reason)).OrderByDescending(s => s.Sum(t => t.Claims)).ThenBy(s => s.Key, StringComparer.Ordinal).First().Key,
                g.Sum(t => t.Claims),
                g.Sum(t => t.Amount)))
            .OrderByDescending(l => l.Amount).ThenBy(l => l.Reason, StringComparer.Ordinal)
            .ToList();

    public static string Normalise(string? reason) => string.Join(' ', Display(reason).Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private static string Display(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? NoReason : string.Join(' ', reason.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
