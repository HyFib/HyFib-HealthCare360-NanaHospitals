using HyFib.HealthCare360.Modules.Clinical;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.SupplyChain;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

/// <summary>
/// The medication chart's queries translate to PostgreSQL (checked with the Npgsql provider, without a connection), so a
/// change EF cannot translate fails here and not in the job that keeps the ward's rounds charted, or in the prescribing
/// safety check that reads the ward's orders.
/// </summary>
public sealed class MarQueryTests : IDisposable
{
    private readonly HospitalDbContext _db;

    public MarQueryTests()
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options;
        _db = new HospitalDbContext(options, new ModuleCatalog([new PatientsModule(), new SupplyChainModule(), new ClinicalModule(), new InpatientModule()]));
    }

    public void Dispose() => _db.Dispose();

    private MarService Mar => new(_db, Substitute.For<ISettingsService>(), Substitute.For<ICurrentUser>(), Substitute.For<IWitnessConfirmations>(),
        Substitute.For<IPermissionService>(), Substitute.For<INotificationService>(), Substitute.For<IAuditLogger>(), TimeProvider.System,
        Substitute.For<IIncidentRegister>(), Substitute.For<IMedicationSafetyCheck>());

    [Fact]
    public void The_orders_kept_charted_ahead_are_read_with_whether_each_has_a_dose_for_patients_still_in_a_bed()
    {
        var sql = Mar.ChartAheadCandidates(DateTimeOffset.UtcNow).ToQueryString();

        sql.ShouldContain("inpatient.medication_orders");
        sql.ShouldContain("'Active'", Case.Sensitive, "Enums are compared as their stored text.");
        sql.ShouldContain("stop_requested_at IS NULL");
        sql.ShouldContain("inpatient.admissions");
        sql.ShouldContain("'Admitted'");
        sql.ShouldContain("'DischargeInitiated'");
        sql.ShouldContain("EXISTS");
        sql.ShouldContain("inpatient.medication_administrations");
        sql.ShouldContain("is_deleted");
    }

    [Fact]
    public void The_ward_medicines_a_safety_check_counts_are_the_active_chart_lines_no_prescription_carries_in_any_facility()
    {
        // Read by a user working in one facility: the patient takes every facility's medicines all the same.
        var user = Substitute.For<ICurrentUser>();
        user.FacilityId.Returns(Guid.CreateVersion7());
        using var db = new HospitalDbContext(new DbContextOptionsBuilder<HospitalDbContext>()
                .UseNpgsql("Host=localhost;Database=hc360_model_check")
                .UseSnakeCaseNamingConvention()
                .Options,
            new ModuleCatalog([new PatientsModule(), new SupplyChainModule(), new ClinicalModule(), new InpatientModule()]), user);
        var sql = new WardMedicines(db, TimeProvider.System).Active(Guid.CreateVersion7(), DateTimeOffset.UtcNow).ToQueryString();

        sql.ShouldContain("inpatient.medication_orders");
        sql.ShouldContain("'Active'");
        sql.ShouldContain("'Prescription'", Case.Sensitive, "an e-prescription's lines are counted from the prescription");
        sql.ShouldContain("inpatient.admissions");
        sql.ShouldContain("'DischargeInitiated'");
        sql.ShouldNotContain("facility_id", Case.Sensitive, "the patient takes every facility's medicines");
        sql.ShouldContain("is_deleted");
    }
}
