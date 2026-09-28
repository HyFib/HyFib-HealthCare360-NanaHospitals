using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using BillingPaymentMode = HyFib.HealthCare360.Modules.Billing.Domain.PaymentMode;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;

namespace HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;

/// <summary>A billed service line of a finalized bill (member-initialised so EF can group and filter on it).</summary>
internal sealed class RevenueLine
{
    public Guid BillId { get; init; }
    public BillType Type { get; init; }
    public Guid? DepartmentId { get; init; }
    public Guid? PractitionerId { get; init; }
    public ServiceCategory Category { get; init; }
    public decimal Quantity { get; init; }
    public decimal Gross { get; init; }
    public decimal Concession { get; init; }
    public decimal StaffDiscount { get; init; }
    public decimal Net { get; init; }
}

/// <summary>
/// Revenue is recognised when a bill is finalized: the lines of bills finalized in the IST period, after discounts and
/// including GST. Cancelled bills are excluded; returns (negative lines) reduce revenue.
/// </summary>
internal static class RevenueData
{
    public const string BillTypeKey = "billType";

    public const string RevenueRule =
        "Revenue = net amount (after discounts, including GST) of bill lines on bills finalized in the period; cancelled bills are excluded and returns reduce revenue. Credit notes issued after billing are shown in the discounts report.";

    public static ReportParameter BillTypeParameter() =>
        P.Choice(BillTypeKey, "Bill type", [new(nameof(BillType.OutPatient), "Out-patient bills"), new(nameof(BillType.InPatient), "In-patient bills")]);

    public static IQueryable<Bill> FinalizedBills(ReportContext ctx)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var bills = ctx.Db.Set<Bill>().AsNoTracking()
            .Where(b => b.FinalizedAt >= start && b.FinalizedAt < end
                        && (b.Status == BillStatus.Finalized || b.Status == BillStatus.PartiallyPaid || b.Status == BillStatus.Paid));
        if (ctx.Args.EnumOf<BillType>(BillTypeKey) is { } type)
        {
            bills = bills.Where(b => b.Type == type);
        }

        if (ctx.Args.EnumOf<PayerType>(ReportKeys.PayerType) is { } payerType)
        {
            bills = bills.Where(b => b.PayerType == payerType);
        }

        if (ctx.Args.PayerId is { } payerId)
        {
            bills = bills.Where(b => b.PayerId == payerId);
        }

        return bills;
    }

    public static IQueryable<RevenueLine> Lines(ReportContext ctx)
    {
        var lines =
            from l in ctx.Db.Set<BillLine>().AsNoTracking()
            join b in FinalizedBills(ctx) on l.BillId equals b.Id
            select new RevenueLine
            {
                BillId = b.Id,
                Type = b.Type,
                DepartmentId = l.DepartmentId,
                PractitionerId = l.PractitionerId,
                Category = l.Category,
                Quantity = l.Quantity,
                Gross = l.GrossAmount,
                Concession = l.TariffDiscountAmount,
                StaffDiscount = l.LineDiscountAmount + l.BillDiscountShare,
                Net = l.NetAmount,
            };
        if (ctx.Args.DepartmentId is { } departmentId)
        {
            lines = lines.Where(l => l.DepartmentId == departmentId);
        }

        if (ctx.Args.PractitionerId is { } practitionerId)
        {
            lines = lines.Where(l => l.PractitionerId == practitionerId);
        }

        return lines;
    }

    public static object?[] AmountRow(string label, AmountSummary s, decimal total) =>
        [label, s.Op, s.Ip, s.Gross, s.Discounts, s.Net, KpiMath.Percent(s.Net, total)];
}

internal sealed record AmountSummary(Guid? Key, decimal Op, decimal Ip, decimal Gross, decimal Discounts, decimal Net, int Services);

/// <summary>MIS-01: revenue by department.</summary>
internal sealed class RevenueByDepartmentReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "revenue-by-department",
        "Revenue by department",
        ReportCategory.Revenue,
        "Out-patient and in-patient revenue earned by each department, with discounts and share of the total.",
        RevenueData.RevenueRule + " Discounts include tariff concessions and staff discounts.",
        Audience.Finance,
        [P.Period(), P.Facility(), RevenueData.BillTypeParameter(), P.PayerType(), P.Department()],
        [
            C.Text("department", "Department"),
            C.Money("op", "OP revenue"),
            C.Money("ip", "IP revenue"),
            C.Money("gross", "Gross"),
            C.Money("discounts", "Discounts & concessions"),
            C.Money("net", "Net revenue"),
            C.Pct("share", "Share %"),
        ],
        new ReportChartHint(ReportChartType.Bar, "department", ["net"]),
        DefaultSort: "net",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var groups = await ctx.ListAsync(
            RevenueData.Lines(ctx).GroupBy(l => l.DepartmentId)
                .Select(g => new AmountSummary(
                    g.Key,
                    g.Sum(l => l.Type == BillType.OutPatient ? l.Net : 0m),
                    g.Sum(l => l.Type == BillType.InPatient ? l.Net : 0m),
                    g.Sum(l => l.Gross),
                    g.Sum(l => l.Concession + l.StaffDiscount),
                    g.Sum(l => l.Net),
                    g.Count())),
            ct);

        var names = await Names.DepartmentsAsync(ctx, groups.Select(g => g.Key), ct);
        var total = Total(groups);
        return new ReportData(groups.Select(g => RevenueData.AmountRow(Labels.Name(names, g.Key, "Not assigned"), g, total.Net)).ToList())
        {
            Totals = RevenueData.AmountRow("Total", total, total.Net),
            Summary =
            [
                new("net", "Net revenue", total.Net, ReportValueType.Money),
                new("op", "OP revenue", total.Op, ReportValueType.Money),
                new("ip", "IP revenue", total.Ip, ReportValueType.Money),
                new("discounts", "Discounts & concessions", total.Discounts, ReportValueType.Money),
            ],
        };
    }

    internal static AmountSummary Total(IReadOnlyCollection<AmountSummary> groups) => new(
        null, groups.Sum(g => g.Op), groups.Sum(g => g.Ip), groups.Sum(g => g.Gross), groups.Sum(g => g.Discounts), groups.Sum(g => g.Net), groups.Sum(g => g.Services));
}

