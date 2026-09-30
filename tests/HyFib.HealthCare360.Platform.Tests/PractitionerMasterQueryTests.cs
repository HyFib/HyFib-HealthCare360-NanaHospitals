using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The doctor master, GET /api/v1/masters/practitioners and /{id}. Both answered 500: they filtered and sorted the
/// projected <see cref="PractitionerDto"/> records, which EF cannot translate. The practitioners are now filtered, sorted
/// and paged in the database, and projected last.
/// </summary>
public sealed class PractitionerMasterQueryTests
{
    [Fact]
    public void One_doctor_is_read_by_id_in_the_database()
    {
        using var db = ModelOnlyContext();
        var id = Guid.NewGuid();

        var sql = OrganizationEndpoints.PractitionerQuery(db, db.Set<Practitioner>().Where(p => p.Id == id)).ToQueryString();

        sql.ShouldContain("FROM org.practitioners");
        sql.ShouldContain("id = @id");
    }

    [Fact]
    public void The_doctor_list_is_filtered_searched_sorted_and_paged_in_the_database()
    {
        using var db = ModelOnlyContext();
        var practitioners = OrganizationEndpoints.FilterPractitioners(
            db.Set<Practitioner>(), Guid.NewGuid(), Guid.NewGuid(), PractitionerKind.Consultant, active: true, searchTerm: "mehta");

        var sql = OrganizationEndpoints.PractitionerQuery(db, practitioners).Skip(25).Take(25).ToQueryString();

        sql.ShouldContain("department_id = @departmentId");
        sql.ShouldContain("specialty_id = @specialtyId");
        sql.ShouldContain("kind = @kind");
        sql.ShouldContain("is_active = @active");
        sql.ShouldContain("display_name ILIKE");
        sql.ShouldContain("ORDER BY");
        sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..].ShouldContain("display_name");
        sql.ShouldContain("LIMIT");
    }

    /// <summary>The platform's EF model with the Npgsql provider; queries are translated, never run.</summary>
    private static HospitalDbContext ModelOnlyContext() => new(
        new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options,
        new ModuleCatalog([]));
}
