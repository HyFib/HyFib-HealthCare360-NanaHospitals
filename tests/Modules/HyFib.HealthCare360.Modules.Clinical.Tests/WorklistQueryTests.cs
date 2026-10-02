using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// The worklist's appointments query translates to PostgreSQL (checked with the Npgsql provider, without a connection),
/// so a change EF cannot translate fails here and not on a doctor's first morning.
/// </summary>
public sealed class WorklistQueryTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 10, 2);
    private readonly Platform.Persistence.HospitalDbContext _db = ClinicalModelTests.CreateContext();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void A_doctors_appointments_translate_with_their_queue_token_and_note()
    {
        var sql = new WorklistQueries(_db, Substitute.For<IPermissionService>(), Substitute.For<ICurrentUser>(), TimeProvider.System).Appointments(Day, Guid.CreateVersion7(), null).ToQueryString();

        sql.ShouldContain("scheduling.appointments");
        sql.ShouldContain("practitioner_id");
        sql.ShouldContain("'Cancelled'", Case.Sensitive, "Enums are compared as their stored text.");
        sql.ShouldContain("'Rescheduled'");
        sql.ShouldContain("patients.queue_tokens");
        sql.ShouldContain("clinical.encounters");
        sql.ShouldContain("is_deleted");
    }

    [Fact]
    public void A_departments_appointments_translate()
    {
        var sql = new WorklistQueries(_db, Substitute.For<IPermissionService>(), Substitute.For<ICurrentUser>(), TimeProvider.System).Appointments(Day, null, Guid.CreateVersion7()).ToQueryString();

        sql.ShouldContain("department_id");
        sql.ShouldContain("LEFT JOIN");
    }
}