/// <summary>MIS-01: revenue by doctor (ordering/performing practitioner on the charge).</summary>
internal sealed class RevenueByDoctorReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "revenue-by-doctor",
        "Revenue by doctor",
        ReportCategory.Revenue,
        "Revenue from the services each doctor ordered or performed (consultations, procedures, investigations, visits).",
        RevenueData.RevenueRule + " The doctor is the ordering or performing practitioner recorded on the charge; services without a doctor are grouped together.",
        Audience.Finance,
        [P.Period(), P.Facility(), RevenueData.BillTypeParameter(), P.PayerType(), P.Department("Service department"), P.Doctor()],
        [
            C.Text("doctor", "Doctor"),
            C.Text("department", "Doctor's department"),
            C.Int("services", "Services"),
            C.Money("gross", "Gross"),
            C.Money("discounts", "Discounts & concessions"),
            C.Money("net", "Net revenue"),
            C.Pct("share", "Share %"),
        ],
        new ReportChartHint(ReportChartType.Bar, "doctor", ["net"]),
        DefaultSort: "net",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var groups = await ctx.ListAsync(
            RevenueData.Lines(ctx).GroupBy(l => l.PractitionerId)
                .Select(g => new AmountSummary(g.Key, 0m, 0m, g.Sum(l => l.Gross), g.Sum(l => l.Concession + l.StaffDiscount), g.Sum(l => l.Net), g.Count(l => l.Quantity > 0))),
            ct);

        var ids = groups.Select(g => g.Key).OfType<Guid>().ToList();
        var doctors = await ctx.LookupAsync(
            ctx.Db.Set<Practitioner>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
                .Where(p => ids.Contains(p.Id))
                .Select(p => new DoctorInfo(p.Id, p.DisplayName, p.DepartmentId)),
            ct);
        var byId = doctors.ToDictionary(d => d.Id);
        var departments = await Names.DepartmentsAsync(ctx, doctors.Select(d => d.DepartmentId), ct);
        var total = RevenueByDepartmentReport.Total(groups);

        var rows = groups
            .Select(g =>
            {
                var doctor = g.Key is { } id ? byId.GetValueOrDefault(id) : null;
                return new object?[]
                {
                    g.Key is null ? "No doctor recorded" : doctor?.Name ?? "Unknown", doctor is null ? null : Labels.Name(departments, doctor.DepartmentId),
                    g.Services, g.Gross, g.Discounts, g.Net, KpiMath.Percent(g.Net, total.Net),
                };
            })
            .ToList();

        return new ReportData(rows)
        {
            Totals = ["Total", null, total.Services, total.Gross, total.Discounts, total.Net, total.Net == 0 ? null : 100m],
            Summary =
            [
                new("net", "Net revenue", total.Net, ReportValueType.Money),
                new("doctors", "Doctors with revenue", groups.Count(g => g.Key is not null), ReportValueType.Integer),
                new("services", "Services", total.Services, ReportValueType.Integer),
            ],
        };
    }

    private sealed record DoctorInfo(Guid Id, string Name, Guid? DepartmentId);
}

/// <summary>MIS-01: revenue by service category (consultation, laboratory, bed charges…).</summary>
internal sealed class RevenueByServiceReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "revenue-by-service",
        "Revenue by service category",
        ReportCategory.Revenue,
        "Revenue by type of service: consultations, laboratory, radiology, procedures, bed charges, pharmacy issues and more.",
        RevenueData.RevenueRule + " Units = quantity billed (returns deducted).",
        Audience.Finance,
        [P.Period(), P.Facility(), RevenueData.BillTypeParameter(), P.PayerType(), P.Department()],
        [
            C.Text("category", "Service category"),
            C.Dec("units", "Units"),
            C.Money("gross", "Gross"),
            C.Money("discounts", "Discounts & concessions"),
            C.Money("net", "Net revenue"),
            C.Pct("share", "Share %"),
        ],
        new ReportChartHint(ReportChartType.Pie, "category", ["net"], TopN: 10),
        DefaultSort: "net",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var groups = await ctx.ListAsync(
            RevenueData.Lines(ctx).GroupBy(l => l.Category)
                .Select(g => new CategoryRow(g.Key, g.Sum(l => l.Quantity), g.Sum(l => l.Gross), g.Sum(l => l.Concession + l.StaffDiscount), g.Sum(l => l.Net))),
            ct);

        var net = groups.Sum(g => g.Net);
        var rows = groups
            .Select(g => new object?[] { Labels.Humanize(g.Category.ToString()), g.Units, g.Gross, g.Discounts, g.Net, KpiMath.Percent(g.Net, net) })
            .ToList();
        var top = groups.OrderByDescending(g => g.Net).FirstOrDefault();
        return new ReportData(rows)
        {
            Totals = ["Total", groups.Sum(g => g.Units), groups.Sum(g => g.Gross), groups.Sum(g => g.Discounts), net, net == 0 ? null : 100m],
            Summary =
            [
                new("net", "Net revenue", net, ReportValueType.Money),
                new("top", "Largest category", top is null ? null : Labels.Humanize(top.Category.ToString()), ReportValueType.Text),
            ],
        };
    }

    private sealed record CategoryRow(ServiceCategory Category, decimal Units, decimal Gross, decimal Discounts, decimal Net);
}

