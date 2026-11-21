using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// An audit event is filed under the signed-in user's facility unless the entry names another: an event about a record of
/// another facility (a blood request held in a branch other than the officer's) is found by a view filtered to that facility.
/// Checked without a database: the change tracker holds the event a save would write.
/// </summary>
public sealed class AuditEntryFacilityTests
{
    private static readonly Guid UsersFacility = Guid.NewGuid();
    private static readonly Guid OtherFacility = Guid.NewGuid();

    [Fact]
    public void An_entry_is_filed_under_the_signed_in_users_facility_by_default()
    {
        var (audit, db) = Logger();
        using (db)
        {
            audit.Add(new AuditEntry(AuditCategories.Clinical, "test.action", "summary"));

            db.ChangeTracker.Entries<AuditEvent>().Single().Entity.FacilityId.ShouldBe(UsersFacility);
        }
    }

    [Fact]
    public void An_entry_that_names_a_facility_is_filed_under_it()
    {
        var (audit, db) = Logger();
        using (db)
        {
            audit.Add(new AuditEntry(AuditCategories.Clinical, "test.action", "summary") { FacilityId = OtherFacility });

            db.ChangeTracker.Entries<AuditEvent>().Single().Entity.FacilityId.ShouldBe(OtherFacility);
        }
    }

    private static (IAuditLogger Audit, HospitalDbContext Db) Logger()
    {
        var db = new HospitalDbContext(
            new DbContextOptionsBuilder<HospitalDbContext>().UseNpgsql("Host=localhost;Database=hc360_model_check").UseSnakeCaseNamingConvention().Options,
            new ModuleCatalog([]));
        var user = Substitute.For<ICurrentUser>();
        user.FacilityId.Returns(UsersFacility);
        return (new AuditLogger(db, null!, user, TimeProvider.System), db);
    }
}
