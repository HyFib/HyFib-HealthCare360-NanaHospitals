using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.Extensions.Configuration;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The server reaches a PostgreSQL on another machine only over TLS that checks the server's certificate (ASVS V9.2.2):
/// otherwise it does not start, and says what to set, unless a lab turns the rule off.
/// </summary>
public sealed class DatabaseTlsTests
{
    [Theory]
    [InlineData("Host=localhost;Database=hc360;Username=hc360_app")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=hc360")]
    [InlineData("Host=127.0.0.5;Database=hc360")]
    [InlineData("Host=::1;Database=hc360")]
    [InlineData("Host=[::1]:5433;Database=hc360")]
    [InlineData("Host=/var/run/postgresql;Database=hc360")]
    [InlineData("Database=hc360;Username=hc360_app")]
    [InlineData("Host=db.hospital.lan;Database=hc360;SSL Mode=VerifyFull")]
    [InlineData("Host=db.hospital.lan;Database=hc360;SSL Mode=Require;Root Certificate=C:\\certs\\hospital-ca.crt")]
    [InlineData("Host=db.hospital.lan;Database=hc360;SSL Mode=VerifyCA;Root Certificate=hospital-ca.crt")]
    [InlineData("Host=localhost,db2.hospital.lan;Database=hc360;SSL Mode=VerifyFull")]
    public void This_machine_or_a_checked_certificate_is_allowed(string connectionString) =>
        DatabaseTls.Problem("Hospital", connectionString).ShouldBeNull();

    [Theory]
    [InlineData("Host=db.hospital.lan;Database=hc360", "Prefer")]
    [InlineData("Host=10.0.0.5;Database=hc360;SSL Mode=Disable", "Disable")]
    [InlineData("Host=db.hospital.lan;Database=hc360;SSL Mode=Require", "Require")]
    [InlineData("Host=db.hospital.lan;Database=hc360;SSL Mode=VerifyCA", "VerifyCA")]
    [InlineData("Host=db.hospital.lan;Database=hc360;SSL Mode=Allow", "Allow")]
    [InlineData("Host=localhost,db2.hospital.lan:5433;Database=hc360;SSL Mode=Prefer", "Prefer")]
    public void Another_machine_without_a_checked_certificate_is_refused_saying_what_to_set(string connectionString, string mode)
    {
        var problem = DatabaseTls.Problem("Hospital", connectionString).ShouldNotBeNull();
        problem.ShouldStartWith("ConnectionStrings:Hospital reaches PostgreSQL on ");
        problem.ShouldContain($"SSL Mode={mode}");
        problem.ShouldContain("SSL Mode=VerifyFull");
        problem.ShouldContain("AllowUnverifiedRemoteConnection");
        problem.ShouldNotContain("localhost", customMessage: "only the hosts off this machine are named");
    }

    [Fact]
    public void Every_PostgreSQL_connection_string_is_checked_and_the_lab_setting_lets_them_through()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Hospital"] = "Host=db.hospital.lan;Database=hc360;Username=hc360_app;Password=secret;SSL Mode=VerifyFull",
            ["ConnectionStrings:HospitalOwner"] = "Host=db.hospital.lan;Database=hc360;Username=hc360_owner;Password=secret",
            ["ConnectionStrings:Reporting"] = "Host=replica.hospital.lan;Database=hc360;SSL Mode=Require",
            ["ConnectionStrings:Redis"] = "redis.hospital.lan:6379,password=x",
        }).Build();

        var problems = DatabaseTls.Problems(configuration, new DatabaseOptions());
        problems.Count.ShouldBe(2);
        problems[0].ShouldStartWith("ConnectionStrings:HospitalOwner ");
        problems[0].ShouldNotContain("secret");
        problems[1].ShouldStartWith("ConnectionStrings:Reporting ");

        DatabaseTls.Problems(configuration, new DatabaseOptions { AllowUnverifiedRemoteConnection = true }).ShouldBeEmpty();
        new PlatformOptions().Database.AllowUnverifiedRemoteConnection.ShouldBeFalse("checked unless a lab says otherwise");
    }
}