/// <summary>MIS-01 / INS: revenue by payer with the patient and payer shares and what is still owed.</summary>
internal sealed class RevenueByPayerReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "revenue-by-payer",
        "Revenue by payer",
        ReportCategory.Revenue,
        "Bills finalized in the period by payer (self pay, insurers, TPAs, corporates, schemes) with the patient and payer shares, amounts received and dues.",
        "Bills finalized in the period (cancelled bills excluded). Collected = patient payments less refunds; settled = insurer/TPA settlements; outstanding = patient due + payer share not yet settled or disallowed.",
        Audience.Finance,
        [P.Period(), P.Facility(), RevenueData.BillTypeParameter(), P.PayerType(), P.Payer()],
        [
            C.Text("payer", "Payer"),
            C.Text("payerType", "Payer type"),
            C.Int("bills", "Bills"),
            C.Money("net", "Billed"),
            C.Money("patientShare", "Patient share"),
            C.Money("payerShare", "Payer share"),
            C.Money("collected", "Collected from patients"),
            C.Money("settled", "Settled by payer"),
            C.Money("outstanding", "Outstanding"),
        ],
        new ReportChartHint(ReportChartType.Bar, "payer", ["net"]),
        DefaultSort: "net",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var groups = await ctx.ListAsync(
            RevenueData.FinalizedBills(ctx).GroupBy(b => new { b.PayerType, b.PayerId })
                .Select(g => new PayerRow(
                    g.Key.PayerType,
                    g.Key.PayerId,
                    g.Count(),
                    g.Sum(b => b.NetAmount),
                    g.Sum(b => b.PatientAmount),
                    g.Sum(b => b.PayerAmount),
                    g.Sum(b => b.PaidAmount - b.RefundedAmount),
                    g.Sum(b => b.PayerSettledAmount),
                    g.Sum(b => b.DueAmount + (b.PayerAmount - b.PayerSettledAmount - b.PayerDisallowedAmount > 0m ? b.PayerAmount - b.PayerSettledAmount - b.PayerDisallowedAmount : 0m)))),
            ct);

        var payers = await Names.PayersAsync(ctx, groups.Select(g => g.PayerId), ct);
        var rows = groups
            .Select(g => new object?[]
            {
                g.PayerId is null ? Labels.PayerType(g.PayerType) : Labels.Name(payers, g.PayerId), Labels.PayerType(g.PayerType), g.Bills, g.Net, g.PatientShare,
                g.PayerShare, g.Collected, g.Settled, g.Outstanding,
            })
            .ToList();

        var net = groups.Sum(g => g.Net);
        var payerShare = groups.Sum(g => g.PayerShare);
        return new ReportData(rows)
        {
            Totals =
            [
                "Total", null, groups.Sum(g => g.Bills), net, groups.Sum(g => g.PatientShare), payerShare, groups.Sum(g => g.Collected), groups.Sum(g => g.Settled),
                groups.Sum(g => g.Outstanding),
            ],
            Summary =
            [
                new("net", "Billed", net, ReportValueType.Money),
                new("payerPercent", "Billed to payers", KpiMath.Percent(payerShare, net), ReportValueType.Percent),
                new("outstanding", "Outstanding", groups.Sum(g => g.Outstanding), ReportValueType.Money),
            ],
        };
    }

    private sealed record PayerRow(
        PayerType PayerType, Guid? PayerId, int Bills, decimal Net, decimal PatientShare, decimal PayerShare, decimal Collected, decimal Settled, decimal Outstanding);
}

