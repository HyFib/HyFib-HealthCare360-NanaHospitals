using HyFib.HealthCare360.Modules.Clinical;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.SupplyChain;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

/// <summary>
/// The ward round's queries translate to PostgreSQL (checked with the Npgsql provider, without a connection), so a
/// change EF cannot translate fails here and not on the morning round.
/// </summary>
public sealed class WardRoundQueryTests : IDisposable
{
    private readonly HospitalDbContext _db;

    public WardRoundQueryTests()
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options;
        _db = new HospitalDbContext(options, new ModuleCatalog([new PatientsModule(), new SupplyChainModule(), new ClinicalModule(), new InpatientModule()]));
    }

    public void Dispose() => _db.Dispose();

    private WardRoundQueries Queries => new(_db, Substitute.For<IPermissionService>(), Substitute.For<ICurrentUser>(), TimeProvider.System);

    [Fact]
    public void A_doctors_round_covers_their_admissions_and_the_ones_they_visited()
    {
        var sql = Queries.Admissions(Guid.CreateVersion7(), null).ToQueryString();

        sql.ShouldContain("inpatient.admissions");
        sql.ShouldContain("'Admitted'", Case.Sensitive, "Enums are compared as their stored text.");
        sql.ShouldContain("'DischargeInitiated'");
        sql.ShouldContain("consultant_practitioner_id");
        sql.ShouldContain("inpatient.consultant_visits");
        sql.ShouldContain("clinical.allergies");
        sql.ShouldContain("LEFT JOIN");
        sql.ShouldContain("is_deleted");
    }

    [Fact]
    public void A_departments_round_translates() =>
        Queries.Admissions(null, Guid.CreateVersion7()).ToQueryString().ShouldContain("department_id");

    [Fact]
    public void Only_each_patients_latest_vital_signs_are_read()
    {
        var sql = Queries.LatestVitals([Guid.CreateVersion7(), Guid.CreateVersion7()], DateTimeOffset.UtcNow.AddDays(-3)).ToQueryString();

        sql.ShouldContain("clinical.vital_signs");
        sql.ShouldContain("ROW_NUMBER() OVER(PARTITION BY");
        sql.ShouldContain("is_entered_in_error");
    }
}
