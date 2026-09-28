using System.Globalization;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using Theatre = HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre.OperationTheatre;

namespace HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;

/// <summary>Who may open each family of reports (any one of the listed permissions).</summary>
internal static class Audience
{
    /// <summary>Operational MIS that clinical-quality staff (quality manager, MRD) also review.</summary>
    public static readonly string[] Operations = [AnalyticsPermissions.MisRead, AnalyticsPermissions.ClinicalRead];

    public static readonly string[] Finance = [AnalyticsPermissions.FinanceRead];

    /// <summary>Stock value is reviewed by stores/MIS and by accounts.</summary>
    public static readonly string[] Stock = [AnalyticsPermissions.MisRead, AnalyticsPermissions.FinanceRead];

    public static readonly string[] Mis = [AnalyticsPermissions.MisRead];

    /// <summary>Clinical quality (patient-level reports additionally always require analytics.clinical.read).</summary>
    public static readonly string[] Clinical = [AnalyticsPermissions.ClinicalRead];
}

/// <summary>Parameter factory: the same wording and defaults on every report.</summary>
internal static class P
{
    public static ReportParameter Period(string defaultPreset = ReportPeriods.ThisMonth) =>
        new(ReportKeys.Period, "Period", ReportParameterKind.Period, Required: true, DefaultValue: defaultPreset);

    public static ReportParameter Facility() =>
        new(ReportKeys.Facility, "Facility", ReportParameterKind.Facility, Hint: "Leave empty for the facility you are signed in to.");

    public static ReportParameter Department(string label = "Department") => new(ReportKeys.Department, label, ReportParameterKind.Department);

    public static ReportParameter Doctor(string label = "Doctor") => new(ReportKeys.Practitioner, label, ReportParameterKind.Practitioner);

    public static ReportParameter Payer() => new(ReportKeys.Payer, "Insurer or payer", ReportParameterKind.Payer);

    public static ReportParameter PayerType() =>
        new(ReportKeys.PayerType, "Payer type", ReportParameterKind.Choice, Choices: Choices.Of<PayerType>(Labels.PayerType));

    public static ReportParameter Ward() => new(ReportKeys.Ward, "Ward", ReportParameterKind.Ward);

    public static ReportParameter Store() => new(ReportKeys.Store, "Store", ReportParameterKind.Store);

    public static ReportParameter Theatre() => new(ReportKeys.Theatre, "Operation theatre", ReportParameterKind.Theatre);

    public static ReportParameter Number(string key, string label, int defaultValue, int min, int max, string? hint = null) =>
        new(key, label, ReportParameterKind.Number, Required: true, DefaultValue: defaultValue.ToString(CultureInfo.InvariantCulture), Min: min, Max: max, Hint: hint);

    public static ReportParameter Choice(string key, string label, IReadOnlyList<ReportChoice> choices, string? defaultValue = null, string? hint = null) =>
        new(key, label, ReportParameterKind.Choice, Required: defaultValue is not null, DefaultValue: defaultValue, Choices: choices, Hint: hint);

    /// <summary>Day / week / month buckets for trend reports.</summary>
    public static ReportParameter Interval(string defaultValue = Trend.Day) =>
        Choice(ReportKeys.GroupBy, "Show by", [new(Trend.Day, "Day"), new(Trend.Week, "Week"), new(Trend.Month, "Month")], defaultValue);
}

internal static class Choices
{
    public static IReadOnlyList<ReportChoice> Of<TEnum>(Func<TEnum, string>? label = null)
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(v => new ReportChoice(v.ToString(), label?.Invoke(v) ?? Labels.Humanize(v.ToString()))).ToList();

    public static IReadOnlyList<ReportChoice> Of<TEnum>(IEnumerable<TEnum> values, Func<TEnum, string>? label = null)
        where TEnum : struct, Enum =>
        values.Select(v => new ReportChoice(v.ToString(), label?.Invoke(v) ?? Labels.Humanize(v.ToString()))).ToList();
}

/// <summary>Column factory.</summary>
internal static class C
{
    public static ReportColumn Text(string key, string label) => new(key, label, ReportValueType.Text);

    public static ReportColumn Int(string key, string label) => new(key, label, ReportValueType.Integer);

    public static ReportColumn Dec(string key, string label) => new(key, label, ReportValueType.Decimal);