/// <summary>Daily revenue and collections across billing and the pharmacy counter (trend).</summary>
internal sealed class DailyRevenueReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "daily-revenue",
        "Revenue and collections (trend)",
        ReportCategory.Revenue,
        "OP billing, IP billing and pharmacy counter sales by day, week or month, with the money actually collected.",
        "Billing revenue by the IST date bills were finalized (cancelled bills excluded). Pharmacy = counter and prescription sales less returns (cancelled invoices excluded). Collections = money received at billing and pharmacy counters (all modes except advance adjustments and pharmacy credit) less refunds paid out.",
        Audience.Finance,
        [P.Period(), P.Facility(), P.Interval()],
        [
            C.Date("period", "Date"),
            C.Money("op", "OP billing"),
            C.Money("ip", "IP billing"),
            C.Money("pharmacy", "Pharmacy sales"),
            C.Money("revenue", "Total revenue"),
            C.Money("collections", "Collections"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "period", ["op", "ip", "pharmacy"], TimeSeries: true));

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var bills = await ctx.ListAsync(
            ctx.Db.Set<Bill>().AsNoTracking()
                .Where(b => b.FinalizedAt >= start && b.FinalizedAt < end
                            && (b.Status == BillStatus.Finalized || b.Status == BillStatus.PartiallyPaid || b.Status == BillStatus.Paid))
                .GroupBy(b => new { Day = DateOnly.FromDateTime(b.FinalizedAt!.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime), b.Type })
                .Select(g => new DayAmount(g.Key.Day, g.Key.Type == BillType.InPatient ? 1 : 0, g.Sum(b => b.NetAmount))),
            ct);
        var pharmacy = await ctx.ListAsync(
            ctx.Db.Set<PharmacySale>().AsNoTracking()
                .Where(s => s.SoldAt >= start && s.SoldAt < end && s.Status != SaleStatus.Cancelled)
                .GroupBy(s => DateOnly.FromDateTime(s.SoldAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime))
                .Select(g => new DayAmount(g.Key, 2, g.Sum(s => s.NetAmount - s.ReturnedAmount))),
            ct);
        var collections = await CollectionsByDayAsync(ctx, start, end, ct);

        var interval = Trend.Of(ctx.Args);
        var revenue = bills.Concat(pharmacy).GroupBy(d => (Bucket: Trend.BucketOf(d.Day, interval), d.Stream)).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));
        var received = collections.GroupBy(d => Trend.BucketOf(d.Day, interval)).ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));

        var rows = Trend.Buckets(ctx.Args.From, ctx.Args.To, interval)
            .Select(bucket =>
            {
                var op = revenue.GetValueOrDefault((bucket, 0));
                var ip = revenue.GetValueOrDefault((bucket, 1));
                var sales = revenue.GetValueOrDefault((bucket, 2));
                return new object?[] { bucket, op, ip, sales, op + ip + sales, received.GetValueOrDefault(bucket) };
            })
            .ToList();

        decimal Sum(int stream) => bills.Concat(pharmacy).Where(d => d.Stream == stream).Sum(d => d.Amount);
        var total = Sum(0) + Sum(1) + Sum(2);
        var collected = collections.Sum(d => d.Amount);
        return new ReportData(rows)
        {
            Columns =
            [
                new ReportColumn("period", Trend.ColumnLabel(interval), interval == Trend.Month ? ReportValueType.Month : ReportValueType.Date),
                .. Descriptor.Columns.Skip(1),
            ],
            Totals = ["Total", Sum(0), Sum(1), Sum(2), total, collected],
            Summary =
            [
                new("revenue", "Total revenue", total, ReportValueType.Money),
                new("collections", "Collections", collected, ReportValueType.Money),
                new("perDay", "Revenue per day", Figures.PerDay(total, ctx.Args.DayCount), ReportValueType.Money),
            ],
        };
    }

    /// <summary>Net money received per IST day (billing receipts and pharmacy payments, less refunds paid).</summary>
    internal static async Task<List<DayAmount>> CollectionsByDayAsync(ReportContext ctx, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var billing = await ctx.ListAsync(
            (from p in ctx.Db.Set<ReceiptPayment>().AsNoTracking()
             join r in ctx.Db.Set<Receipt>() on p.ReceiptId equals r.Id
             where r.ReceivedAt >= start && r.ReceivedAt < end && p.Mode != BillingPaymentMode.Advance
             select new { Day = DateOnly.FromDateTime(r.ReceivedAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime), p.Amount })
            .GroupBy(x => x.Day)
            .Select(g => new DayAmount(g.Key, 0, g.Sum(x => x.Amount))),
            ct);
        var refunds = await ctx.ListAsync(
            ctx.Db.Set<Refund>().AsNoTracking()
                .Where(r => r.Status == RefundStatus.Paid && r.PaidAt >= start && r.PaidAt < end && r.Mode != BillingPaymentMode.Advance)
                .GroupBy(r => DateOnly.FromDateTime(r.PaidAt!.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime))
                .Select(g => new DayAmount(g.Key, 0, -g.Sum(r => r.Amount))),
            ct);
        var pharmacy = await ctx.ListAsync(
            (from p in ctx.Db.Set<PharmacyPayment>().AsNoTracking()
             join s in ctx.Db.Set<PharmacySale>() on p.PharmacySaleId equals s.Id
             where p.ReceivedAt >= start && p.ReceivedAt < end && p.Mode != PharmacyPaymentMode.Credit
             select new { Day = DateOnly.FromDateTime(p.ReceivedAt.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime), Amount = p.IsRefund ? -p.Amount : p.Amount })
            .GroupBy(x => x.Day)
            .Select(g => new DayAmount(g.Key, 0, g.Sum(x => x.Amount))),
            ct);
        return [.. billing, .. refunds, .. pharmacy];
    }

    internal sealed record DayAmount(DateOnly Day, int Stream, decimal Amount);
}

