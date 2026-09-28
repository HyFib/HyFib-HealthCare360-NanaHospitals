using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Reports;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Reports;

/// <summary>Guards the catalogue's promises: coverage, unique ids, permissions and consistent column/chart metadata.</summary>
public sealed class ReportCatalogTests
{
    private static readonly IReadOnlyList<ReportDescriptor> Reports = new ReportCatalog().All.Select(r => r.Descriptor).ToList();

    [Fact]
    public void The_catalogue_offers_at_least_thirty_distinct_reports_in_every_category()
    {
        Reports.Count.ShouldBeGreaterThanOrEqualTo(30);
        Reports.Select(r => r.Id).ShouldBeUnique();
        Reports.Select(r => r.Title).ShouldBeUnique();
        Reports.ShouldAllBe(r => System.Text.RegularExpressions.Regex.IsMatch(r.Id, "^[a-z0-9-]{3,64}$"));
        Enum.GetValues<ReportCategory>().ShouldAllBe(c => Reports.Any(r => r.Category == c));
    }

    [Theory]
    [InlineData("op-census")]
    [InlineData("op-hourly-footfall")]
    [InlineData("appointment-no-show")]
    [InlineData("bed-occupancy")]
    [InlineData("alos")]
    [InlineData("bed-turnover")]
    [InlineData("ward-census")]
    [InlineData("discharge-outcomes")]
    [InlineData("revenue-by-department")]
    [InlineData("revenue-by-doctor")]
    [InlineData("revenue-by-service")]
    [InlineData("revenue-by-payer")]
    [InlineData("collections-by-mode")]
    [InlineData("outstanding-ageing")]
    [InlineData("discounts")]
    [InlineData("refunds")]
    [InlineData("doctor-fee-summary")]
    [InlineData("lab-volume-tat")]
    [InlineData("radiology-volume-tat")]
    [InlineData("critical-values")]
    [InlineData("pharmacy-sales-margin")]
    [InlineData("near-expiry-stock")]
    [InlineData("dead-stock")]
    [InlineData("purchase-vs-consumption")]
    [InlineData("ot-utilisation")]
    [InlineData("dialysis-sessions")]
    [InlineData("blood-bank-stock")]
    [InlineData("incident-rates")]
    [InlineData("capa-closure")]
    [InlineData("nabh-indicators")]
    [InlineData("feedback-nps")]
    [InlineData("hr-headcount")]
    [InlineData("hr-attendance")]
    public void Hospital_reports_are_in_the_catalogue(string id) => new ReportCatalog().Find(id).ShouldNotBeNull();

    [Fact]
    public void Every_report_is_complete_and_consistent()
    {
        foreach (var report in Reports)
        {
            report.Permissions.ShouldNotBeEmpty(report.Id);
            report.Permissions.ShouldAllBe(p => AnalyticsPermissions.AnyRead.Contains(p), report.Id);
            report.Columns.ShouldNotBeEmpty(report.Id);
            report.Columns.Select(c => c.Key).ShouldBeUnique(report.Id);
            report.Parameters.Select(p => p.Key).ShouldBeUnique(report.Id);
            report.Parameters.Count(p => p.Kind == ReportParameterKind.Facility).ShouldBe(1, report.Id);
            report.Parameters.Where(p => p.Kind == ReportParameterKind.Choice).ShouldAllBe(p => p.Choices != null && p.Choices.Count > 0, report.Id);
            report.Description.ShouldNotBeNullOrWhiteSpace(report.Id);
            report.Definition.ShouldNotBeNullOrWhiteSpace(report.Id);
            if (report.DefaultSort is { } sort)
            {
                report.Columns.ShouldContain(c => c.Key == sort, report.Id);
            }

            if (report.Chart is { } chart)
            {
                report.Columns.ShouldContain(c => c.Key == chart.CategoryKey, report.Id);
                chart.SeriesKeys.ShouldAllBe(k => report.Columns.Any(c => c.Key == k && c.Type != ReportValueType.Text), report.Id);
            }
        }
    }

    [Fact]
    public void Patient_level_reports_need_clinical_access_and_are_never_scheduled()
    {
        var patientLevel = Reports.Where(r => r.ContainsPatientIdentifiers).ToList();

        patientLevel.Select(r => r.Id).ShouldBe(["adverse-discharges", "critical-values"], ignoreOrder: true);
        patientLevel.ShouldAllBe(r => r.Permissions.SequenceEqual(new[] { AnalyticsPermissions.ClinicalRead }));
        patientLevel.ShouldAllBe(r => !r.CanSchedule);
        patientLevel.ShouldAllBe(r => r.Columns.Any(c => c.IsPatientIdentifier));
        Reports.Where(r => !r.ContainsPatientIdentifiers).ShouldAllBe(r => r.Columns.All(c => !c.IsPatientIdentifier));
    }