    public static ReportColumn Money(string key, string label) => new(key, label, ReportValueType.Money);

    public static ReportColumn Pct(string key, string label) => new(key, label, ReportValueType.Percent);

    public static ReportColumn Days(string key, string label) => new(key, label, ReportValueType.Days);

    public static ReportColumn Minutes(string key, string label) => new(key, label, ReportValueType.Minutes);

    public static ReportColumn Date(string key, string label) => new(key, label, ReportValueType.Date);

    public static ReportColumn DateTime(string key, string label) => new(key, label, ReportValueType.DateTime);

    /// <summary>A column that identifies a patient (UHID, name) — only in patient-level reports.</summary>
    public static ReportColumn Patient(string key, string label) => new(key, label, ReportValueType.Text, IsPatientIdentifier: true);
}

/// <summary>Plain-language labels for stored codes (enums are stored as text and never sorted in SQL).</summary>
internal static class Labels
{
    public const string NotRecorded = "Not recorded";

    public static string Humanize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (i > 0 && char.IsUpper(c) && (char.IsLower(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]) && char.IsUpper(value[i - 1]))))
            {
                builder.Append(' ');
                builder.Append(i + 1 < value.Length && char.IsUpper(value[i + 1]) ? c : char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    public static string PayerType(PayerType type) => type switch
    {
        SharedKernel.PayerType.Self => "Self pay",
        SharedKernel.PayerType.GovernmentScheme => "Government scheme",
        _ => Humanize(type.ToString()),
    };

    public static string DischargeType(DischargeType type) => type switch
    {
        Inpatient.Domain.DischargeType.Lama => "LAMA (left against advice)",
        Inpatient.Domain.DischargeType.Dama => "DAMA (discharged against advice)",
        Inpatient.Domain.DischargeType.Expired => "Death",
        Inpatient.Domain.DischargeType.Referred => "Referred out",
        _ => Humanize(type.ToString()),
    };

    public static string PaymentMode(string mode) => mode switch
    {
        nameof(Billing.Domain.PaymentMode.Upi) => "UPI",
        nameof(Billing.Domain.PaymentMode.PaymentGateway) => "Online (payment gateway)",
        nameof(Billing.Domain.PaymentMode.Insurance) => "Insurer / TPA settlement",
        nameof(Billing.Domain.PaymentMode.NetBanking) => "Net banking",
        _ => Humanize(mode),
    };

    public static string WardType(WardType type) => type switch
    {
        Inpatient.Domain.WardType.Icu => "ICU",
        Inpatient.Domain.WardType.Nicu => "NICU",
        Inpatient.Domain.WardType.Picu => "PICU",
        Inpatient.Domain.WardType.Hdu => "HDU",
        _ => Humanize(type.ToString()),
    };

    public static string Name(IReadOnlyDictionary<Guid, string> names, Guid? id, string missing = NotRecorded) =>
        id is { } key ? names.GetValueOrDefault(key, "Unknown") : missing;
}

/// <summary>Day/week/month buckets for trend reports, always continuous so charts show quiet days as zero.</summary>
internal static class Trend
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";

    public static string Of(ReportArguments args) => args.Text(ReportKeys.GroupBy) ?? Day;

    public static DateOnly BucketOf(DateOnly date, string interval) => interval switch
    {
        Week => ReportPeriods.StartOfWeek(date),
        Month => new DateOnly(date.Year, date.Month, 1),
        _ => date,
    };

    /// <summary>Every bucket start between the two dates (inclusive).</summary>
    public static IReadOnlyList<DateOnly> Buckets(DateOnly from, DateOnly to, string interval)
    {
        var buckets = new List<DateOnly>();
        for (var bucket = BucketOf(from, interval); bucket <= to; bucket = Next(bucket, interval))
        {
            buckets.Add(bucket);
        }

        return buckets;
    }

    public static string Label(DateOnly bucket, string interval) => interval switch
    {
        Week => "Week of " + AnalyticsClock.FormatDate(bucket),
        Month => bucket.ToString("MMM yyyy", CultureInfo.InvariantCulture),
        _ => AnalyticsClock.FormatDate(bucket),
    };

    public static string ColumnLabel(string interval) => interval switch
    {
        Week => "Week",
        Month => "Month",
        _ => "Date",
    };

    /// <summary>Days of the bucket that fall inside the reporting period (for per-day averages).</summary>
    public static int DaysIn(DateOnly bucket, string interval, DateOnly from, DateOnly to)
    {
        var end = Next(bucket, interval).AddDays(-1);
        var first = bucket < from ? from : bucket;
        var last = end > to ? to : end;
        return Math.Max(0, last.DayNumber - first.DayNumber + 1);
    }

    private static DateOnly Next(DateOnly bucket, string interval) => interval switch
    {
        Week => bucket.AddDays(7),
        Month => bucket.AddMonths(1),
        _ => bucket.AddDays(1),
    };
}