/// <summary>MIS-01: collections by payment mode (cash, card, UPI…), billing and pharmacy counters together.</summary>
internal sealed class CollectionsByModeReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "collections-by-mode",
        "Collections by payment mode",
        ReportCategory.Revenue,
        "Money received in the period by payment mode at billing and pharmacy counters, less refunds paid out.",
        "Billing receipts (bill payments, IP deposits, advances and insurer settlements) and pharmacy payments received in the period. Advance adjustments and pharmacy credit are not money received and are excluded. Net = received − refunds paid in the same mode.",
        Audience.Finance,
        [P.Period(), P.Facility()],
        [
            C.Text("mode", "Payment mode"),
            C.Int("transactions", "Transactions"),
            C.Money("billing", "Billing"),
            C.Money("pharmacy", "Pharmacy"),
            C.Money("refunds", "Refunds paid"),
            C.Money("net", "Net collected"),
            C.Pct("share", "Share %"),
        ],
        new ReportChartHint(ReportChartType.Pie, "mode", ["net"]),
        DefaultSort: "net",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var billing = await ctx.ListAsync(
            (from p in ctx.Db.Set<ReceiptPayment>().AsNoTracking()
             join r in ctx.Db.Set<Receipt>() on p.ReceiptId equals r.Id
             where r.ReceivedAt >= start && r.ReceivedAt < end && p.Mode != BillingPaymentMode.Advance
             select p)
            .GroupBy(p => p.Mode)
            .Select(g => new BillingMode(g.Key, g.Select(p => p.ReceiptId).Distinct().Count(), g.Sum(p => p.Amount))),
            ct);
        var refunds = await ctx.ListAsync(
            ctx.Db.Set<Refund>().AsNoTracking()
                .Where(r => r.Status == RefundStatus.Paid && r.PaidAt >= start && r.PaidAt < end && r.Mode != null && r.Mode != BillingPaymentMode.Advance)
                .GroupBy(r => r.Mode)
                .Select(g => new RefundMode(g.Key, g.Sum(r => r.Amount))),
            ct);
        var pharmacy = await ctx.ListAsync(
            (from p in ctx.Db.Set<PharmacyPayment>().AsNoTracking()
             join s in ctx.Db.Set<PharmacySale>() on p.PharmacySaleId equals s.Id
             where p.ReceivedAt >= start && p.ReceivedAt < end && p.Mode != PharmacyPaymentMode.Credit
             select p)
            .GroupBy(p => new { p.Mode, p.IsRefund })
            .Select(g => new PharmacyMode(g.Key.Mode, g.Key.IsRefund, g.Count(), g.Sum(p => p.Amount))),
            ct);

        var modes = billing.Select(b => b.Mode.ToString())
            .Concat(refunds.Select(r => r.Mode?.ToString() ?? "Other"))
            .Concat(pharmacy.Select(p => p.Mode.ToString()))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var lines = modes
            .Select(mode =>
            {
                var fromBilling = billing.Where(b => b.Mode.ToString() == mode).ToList();
                var fromPharmacy = pharmacy.Where(p => p.Mode.ToString() == mode).ToList();
                var pharmacyIn = fromPharmacy.Where(p => !p.IsRefund).Sum(p => p.Amount);
                var refunded = refunds.Where(r => (r.Mode?.ToString() ?? "Other") == mode).Sum(r => r.Amount) + fromPharmacy.Where(p => p.IsRefund).Sum(p => p.Amount);
                var billingIn = fromBilling.Sum(b => b.Amount);
                return (Mode: mode, Transactions: fromBilling.Sum(b => b.Receipts) + fromPharmacy.Where(p => !p.IsRefund).Sum(p => p.Count), Billing: billingIn,
                    Pharmacy: pharmacyIn, Refunds: refunded, Net: billingIn + pharmacyIn - refunded);
            })
            .ToList();

        var net = lines.Sum(l => l.Net);
        var rows = lines
            .Select(l => new object?[] { Labels.PaymentMode(l.Mode), l.Transactions, l.Billing, l.Pharmacy, l.Refunds, l.Net, KpiMath.Percent(l.Net, net) })
            .ToList();
        var cash = lines.Where(l => l.Mode == nameof(BillingPaymentMode.Cash)).Sum(l => l.Net);
        return new ReportData(rows)
        {
            Totals = ["Total", lines.Sum(l => l.Transactions), lines.Sum(l => l.Billing), lines.Sum(l => l.Pharmacy), lines.Sum(l => l.Refunds), net, net == 0 ? null : 100m],
            Summary =
            [
                new("net", "Net collected", net, ReportValueType.Money),
                new("cashPercent", "Cash share", KpiMath.Percent(cash, net), ReportValueType.Percent),
                new("digitalPercent", "Digital share (card, UPI, online)", KpiMath.Percent(net - cash - lines.Where(l => l.Mode is nameof(BillingPaymentMode.Cheque) or nameof(BillingPaymentMode.Insurance)).Sum(l => l.Net), net), ReportValueType.Percent),
            ],
        };
    }

    private sealed record BillingMode(BillingPaymentMode Mode, int Receipts, decimal Amount);

    private sealed record RefundMode(BillingPaymentMode? Mode, decimal Amount);

    private sealed record PharmacyMode(PharmacyPaymentMode Mode, bool IsRefund, int Count, decimal Amount);
}

