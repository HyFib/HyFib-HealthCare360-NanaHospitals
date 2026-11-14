using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// How a verified mobile number and the portal's confirmations are stored, from the real EF Core model (no database needed),
/// as the hand-written migration <c>20261114110000_PatientPhoneVerification</c> creates them.
/// </summary>
public sealed class PhoneVerificationModelTests
{
    private static readonly Lazy<IModel> Model = new(() => Build(db => db.GetService<IDesignTimeModel>().Model));

    [Fact]
    public void The_patient_keeps_when_its_number_was_verified_and_which_number()
    {
        var patient = Model.Value.FindEntityType(typeof(Patient))!;

        var at = patient.FindProperty(nameof(Patient.PhoneVerifiedAt))!;
        (at.GetColumnName(), at.GetColumnType(), at.IsNullable).ShouldBe(("phone_verified_at", "timestamp with time zone", true));
        var number = patient.FindProperty(nameof(Patient.PhoneVerifiedNumber))!;
        (number.GetColumnName(), number.GetMaxLength(), number.IsNullable).ShouldBe(("phone_verified_number", 20, true));
        patient.FindProperty(nameof(Patient.IsPhoneVerified)).ShouldBeNull("worked out from the two, never stored");
    }

    [Fact]
    public void A_code_says_what_it_is_for()
    {
        var purpose = Model.Value.FindEntityType(typeof(PatientPhoneChange))!.FindProperty(nameof(PatientPhoneChange.Purpose))!;

        (purpose.GetColumnName(), purpose.GetMaxLength(), purpose.IsNullable).ShouldBe(("purpose", 64, false));
    }

    [Fact]
    public void The_portals_tries_are_counted_by_number_without_reading_the_whole_table()
    {
        var tries = Model.Value.FindEntityType(typeof(PortalRecordConfirmation))!;

        (tries.GetSchema(), tries.GetTableName()).ShouldBe(("patients", "portal_record_confirmations"));
        tries.GetIndexes().ShouldContain(i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "AccountPhone", "CreatedAt" }));
        tries.GetProperties().ShouldNotContain(p => p.GetColumnName().EndsWith("patient_id", StringComparison.Ordinal),
            "a try names no patient: a wrong one matched none");
    }

    [Fact]
    public void The_migration_creates_what_the_model_has()
    {
        var script = Build(db => db.Database.GenerateCreateScript());

        var patients = Table(script, "patients.patients");
        patients.ShouldContain("phone_verified_at timestamp with time zone,");
        patients.ShouldContain("phone_verified_number character varying(20),");
        Table(script, "patients.patient_phone_changes").ShouldContain("purpose character varying(64) NOT NULL,");
        var tries = Table(script, "patients.portal_record_confirmations");
        tries.ShouldContain("account_phone character varying(20) NOT NULL,");
        tries.ShouldContain("method character varying(64) NOT NULL,");
        tries.ShouldContain("confirmed integer NOT NULL,");
        script.ShouldContain("CREATE INDEX ix_portal_record_confirmations_account_phone_created_at ON patients.portal_record_confirmations (account_phone, created_at);");
    }

    /// <summary>The columns of one table as the create script declares them.</summary>
    private static string Table(string script, string name)
    {
        var start = script.IndexOf($"CREATE TABLE {name} (", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"{name} is not created");
        return script[start..script.IndexOf(");", start, StringComparison.Ordinal)];
    }

    private static TResult Build<TResult>(Func<HospitalDbContext, TResult> use)
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new HospitalDbContext(options, new ModuleCatalog([new PatientsModule()]));
        return use(db);
    }
}
