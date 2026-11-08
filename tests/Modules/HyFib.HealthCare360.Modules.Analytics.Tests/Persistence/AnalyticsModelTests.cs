using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.Persistence;

/// <summary>Builds the real EF Core model (no database) and pins the analytics tables before migrations are generated.</summary>
public sealed class AnalyticsModelTests
{
    private static readonly Lazy<IModel> Model = new(() =>
    {
        using var db = ReportTestHarness.CreateContext(null);
        return db.GetService<IDesignTimeModel>().Model;
    });

    [Fact]
    public void Analytics_tables_live_in_the_analytics_schema()
    {
        var entities = Model.Value.GetEntityTypes()
            .Where(e => e.ClrType.Namespace?.StartsWith("HyFib.HealthCare360.Modules.Analytics", StringComparison.Ordinal) == true)
            .ToList();

        entities.Select(e => e.GetTableName()).ShouldBe(["dashboards", "report_schedules", "report_deliveries", "report_delivery_recipients", "kpi_targets"], ignoreOrder: true);
        entities.ShouldAllBe(e => e.GetSchema() == "analytics");
    }

    [Fact]
    public void Schedules_and_deliveries_are_facility_scoped_and_indexed_for_the_job()
    {
        var schedule = Model.Value.FindEntityType(typeof(ReportSchedule))!;
        var delivery = Model.Value.FindEntityType(typeof(ReportDelivery))!;

        schedule.GetIndexes().ShouldContain(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(ReportSchedule.IsActive), nameof(ReportSchedule.NextRunAt) }));
        schedule.FindProperty(nameof(ReportSchedule.ParametersJson))!.GetColumnType().ShouldBe("jsonb");
        schedule.FindProperty(nameof(ReportSchedule.Recipients))!.IsPrimitiveCollection.ShouldBeTrue();
        schedule.FindProperty(nameof(ReportSchedule.Version))!.IsConcurrencyToken.ShouldBeTrue();
        schedule.GetDeclaredQueryFilters().Select(f => f.Key).ShouldContain(Platform.Persistence.HospitalDbContext.FacilityFilter);
        delivery.GetDeclaredQueryFilters().Select(f => f.Key).ShouldContain(Platform.Persistence.HospitalDbContext.FacilityFilter);
        delivery.GetIndexes().ShouldContain(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(ReportDelivery.CreatedAt));
        Model.Value.FindEntityType(typeof(Dashboard))!.FindProperty(nameof(Dashboard.LayoutJson))!.GetColumnType().ShouldBe("jsonb");
    }

    [Fact]
    public void Two_extracts_of_a_facility_never_write_the_same_file()
    {
        var schedule = Model.Value.FindEntityType(typeof(ReportSchedule))!;

        var index = schedule.GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(ReportSchedule.FacilityId), nameof(ReportSchedule.ExtractName) }));
        index.IsUnique.ShouldBeTrue();
        index.GetFilter().ShouldBe("extract_name IS NOT NULL AND is_deleted = false");
        schedule.FindProperty(nameof(ReportSchedule.ExtractName))!.GetMaxLength().ShouldBe(ReportSchedule.MaxExtractNameLength);
        schedule.FindProperty(nameof(ReportSchedule.Destination))!.GetProviderClrType().ShouldBe(typeof(string));
    }

    [Fact]
    public void A_facility_has_one_target_per_kpi_and_its_status_is_stored_as_words()
    {
        var target = Model.Value.FindEntityType(typeof(KpiTarget))!;

        var index = target.GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(KpiTarget.FacilityId), nameof(KpiTarget.KpiKey) }));
        index.IsUnique.ShouldBeTrue();
        index.GetFilter().ShouldBe("is_deleted = false");
        target.GetDeclaredQueryFilters().Select(f => f.Key).ShouldContain(Platform.Persistence.HospitalDbContext.FacilityFilter);
        target.FindProperty(nameof(KpiTarget.LastStatus))!.GetProviderClrType().ShouldBe(typeof(string));
        target.FindProperty(nameof(KpiTarget.Target))!.GetPrecision().ShouldBe(18);
        target.FindProperty(nameof(KpiTarget.Version))!.IsConcurrencyToken.ShouldBeTrue();
    }

    [Fact]
    public void The_create_script_includes_the_analytics_schema()
    {
        using var db = ReportTestHarness.CreateContext(null);

        var script = db.Database.GenerateCreateScript();

        script.ShouldContain("CREATE TABLE analytics.dashboards");
        script.ShouldContain("CREATE TABLE analytics.report_schedules");
        script.ShouldContain("CREATE TABLE analytics.report_deliveries");
        script.ShouldContain("CREATE TABLE analytics.kpi_targets");
    }
}