/// <summary>MIS-01 / INS-07: outstanding dues and ageing (patients and payers).</summary>
internal sealed class OutstandingAgeingReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "outstanding-ageing",
        "Outstanding dues and ageing",
        ReportCategory.Revenue,
        "What patients and payers still owe on finalized bills today, by age of the bill.",
        "Patient dues = the patient share of finalized bills not yet paid (after credit notes). Payer dues = the payer share not yet settled or disallowed. Age = days since the bill was finalized (IST), as of today.",
        Audience.Finance,
        [P.Facility(), RevenueData.BillTypeParameter(), P.PayerType()],
        [
            C.Text("party", "Owed by"),
            C.Int("bills", "Bills"),
            C.Money("d0", "0–30 days"),
            C.Money("d31", "31–60 days"),
            C.Money("d61", "61–90 days"),
            C.Money("d91", "91–180 days"),
            C.Money("d181", "Over 180 days"),
            C.Money("total", "Total due"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "party", ["d0", "d31", "d61", "d91", "d181"]),
        DefaultSort: "total",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var todayNumber = ctx.Today.DayNumber;
        var bills = ctx.Db.Set<Bill>().AsNoTracking()
            .Where(b => b.FinalizedAt != null && (b.Status == BillStatus.Finalized || b.Status == BillStatus.PartiallyPaid || b.Status == BillStatus.Paid));
        if (ctx.Args.EnumOf<BillType>(RevenueData.BillTypeKey) is { } type)
        {
            bills = bills.Where(b => b.Type == type);
        }

        if (ctx.Args.EnumOf<PayerType>(ReportKeys.PayerType) is { } payerType)
        {
            bills = bills.Where(b => b.PayerType == payerType);
        }

        var patient = await ctx.SingleAsync(
            bills.Where(b => b.DueAmount > 0)
                .Select(b => new { Due = b.DueAmount, Age = todayNumber - DateOnly.FromDateTime(b.FinalizedAt!.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber })
                .GroupBy(x => 1)
                .Select(g => new AgeingRow(
                    null,
                    g.Count(),
                    g.Sum(x => x.Age <= 30 ? x.Due : 0m),
                    g.Sum(x => x.Age > 30 && x.Age <= 60 ? x.Due : 0m),
                    g.Sum(x => x.Age > 60 && x.Age <= 90 ? x.Due : 0m),
                    g.Sum(x => x.Age > 90 && x.Age <= 180 ? x.Due : 0m),
                    g.Sum(x => x.Age > 180 ? x.Due : 0m),
                    g.Sum(x => x.Due))),
            ct);
        var payers = await ctx.ListAsync(
            bills.Where(b => b.PayerId != null && b.PayerAmount - b.PayerSettledAmount - b.PayerDisallowedAmount > 0m)
                .Select(b => new
                {
                    b.PayerId,
                    Due = b.PayerAmount - b.PayerSettledAmount - b.PayerDisallowedAmount,
                    Age = todayNumber - DateOnly.FromDateTime(b.FinalizedAt!.Value.AddMinutes(AnalyticsClock.OffsetMinutes).UtcDateTime).DayNumber,
                })
                .GroupBy(x => x.PayerId)
                .Select(g => new AgeingRow(
                    g.Key,
                    g.Count(),
                    g.Sum(x => x.Age <= 30 ? x.Due : 0m),
                    g.Sum(x => x.Age > 30 && x.Age <= 60 ? x.Due : 0m),
                    g.Sum(x => x.Age > 60 && x.Age <= 90 ? x.Due : 0m),
                    g.Sum(x => x.Age > 90 && x.Age <= 180 ? x.Due : 0m),
                    g.Sum(x => x.Age > 180 ? x.Due : 0m),
                    g.Sum(x => x.Due))),
            ct);

        var names = await Names.PayersAsync(ctx, payers.Select(p => p.PayerId), ct);
        var all = new List<AgeingRow>(payers.Count + 1);
        if (patient is not null)
        {
            all.Add(patient);
        }

        all.AddRange(payers);
        var rows = all
            .Select(r => new object?[]
            {
                r.PayerId is null ? "Patients (self-pay share)" : Labels.Name(names, r.PayerId), r.Bills, r.D0, r.D31, r.D61, r.D91, r.D181, r.Total,
            })
            .ToList();

        var total = all.Sum(r => r.Total);
        return new ReportData(rows)
        {
            Totals = ["Total", all.Sum(r => r.Bills), all.Sum(r => r.D0), all.Sum(r => r.D31), all.Sum(r => r.D61), all.Sum(r => r.D91), all.Sum(r => r.D181), total],
            Summary =
            [
                new("total", "Total outstanding", total, ReportValueType.Money),
                new("patients", "Owed by patients", patient?.Total ?? 0m, ReportValueType.Money),
                new("payers", "Owed by payers", payers.Sum(p => p.Total), ReportValueType.Money),
                new("over90", "Older than 90 days", all.Sum(r => r.D91 + r.D181), ReportValueType.Money),
            ],
            Notes = ["Amounts are balances as of now; the age is counted from the date each bill was finalized."],
        };
    }

    private sealed record AgeingRow(Guid? PayerId, int Bills, decimal D0, decimal D31, decimal D61, decimal D91, decimal D181, decimal Total);
}

