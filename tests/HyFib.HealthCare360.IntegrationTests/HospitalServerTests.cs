using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>The test server itself: a database built by the real migrations, the seeded staff accounts, deny by default.</summary>
public sealed class HospitalServerTests(HospitalServer server)
{
    [Fact]
    public async Task The_real_migrations_build_the_database()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var ct = TestContext.Current.CancellationToken;

        (await db.Database.GetPendingMigrationsAsync(ct)).ShouldBeEmpty();
        (await db.Database.GetAppliedMigrationsAsync(ct)).ShouldBe(db.Database.GetMigrations());
    }

    [Theory]
    [InlineData(HospitalServer.Users.FrontOffice)]
    [InlineData(HospitalServer.Users.Doctor)]
    [InlineData(HospitalServer.Users.Nurse)]
    [InlineData(HospitalServer.Users.Lab)]
    [InlineData(HospitalServer.Users.Pharmacist)]
    [InlineData(HospitalServer.Users.Billing)]
    public async Task Staff_sign_in_with_the_seeded_accounts(string userName)
    {
        var api = await server.SignInAsync(userName);

        var me = await api.GetAsync<JsonElement>("/api/v1/auth/me");

        me.GetProperty("userName").GetString().ShouldBe(userName);
    }

    [Fact]
    public async Task The_api_refuses_a_caller_who_has_not_signed_in()
    {
        using var response = await server.Anonymous().SendRawAsync(HttpMethod.Get, "/api/v1/patients?search=test");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_server_reports_ready()
    {
        using var response = await server.Anonymous().SendRawAsync(HttpMethod.Get, "/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
