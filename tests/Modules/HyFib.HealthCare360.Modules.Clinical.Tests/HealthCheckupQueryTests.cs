using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// A check-up booking is completed or cancelled from whichever facility its doctor or desk works at, as it is found at
/// any facility; the health check-up visit its corporate check-in opened must be found the same way, or it stays open
/// for good. Checked on the SQL the Npgsql provider writes, without a connection.
/// </summary>
public sealed class HealthCheckupQueryTests : IDisposable
{
    private const string FacilityFilterParameter = "@ef_filter__";

    private readonly Platform.Persistence.HospitalDbContext _db = ClinicalModelTests.CreateContext();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void The_visit_a_check_in_opened_is_found_from_any_facility()
    {
        // The facility filter, as EF writes it on a plain query of visits (a parameter, as no user is signed in here).
        _db.Set<Visit>().ToQueryString().ShouldContain(FacilityFilterParameter);

        var sql = HealthCheckupService.OpenVisit(_db, Guid.CreateVersion7()).ToQueryString();

        sql.ShouldContain("patients.visits");
        sql.ShouldContain("'Open'", Case.Sensitive, "only a visit still open is closed with the check-up");
        sql.ShouldNotContain(FacilityFilterParameter, Case.Sensitive, "the signed-in user's facility does not narrow it");
    }
}
