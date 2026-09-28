using System.Globalization;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Reports;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Reports;

public sealed class ReportParameterBinderTests
{
    private static readonly DateOnly Today = new(2026, 9, 15);

    private static readonly ReportDescriptor Report = new(
        "test-report",
        "Test report",
        ReportCategory.OutPatient,
        "Description",
        "Definition",
        [AnalyticsPermissions.MisRead],
        [
            new(ReportKeys.Period, "Period", ReportParameterKind.Period, true, ReportPeriods.Last7Days),
            new(ReportKeys.Facility, "Facility", ReportParameterKind.Facility),
            new(ReportKeys.Department, "Department", ReportParameterKind.Department),
            new(ReportKeys.GroupBy, "Show by", ReportParameterKind.Choice, true, "department", [new("department", "Department"), new("doctor", "Doctor")]),
            new(ReportKeys.Days, "Days", ReportParameterKind.Number, true, "90", Min: 1, Max: 365),
        ],
        [new("x", "X", ReportValueType.Text)],
        null,
        MaxDays: 93);

    private static Result<ReportArguments> Bind(params (string Key, string? Value)[] inputs) =>
        ReportParameterBinder.Bind(Report, inputs.ToDictionary(i => i.Key, i => i.Value), Today);

    [Fact]
    public void Defaults_apply_when_nothing_is_given()
    {
        var args = ReportParameterBinder.Bind(Report, null, Today).Value;

        args.Period.ShouldBe(ReportPeriods.Last7Days);
        (args.From, args.To).ShouldBe((new DateOnly(2026, 9, 9), Today));
        args.Text(ReportKeys.GroupBy).ShouldBe("department");
        args.NumberOf(ReportKeys.Days).ShouldBe(90);
        args.DepartmentId.ShouldBeNull();
        args.Facility.ShouldBeNull();
        args.DayCount.ShouldBe(7);
    }

    [Fact]
    public void Custom_dates_are_used_when_given_without_a_preset()
    {
        var args = Bind((ReportKeys.From, "2026-08-01"), (ReportKeys.To, "2026-08-31")).Value;

        args.Period.ShouldBe(ReportPeriods.Custom);
        (args.From, args.To).ShouldBe((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)));
        args.Start.ShouldBe(new DateTimeOffset(2026, 7, 31, 18, 30, 0, TimeSpan.Zero));
        args.End.ShouldBe(new DateTimeOffset(2026, 8, 31, 18, 30, 0, TimeSpan.Zero));
        args.ToCanonical().ShouldContainKeyAndValue(ReportKeys.From, "2026-08-01");
    }

    [Theory]
    [InlineData("2026-09-10", "2026-09-01", ReportKeys.From)]
    [InlineData("2026-09-01", "2026-09-16", ReportKeys.To)]
    [InlineData("1999-12-31", "2000-01-01", ReportKeys.From)]
    [InlineData("2026-01-01", "2026-09-15", ReportKeys.From)]
    [InlineData("01/09/2026", "2026-09-15", ReportKeys.From)]
    [InlineData(null, "2026-09-15", ReportKeys.From)]
    public void Invalid_custom_dates_are_reported_on_the_right_field(string? from, string to, string field)
    {
        var result = Bind((ReportKeys.Period, ReportPeriods.Custom), (ReportKeys.From, from), (ReportKeys.To, to));

        result.IsFailure.ShouldBeTrue();
        result.Error!.Fields!.ShouldContainKey(field);
    }

    [Fact]
    public void Presets_must_be_known_and_fit_the_report_limit()
    {
        Bind((ReportKeys.Period, "fortnight")).Error!.Fields!.ShouldContainKey(ReportKeys.Period);
        Bind((ReportKeys.Period, ReportPeriods.ThisFinancialYear)).Error!.Fields![ReportKeys.From][0].ShouldContain("93 days");
        Bind((ReportKeys.Period, ReportPeriods.LastMonth)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Ids_choices_and_numbers_are_validated()
    {
        Bind((ReportKeys.Department, "not-a-guid")).Error!.Fields!.ShouldContainKey(ReportKeys.Department);
        Bind((ReportKeys.GroupBy, "ward")).Error!.Fields!.ShouldContainKey(ReportKeys.GroupBy);
        Bind((ReportKeys.Days, "0")).Error!.Fields!.ShouldContainKey(ReportKeys.Days);
        Bind((ReportKeys.Days, "12.5")).Error!.Fields!.ShouldContainKey(ReportKeys.Days);
        Bind((ReportKeys.Facility, "everywhere")).Error!.Fields!.ShouldContainKey(ReportKeys.Facility);

        var id = Guid.NewGuid();
        var args = Bind((ReportKeys.Department, id.ToString().ToUpperInvariant()), (ReportKeys.GroupBy, "DOCTOR"), (ReportKeys.Days, "30"), (ReportKeys.Facility, "all")).Value;
        args.DepartmentId.ShouldBe(id);
        args.Text(ReportKeys.GroupBy).ShouldBe("doctor");
        args.NumberOf(ReportKeys.Days).ShouldBe(30);
        args.Facility.ShouldBe(ReportKeys.AllFacilities);
    }

    [Fact]
    public void Unknown_inputs_are_ignored_and_blanks_mean_not_set()
    {
        var args = Bind(("dropTable", "x"), (ReportKeys.Department, "  "), (ReportKeys.Payer, Guid.NewGuid().ToString())).Value;

        args.Values.ShouldNotContainKey("dropTable");
        args.Values.ShouldNotContainKey(ReportKeys.Payer);
        args.DepartmentId.ShouldBeNull();
    }

    [Fact]
    public void Canonical_form_keeps_the_preset_not_the_dates()
    {
        var canonical = Bind((ReportKeys.Period, ReportPeriods.Yesterday), (ReportKeys.GroupBy, "doctor")).Value.ToCanonical();

        canonical.ShouldContainKeyAndValue(ReportKeys.Period, ReportPeriods.Yesterday);
        canonical.ShouldNotContainKey(ReportKeys.From);
        canonical.Keys.ShouldBe(canonical.Keys.Order(StringComparer.Ordinal).ToList());
        Bind((ReportKeys.Period, ReportPeriods.Yesterday)).Value.CacheKey().ShouldContain(string.Create(CultureInfo.InvariantCulture, $"{Today.AddDays(-1):yyyy-MM-dd}"));
    }
}
