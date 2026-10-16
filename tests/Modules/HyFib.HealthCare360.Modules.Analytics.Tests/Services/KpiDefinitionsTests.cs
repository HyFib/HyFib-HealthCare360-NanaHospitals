using HyFib.HealthCare360.Modules.Analytics.Features.Reports;
using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Modules.Analytics.Services;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Services;

/// <summary>The KPI definitions (BI-01..05): one list for the dashboard and the catalogue, each drilling into a real report.</summary>
public sealed class KpiDefinitionsTests
{
    private static readonly ReportCatalog Catalog = new();

    private static ReportActor Actor(params string[] permissions) => new(Guid.NewGuid(), "u", "User", Guid.NewGuid(), permissions.ToHashSet());

    [Fact]
    public void Every_kpi_is_defined_once_and_drills_into_a_catalogue_report_of_its_own_audience()
    {
        KpiDefinitions.All.Select(k => k.Key).ShouldBeUnique();
        KpiDefinitions.All.Select(k => k.Label).ShouldBeUnique();
        KpiDefinitions.All.ShouldAllBe(k => k.Formula.Length > 20 && k.Source.Length > 0);
        KpiDefinitions.All.ShouldAllBe(k => new[] { "count", "percent", "days", "minutes", "score", "inr" }.Contains(k.Unit));
        foreach (var kpi in KpiDefinitions.All)
        {
            var report = Catalog.Find(kpi.ReportId).ShouldNotBeNull($"{kpi.Key} drills into a missing report.").Descriptor;
            var audience = kpi.Audience == KpiAudience.Finance ? AnalyticsPermissions.FinanceRead : AnalyticsPermissions.MisRead;
            ReportCatalog.CanRead(report, Actor(audience)).ShouldBeTrue($"Whoever sees {kpi.Key} can open its report.");
        }
    }

    [Fact]
    public void An_operations_reader_sees_the_operational_kpis_and_no_money()
    {
        var catalogue = ReportEndpoints.DescribeDefinitions(Catalog, Actor(AnalyticsPermissions.MisRead));

        catalogue.Kpis.Select(k => k.Key).ShouldBe(["opVisits", "admissions", "occupancy", "alos", "labTat", "nps"]);
        catalogue.Kpis.ShouldAllBe(k => k.ReportTitle != null);
        catalogue.Reports.ShouldNotContain(r => r.Id == "daily-revenue");
        catalogue.Reports.Single(r => r.Id == "bed-occupancy").KpiKeys.ShouldBe(["occupancy"]);
        catalogue.Reports.Single(r => r.Id == "op-census").KpiKeys.ShouldBeEmpty();
    }

    [Fact]
    public void A_finance_reader_sees_revenue_and_collections_with_their_reports()
    {
        var catalogue = ReportEndpoints.DescribeDefinitions(Catalog, Actor(AnalyticsPermissions.FinanceRead));

        catalogue.Kpis.Select(k => k.Key).ShouldBe(["revenue", "collections"]);
        var revenue = catalogue.Kpis[0];
        revenue.Formula.ShouldBe(KpiDefinitions.Revenue.Formula);
        revenue.ReportTitle.ShouldBe(Catalog.Find("daily-revenue")!.Descriptor.Title);
        catalogue.Reports.Single(r => r.Id == "collections-by-mode").KpiKeys.ShouldBe(["collections"]);
    }

    [Fact]
    public void Lists_the_reports_in_catalogue_order_and_every_kpi_for_a_super_user()
    {
        var catalogue = ReportEndpoints.DescribeDefinitions(Catalog, Actor("*"));

        catalogue.Kpis.Count.ShouldBe(KpiDefinitions.All.Count);
        catalogue.Reports.Select(r => r.Id).ShouldBe(Catalog.All.Select(r => r.Descriptor.Id));
        catalogue.Reports.ShouldAllBe(r => r.Definition.Length > 0 && r.CategoryLabel.Length > 0);
    }
}