/// <summary>OPB-04 / MB-05: discounts, concessions, credit notes and write-offs.</summary>
internal sealed class DiscountsReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "discounts",
        "Discounts given",
        ReportCategory.Revenue,
        "Tariff concessions and staff discounts by department, with credit notes, write-offs and discount approvals in the period.",
        "From bills finalized in the period: tariff concession = the payer plan's automatic discount; staff discount = line discounts plus each line's share of the bill discount. Discount % = total discount ÷ gross. Credit notes and write-offs are counted by the date issued.",
        Audience.Finance,
        [P.Period(), P.Facility(), RevenueData.BillTypeParameter(), P.Department()],
        [
            C.Text("department", "Department"),
            C.Int("bills", "Bills"),
            C.Money("gross", "Gross"),
            C.Money("concession", "Tariff concession"),
            C.Money("staffDiscount", "Staff discount"),
            C.Money("total", "Total discount"),
            C.Pct("percent", "Discount %"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "department", ["concession", "staffDiscount"]),
        DefaultSort: "total",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var groups = await ctx.ListAsync(
            RevenueData.Lines(ctx).GroupBy(l => l.DepartmentId)
                .Select(g => new DiscountRow(
                    g.Key, g.Select(l => l.BillId).Distinct().Count(), g.Sum(l => l.Gross), g.Sum(l => l.Concession), g.Sum(l => l.StaffDiscount))),
            ct);
        var credits = await ctx.ListAsync(
            ctx.Db.Set<CreditNote>().AsNoTracking()
                .Where(c => c.IssuedAt >= start && c.IssuedAt < end)
                .GroupBy(c => c.Kind)
                .Select(g => new CreditRow(g.Key, g.Count(), g.Sum(c => c.Amount))),
            ct);
        var approvals = await ctx.ListAsync(
            ctx.Db.Set<DiscountApproval>().AsNoTracking()
                .Where(a => a.RequestedAt >= start && a.RequestedAt < end)
                .GroupBy(a => a.Status)
                .Select(g => new ApprovalRow(g.Key, g.Count(), g.Sum(a => a.RequestedAmount))),
            ct);

        var names = await Names.DepartmentsAsync(ctx, groups.Select(g => g.DepartmentId), ct);
        var rows = groups
            .Where(g => g.Concession != 0 || g.StaffDiscount != 0)
            .Select(g => new object?[]
            {
                Labels.Name(names, g.DepartmentId, "Not assigned"), g.Bills, g.Gross, g.Concession, g.StaffDiscount, g.Concession + g.StaffDiscount,
                KpiMath.Percent(g.Concession + g.StaffDiscount, g.Gross),
            })
            .ToList();

        var gross = groups.Sum(g => g.Gross);
        var concession = groups.Sum(g => g.Concession);
        var staff = groups.Sum(g => g.StaffDiscount);
        decimal Credit(CreditNoteKind kind) => credits.Where(c => c.Kind == kind).Sum(c => c.Amount);
        int Approvals(DiscountApprovalStatus status) => approvals.Where(a => a.Status == status).Sum(a => a.Count);
        return new ReportData(rows)
        {
            Totals = ["Total", null, gross, concession, staff, concession + staff, KpiMath.Percent(concession + staff, gross)],
            Summary =
            [
                new("total", "Discounts & concessions", concession + staff, ReportValueType.Money),
                new("staffDiscount", "Staff discounts", staff, ReportValueType.Money),
                new("creditNotes", "Credit notes issued", Credit(CreditNoteKind.CreditNote), ReportValueType.Money),
                new("writeOffs", "Written off", Credit(CreditNoteKind.WriteOff), ReportValueType.Money),
            ],
            Notes =
            [
                $"Discount approvals requested in the period: {approvals.Sum(a => a.Count)} ({Approvals(DiscountApprovalStatus.Approved)} approved, {Approvals(DiscountApprovalStatus.Rejected)} rejected, {Approvals(DiscountApprovalStatus.Pending)} pending).",
            ],
        };
    }

    private sealed record DiscountRow(Guid? DepartmentId, int Bills, decimal Gross, decimal Concession, decimal StaffDiscount);

    private sealed record CreditRow(CreditNoteKind Kind, int Count, decimal Amount);

    private sealed record ApprovalRow(DiscountApprovalStatus Status, int Count, decimal Amount);
}

/// <summary>MB-05 / IPB-08: refunds requested, approved and paid.</summary>
internal sealed class RefundsReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "refunds",
        "Refunds",
        ReportCategory.Revenue,
        "Refund requests raised in the period by purpose, with amounts paid out, still pending and rejected.",
        "Refund requests by the date raised. Pending = requested or approved but not yet paid. Days to pay = average days from request to payment for refunds paid.",
        Audience.Finance,
        [P.Period(), P.Facility()],
        [
            C.Text("purpose", "Refund for"),
            C.Int("requests", "Requests"),
            C.Money("requested", "Amount requested"),
            C.Money("paid", "Paid out"),
            C.Money("pending", "Pending"),
            C.Money("rejected", "Rejected"),
            C.Days("daysToPay", "Days to pay"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "purpose", ["paid", "pending", "rejected"]),
        DefaultSort: "requested",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (start, end) = (ctx.Args.Start, ctx.Args.End);
        var groups = await ctx.ListAsync(
            ctx.Db.Set<Refund>().AsNoTracking()
                .Where(r => r.RequestedAt >= start && r.RequestedAt < end)
                .GroupBy(r => r.Purpose)
                .Select(g => new RefundRow(
                    g.Key,
                    g.Count(),
                    g.Sum(r => r.Amount),
                    g.Sum(r => r.Status == RefundStatus.Paid ? r.Amount : 0m),
                    g.Sum(r => r.Status == RefundStatus.Requested || r.Status == RefundStatus.Approved ? r.Amount : 0m),
                    g.Sum(r => r.Status == RefundStatus.Rejected ? r.Amount : 0m),
                    g.Average(r => r.Status == RefundStatus.Paid && r.PaidAt != null ? (double?)(r.PaidAt.Value - r.RequestedAt).TotalHours : null))),
            ct);

        var rows = groups
            .Select(g => new object?[] { Purpose(g.Purpose), g.Requests, g.Requested, g.Paid, g.Pending, g.Rejected, Figures.Round1(g.HoursToPay / 24) })
            .ToList();
        return new ReportData(rows)
        {
            Totals = ["Total", groups.Sum(g => g.Requests), groups.Sum(g => g.Requested), groups.Sum(g => g.Paid), groups.Sum(g => g.Pending), groups.Sum(g => g.Rejected), null],
            Summary =
            [
                new("paid", "Refunds paid", groups.Sum(g => g.Paid), ReportValueType.Money),
                new("pending", "Pending refunds", groups.Sum(g => g.Pending), ReportValueType.Money),
                new("requests", "Requests", groups.Sum(g => g.Requests), ReportValueType.Integer),
            ],
        };
    }

    private static string Purpose(RefundPurpose purpose) => purpose switch
    {
        RefundPurpose.Bill => "Bills (overpayment or cancellation)",
        RefundPurpose.Deposit => "Unused in-patient deposits",
        RefundPurpose.Advance => "Advances (e.g. cancelled appointments)",
        _ => Labels.Humanize(purpose.ToString()),
    };

    private sealed record RefundRow(RefundPurpose Purpose, int Requests, decimal Requested, decimal Paid, decimal Pending, decimal Rejected, double? HoursToPay);
}