    [Fact]
    public void Visibility_follows_the_permission_matrix()
    {
        var catalog = new ReportCatalog();
        static ReportActor Actor(params string[] permissions) => new(Guid.NewGuid(), "u", "User", Guid.NewGuid(), permissions.ToHashSet());

        var mis = catalog.VisibleTo(Actor(AnalyticsPermissions.MisRead)).Select(r => r.Descriptor).ToList();
        var finance = catalog.VisibleTo(Actor(AnalyticsPermissions.FinanceRead)).Select(r => r.Descriptor).ToList();
        var clinical = catalog.VisibleTo(Actor(AnalyticsPermissions.ClinicalRead)).Select(r => r.Descriptor).ToList();

        mis.ShouldNotContain(r => r.Category == ReportCategory.Revenue);
        mis.ShouldNotContain(r => r.ContainsPatientIdentifiers);
        mis.ShouldContain(r => r.Id == "hr-headcount");
        finance.ShouldAllBe(r => r.Category == ReportCategory.Revenue || r.Category == ReportCategory.Pharmacy);
        finance.ShouldContain(r => r.Id == "pharmacy-sales-margin");
        clinical.ShouldContain(r => r.Id == "critical-values");
        clinical.ShouldContain(r => r.Id == "discharge-outcomes");
        clinical.ShouldNotContain(r => r.Id == "hr-headcount");
        catalog.VisibleTo(Actor("*")).Count().ShouldBe(Reports.Count);
        catalog.VisibleTo(Actor("lab.results.verify")).ShouldBeEmpty();
    }

    [Fact]
    public void Default_permissions_match_the_brief()
    {
        var definitions = AnalyticsPermissions.All.ToDictionary(p => p.Name);

        definitions[AnalyticsPermissions.MisRead].DefaultRoles.ShouldBe(["MisAnalyst", "HospitalAdmin", "Accountant"], ignoreOrder: true);
        definitions[AnalyticsPermissions.FinanceRead].DefaultRoles.ShouldBe(["Accountant", "HospitalAdmin"], ignoreOrder: true);
        definitions[AnalyticsPermissions.ClinicalRead].DefaultRoles.ShouldBe(["HospitalAdmin", "QualityManager", "MrdOfficer"], ignoreOrder: true);
        definitions[AnalyticsPermissions.DashboardsManage].DefaultRoles.ShouldBe(["HospitalAdmin", "MisAnalyst"], ignoreOrder: true);
        definitions[AnalyticsPermissions.SchedulesManage].DefaultRoles.ShouldBe(["HospitalAdmin", "MisAnalyst"], ignoreOrder: true);
    }

    [Fact]
    public void Dashboard_layouts_round_trip_and_tolerate_bad_json()
    {
        var widgets = new List<DashboardWidget>
        {
            new("w1", "op-census", "OPD", ReportChartType.StackedBar, 2, new Dictionary<string, string> { [ReportKeys.Period] = ReportPeriods.Last30Days }),
            new("w2", "bed-occupancy", null, ReportChartType.Summary, 1, new Dictionary<string, string>()),
        };

        var json = DashboardLayout.Serialize(widgets);
        var back = DashboardLayout.Deserialize(json);

        json.ShouldContain("\"view\":\"StackedBar\"");
        back.Count.ShouldBe(2);
        back[0].Parameters[ReportKeys.Period].ShouldBe(ReportPeriods.Last30Days);
        back[1].View.ShouldBe(ReportChartType.Summary);
        DashboardLayout.Deserialize("{not json").ShouldBeEmpty();
        DashboardLayout.Deserialize(null).ShouldBeEmpty();
    }

    [Fact]
    public void Dashboard_visibility_and_editing_rules()
    {
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var personal = new Dashboard { OwnerUserId = owner };
        var shared = new Dashboard { OwnerUserId = owner, IsShared = true };
        var template = new Dashboard { OwnerUserId = null, IsShared = true };

        personal.IsVisibleTo(owner).ShouldBeTrue();
        personal.IsVisibleTo(other).ShouldBeFalse();
        shared.IsVisibleTo(other).ShouldBeTrue();
        shared.IsEditableBy(other, canManage: true).ShouldBeFalse();
        shared.IsEditableBy(owner, canManage: true).ShouldBeTrue();
        shared.IsEditableBy(owner, canManage: false).ShouldBeFalse();
        template.IsEditableBy(other, canManage: true).ShouldBeTrue();
    }
}