internal sealed record IdName(Guid Id, string Name);

/// <summary>
/// Name look-ups for the ids a report grouped by (one small query each). Soft-deleted masters still resolve so history
/// keeps its labels; the facility filter of the report context still applies.
/// </summary>
internal static class Names
{
    public static Task<Dictionary<Guid, string>> DepartmentsAsync(ReportContext ctx, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = Distinct(ids);
        return ToMapAsync(ctx, ctx.Db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
            .Where(d => list.Contains(d.Id)).Select(d => new IdName(d.Id, d.Name)), ct);
    }

    public static Task<Dictionary<Guid, string>> PractitionersAsync(ReportContext ctx, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = Distinct(ids);
        return ToMapAsync(ctx, ctx.Db.Set<Practitioner>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
            .Where(p => list.Contains(p.Id)).Select(p => new IdName(p.Id, p.DisplayName)), ct);
    }

    public static Task<Dictionary<Guid, string>> PayersAsync(ReportContext ctx, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = Distinct(ids);
        return ToMapAsync(ctx, ctx.Db.Set<Payer>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
            .Where(p => list.Contains(p.Id)).Select(p => new IdName(p.Id, p.Name)), ct);
    }

    public static Task<Dictionary<Guid, string>> WardsAsync(ReportContext ctx, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = Distinct(ids);
        return ToMapAsync(ctx, ctx.Db.Set<Ward>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
            .Where(w => list.Contains(w.Id)).Select(w => new IdName(w.Id, w.Name)), ct);
    }

    public static Task<Dictionary<Guid, string>> StoresAsync(ReportContext ctx, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = Distinct(ids);
        return ToMapAsync(ctx, ctx.Db.Set<Store>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
            .Where(s => list.Contains(s.Id)).Select(s => new IdName(s.Id, s.Name)), ct);
    }

    public static Task<Dictionary<Guid, string>> ItemsAsync(ReportContext ctx, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = Distinct(ids);
        return ToMapAsync(ctx, ctx.Db.Set<Item>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
            .Where(i => list.Contains(i.Id)).Select(i => new IdName(i.Id, i.Name)), ct);
    }

    public static Task<Dictionary<Guid, string>> ItemCategoriesAsync(ReportContext ctx, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = Distinct(ids);
        return ToMapAsync(ctx, ctx.Db.Set<ItemCategory>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
            .Where(c => list.Contains(c.Id)).Select(c => new IdName(c.Id, c.Name)), ct);
    }

    public static Task<Dictionary<Guid, string>> TheatresAsync(ReportContext ctx, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = Distinct(ids);
        return ToMapAsync(ctx, ctx.Db.Set<Theatre>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
            .Where(t => list.Contains(t.Id)).Select(t => new IdName(t.Id, t.Code + " · " + t.Name)), ct);
    }

    private static List<Guid> Distinct(IEnumerable<Guid?> ids) => ids.OfType<Guid>().Distinct().ToList();

    private static async Task<Dictionary<Guid, string>> ToMapAsync(ReportContext ctx, IQueryable<IdName> query, CancellationToken ct) =>
        (await ctx.LookupAsync(query, ct)).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Name);
}

/// <summary>Small arithmetic helpers for totals and shares.</summary>
internal static class Figures
{
    public static decimal? Share(decimal part, decimal total) => KpiMath.Percent(part, total);

    public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal? Round1(double? value) => value is { } v ? Math.Round((decimal)v, 1, MidpointRounding.AwayFromZero) : null;

    public static decimal? PerDay(decimal total, int days) => days <= 0 ? null : Math.Round(total / days, 1, MidpointRounding.AwayFromZero);
}
