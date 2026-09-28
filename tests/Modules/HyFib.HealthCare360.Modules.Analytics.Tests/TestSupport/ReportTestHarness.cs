using HyFib.HealthCare360.Modules.AI;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Modules.Analytics.Services;
using HyFib.HealthCare360.Modules.Billing;
using HyFib.HealthCare360.Modules.Clinical;
using HyFib.HealthCare360.Modules.Diagnostics;
using HyFib.HealthCare360.Modules.Engagement;
using HyFib.HealthCare360.Modules.HR;
using HyFib.HealthCare360.Modules.Inpatient;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Pharmacy;
using HyFib.HealthCare360.Modules.Quality;
using HyFib.HealthCare360.Modules.Scheduling;
using HyFib.HealthCare360.Modules.SpecialtyCare;
using HyFib.HealthCare360.Modules.SupplyChain;
using HyFib.HealthCare360.Modules.SupportServices;
using HyFib.HealthCare360.Modules.VirtualCare;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Modules.Analytics.Tests.TestSupport;

/// <summary>
/// Builds the real, complete EF Core model of every module (no database needed) so report queries can be compiled to
/// SQL with <c>ToQueryString()</c>.
/// </summary>
internal static class ReportTestHarness
{
    /// <summary>15 Sep 2026, 12:00 IST.</summary>
    public static readonly DateTimeOffset Now = new(2026, 9, 15, 6, 30, 0, TimeSpan.Zero);

    public static readonly Guid FacilityId = Guid.Parse("0197f5a2-0000-7000-8000-000000000001");

    private static readonly Lazy<ModuleCatalog> Modules = new(() => new ModuleCatalog(
    [
        new PatientsModule(), new SchedulingModule(), new ClinicalModule(), new DiagnosticsModule(), new SupplyChainModule(), new PharmacyModule(),
        new InpatientModule(), new VirtualCareModule(), new SpecialtyCareModule(), new SupportServicesModule(), new BillingModule(), new HRModule(),
        new EngagementModule(), new QualityModule(), new AIModule(), new AnalyticsModule(),
    ]));

    private static readonly Lazy<DbContextOptions<HospitalDbContext>> Options = new(() => new DbContextOptionsBuilder<HospitalDbContext>()
        .UseNpgsql("Host=localhost;Database=model_only")
        .UseSnakeCaseNamingConvention()
        .Options);

    public static DateOnly Today => AnalyticsClock.LocalDate(Now);

    public static HospitalDbContext CreateContext(Guid? facilityId) => new(Options.Value, Modules.Value, new ReportingUser(facilityId));
}

/// <summary>Records the SQL of every report query instead of running it, and returns no rows.</summary>
internal sealed class SqlCapturingExecutor : IReportQueryExecutor
{
    public List<string> Sql { get; } = [];

    public Task<List<T>> ListAsync<T>(IQueryable<T> query, CancellationToken ct)
    {
        Sql.Add(query.ToQueryString());
        return Task.FromResult(new List<T>());
    }
}