/// <summary>DFA-02..05: doctor fee accruals and payouts.</summary>
internal sealed class DoctorFeeSummaryReport : ReportDefinition
{
    public override ReportDescriptor Descriptor { get; } = new(
        "doctor-fee-summary",
        "Doctor fee summary",
        ReportCategory.Revenue,
        "Each doctor's share of billed services earned in the period, and how much has been paid out.",
        "Accruals by the IST date the fee was earned (bill finalized, or paid, per the doctor-fee setting). Share = doctor's share after reversals of cancelled bills. Paid, in payout and pending split the share by payout status.",
        Audience.Finance,
        [P.Period(ReportPeriods.LastMonth), P.Facility(), P.Doctor()],
        [
            C.Text("doctor", "Doctor"),
            C.Text("department", "Department"),
            C.Int("services", "Services"),
            C.Money("basis", "Billed value"),
            C.Money("share", "Doctor share"),
            C.Pct("sharePercent", "Share %"),
            C.Money("paid", "Paid out"),
            C.Money("inPayout", "In payout"),
            C.Money("pending", "Pending"),
        ],
        new ReportChartHint(ReportChartType.StackedBar, "doctor", ["paid", "inPayout", "pending"]),
        DefaultSort: "share",
        DefaultSortDescending: true);

    public override async Task<ReportData> RunAsync(ReportContext ctx, CancellationToken ct)
    {
        var (periodFrom, periodTo) = (ctx.Args.From, ctx.Args.To);
        var accruals = ctx.Db.Set<DoctorFeeAccrual>().AsNoTracking().Where(a => a.AccruedOn >= periodFrom && a.AccruedOn <= periodTo && a.Status != AccrualStatus.Reversed);
        if (ctx.Args.PractitionerId is { } practitionerId)
        {
            accruals = accruals.Where(a => a.PractitionerId == practitionerId);
        }

        var groups = await ctx.ListAsync(
            accruals.GroupBy(a => a.PractitionerId)
                .Select(g => new FeeRow(
                    g.Key,
                    g.Count(a => a.ReversalOfId == null),
                    g.Sum(a => a.BasisAmount),
                    g.Sum(a => a.ShareAmount),
                    g.Sum(a => a.Status == AccrualStatus.Paid ? a.ShareAmount : 0m),
                    g.Sum(a => a.Status == AccrualStatus.InPayout ? a.ShareAmount : 0m),
                    g.Sum(a => a.Status == AccrualStatus.Accrued ? a.ShareAmount : 0m))),
            ct);

        var ids = groups.Select(g => g.PractitionerId).ToList();
        var doctors = await ctx.LookupAsync(
            ctx.Db.Set<Practitioner>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
                .Where(p => ids.Contains(p.Id))
                .Select(p => new { p.Id, p.DisplayName, p.DepartmentId }),
            ct);
        var byId = doctors.ToDictionary(d => d.Id);
        var departments = await Names.DepartmentsAsync(ctx, doctors.Select(d => d.DepartmentId), ct);
        var rows = groups
            .Select(g =>
            {
                var doctor = byId.GetValueOrDefault(g.PractitionerId);
                return new object?[]
                {
                    doctor?.DisplayName ?? "Unknown", doctor is null ? null : Labels.Name(departments, doctor.DepartmentId), g.Services, g.Basis, g.Share,
                    KpiMath.Percent(g.Share, g.Basis), g.Paid, g.InPayout, g.Pending,
                };
            })
            .ToList();

        var share = groups.Sum(g => g.Share);
        var basis = groups.Sum(g => g.Basis);
        return new ReportData(rows)
        {
            Totals = ["Total", null, groups.Sum(g => g.Services), basis, share, KpiMath.Percent(share, basis), groups.Sum(g => g.Paid), groups.Sum(g => g.InPayout), groups.Sum(g => g.Pending)],
            Summary =
            [
                new("share", "Doctor share earned", share, ReportValueType.Money),
                new("paid", "Paid out", groups.Sum(g => g.Paid), ReportValueType.Money),
                new("pending", "Pending payout", groups.Sum(g => g.Pending) + groups.Sum(g => g.InPayout), ReportValueType.Money),
            ],
        };
    }

    private sealed record FeeRow(Guid PractitionerId, int Services, decimal Basis, decimal Share, decimal Paid, decimal InPayout, decimal Pending);
}
