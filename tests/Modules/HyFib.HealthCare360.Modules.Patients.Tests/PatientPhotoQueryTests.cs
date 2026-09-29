using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// The photo ownership check translates to PostgreSQL (checked with the Npgsql provider, without a connection), so a
/// change that EF cannot translate fails here and not on the first patient saved with a photo.
/// </summary>
public sealed class PatientPhotoQueryTests : IDisposable
{
    private readonly HospitalDbContext _db;

    public PatientPhotoQueryTests()
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options;
        _db = new HospitalDbContext(options, new ModuleCatalog([new PatientsModule()]));
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void A_photo_is_matched_by_file_id_and_skips_the_patient_being_saved()
    {
        var sql = PatientPhoto.UsedByOthers(_db, Guid.CreateVersion7(), exceptPatientId: Guid.CreateVersion7()).ToQueryString();

        sql.ShouldContain("patients.patients");
        sql.ShouldContain("photo_file_id");
        sql.ShouldContain("<>");
        sql.ShouldContain("is_deleted");
    }

    [Fact]
    public void A_new_patient_is_matched_against_every_patient()
    {
        var sql = PatientPhoto.UsedByOthers(_db, Guid.CreateVersion7(), exceptPatientId: null).ToQueryString();

        sql.ShouldContain("patients.patients");
        sql.ShouldContain("photo_file_id");
    }
}
