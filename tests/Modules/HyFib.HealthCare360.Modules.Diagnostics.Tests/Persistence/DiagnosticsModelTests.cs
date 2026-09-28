using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Persistence;

/// <summary>
/// Builds the real EF Core model (no database needed) so mapping mistakes surface before migrations are generated,
/// and pins the persistence rules the workflow depends on: idempotency keys, four-decimal lab values and inline
/// cardiology measurements.
/// </summary>
public sealed class DiagnosticsModelTests
{
    private static readonly Lazy<IModel> Model = new(() => Build(db => db.GetService<IDesignTimeModel>().Model));

    private static readonly string[] Tables =
    [
        "lab_tests", "lab_test_components", "lab_reference_ranges", "lab_instruments", "referral_labs", "lab_orders",
        "lab_order_items", "lab_samples", "lab_results", "lab_result_susceptibilities", "lab_reports", "lab_report_amendments",
        "critical_call_logs", "qc_lots", "qc_runs", "qc_overrides", "imaging_studies", "imaging_repeats", "radiology_reports",
        "radiology_addenda", "report_templates", "imaging_procedures", "imaging_ai_findings", "cardiology_studies",
        "cardiology_reports",
    ];

    [Fact]
    public void Every_module_table_lives_in_the_diagnostics_schema()
    {
        var entities = Model.Value.GetEntityTypes()
            .Where(e => e.ClrType.Namespace?.StartsWith("HyFib.HealthCare360.Modules.Diagnostics", StringComparison.Ordinal) == true)
            .ToList();

        entities.Select(e => e.GetTableName()).ShouldBe(Tables, ignoreOrder: true);
        entities.ShouldAllBe(e => e.GetSchema() == "diagnostics");
    }

    [Fact]
    public void Measured_values_keep_four_decimals_while_money_style_rounding_is_not_applied()
    {
        Scale<LabResult>(nameof(LabResult.NumericValue)).ShouldBe(4);
        Scale<LabResult>(nameof(LabResult.PreviousValue)).ShouldBe(4);
        Scale<ReferenceRange>(nameof(ReferenceRange.Low)).ShouldBe(4);
        Scale<ReferenceRange>(nameof(ReferenceRange.CriticalHigh)).ShouldBe(4);
        Scale<QcLot>(nameof(QcLot.TargetSd)).ShouldBe(4);
        Scale<QcRun>(nameof(QcRun.Value)).ShouldBe(4);
        Scale<QcRun>(nameof(QcRun.ZScore)).ShouldBe(3);
    }

    [Theory]
    [InlineData(typeof(LabOrderItem), nameof(LabOrderItem.SourceOrderItemId))]
    [InlineData(typeof(ImagingStudy), nameof(ImagingStudy.SourceOrderItemId))]
    [InlineData(typeof(CardiologyStudy), nameof(CardiologyStudy.SourceOrderItemId))]
    [InlineData(typeof(LabOrder), nameof(LabOrder.AccessionNumber))]
    [InlineData(typeof(ImagingStudy), nameof(ImagingStudy.AccessionNumber))]
    [InlineData(typeof(LabSample), nameof(LabSample.Barcode))]
    [InlineData(typeof(LabReport), nameof(LabReport.LabOrderId))]
    [InlineData(typeof(LabReport), nameof(LabReport.VerificationCode))]
    [InlineData(typeof(RadiologyReport), nameof(RadiologyReport.VerificationCode))]
    [InlineData(typeof(CardiologyReport), nameof(CardiologyReport.VerificationCode))]
    public void Idempotency_keys_accession_numbers_barcodes_and_QR_codes_are_unique(Type entity, string property) =>
        Model.Value.FindEntityType(entity)!.GetIndexes()
            .ShouldContain(i => i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == property);

    [Fact]
    public void Cardiology_measurements_are_stored_as_columns_for_trend_queries()
    {
        var report = Model.Value.FindEntityType(typeof(CardiologyReport))!;

        report.GetComplexProperties().Select(p => p.Name).ShouldBe(["Ecg", "Echo", "Tmt", "Holter", "Abpm"], ignoreOrder: true);
        var lvef = report.FindComplexProperty(nameof(CardiologyReport.Echo))!.ComplexType.FindProperty(nameof(EchoFindings.Lvef))!;
        lvef.GetColumnName().ShouldBe("echo_lvef");
        lvef.GetScale().ShouldBe(1);
    }

    [Fact]
    public void Answer_lists_and_device_files_are_stored_as_arrays_not_extra_tables()
    {
        Model.Value.FindEntityType(typeof(LabTest))!.FindProperty(nameof(LabTest.Options))!.IsPrimitiveCollection.ShouldBeTrue();
        Model.Value.FindEntityType(typeof(ReferenceRange))!.FindProperty(nameof(ReferenceRange.CriticalValues))!.IsPrimitiveCollection.ShouldBeTrue();
        Model.Value.FindEntityType(typeof(CardiologyStudy))!.FindProperty(nameof(CardiologyStudy.DeviceFileIds))!.IsPrimitiveCollection.ShouldBeTrue();
        Model.Value.FindEntityType(typeof(QcRun))!.FindProperty(nameof(QcRun.Violations))!.IsPrimitiveCollection.ShouldBeTrue();
    }

    [Fact]
    public void Workflow_rows_use_optimistic_concurrency()
    {
        foreach (var type in new[] { typeof(LabResult), typeof(LabSample), typeof(LabReport), typeof(ImagingStudy), typeof(CriticalCallLog) })
        {
            Model.Value.FindEntityType(type)!.FindProperty(nameof(AuditableEntity.Version))!.IsConcurrencyToken.ShouldBeTrue(type.Name);
        }
    }

    [Fact]
    public void Statuses_are_stored_as_readable_strings() =>
        Model.Value.FindEntityType(typeof(LabSample))!.FindProperty(nameof(LabSample.Status))!.GetProviderClrType().ShouldBe(typeof(string));

    [Fact]
    public void Worklists_rank_STAT_first_in_SQL_instead_of_sorting_the_priority_text()
    {
        var sql = Build(db => db.Set<LabSample>().OrderByUrgency(s => s.Priority).ThenBy(s => s.CreatedAt).ToQueryString());

        sql.ShouldContain("'Stat'");
        sql.ShouldContain("'Urgent'");
    }

    [Fact]
    public void Package_components_can_be_listed_per_booking()
    {
        foreach (var type in new[] { typeof(LabOrderItem), typeof(ImagingStudy), typeof(CardiologyStudy) })
        {
            var entity = Model.Value.FindEntityType(type)!;
            entity.GetIndexes().ShouldContain(i => i.Properties.Count == 1 && i.Properties[0].Name == "PackageBookingId", type.Name);
            entity.FindProperty("IsPackageComponent").ShouldBeNull(type.Name);
        }
    }

    [Fact]
    public void The_database_script_can_be_generated_for_the_whole_schema()
    {
        var script = Build(db => db.Database.GenerateCreateScript());

        script.ShouldContain("nspname = 'diagnostics'");
        script.ShouldContain("CREATE TABLE diagnostics.lab_results");
        script.ShouldContain("CREATE TABLE diagnostics.cardiology_reports");
    }

    private static int? Scale<T>(string property) => Model.Value.FindEntityType(typeof(T))!.FindProperty(property)!.GetScale();

    private static TResult Build<TResult>(Func<HospitalDbContext, TResult> use)
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new HospitalDbContext(options, new ModuleCatalog([new PatientsModule(), new DiagnosticsModule()]));
        return use(db);
    }
}
