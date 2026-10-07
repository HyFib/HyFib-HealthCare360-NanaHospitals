using System.Net;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Changing the database password (SRS SEC-07) against a real PostgreSQL. The server under test signs in as the
/// superuser, whose password must not change, so the SQL step runs for a throwaway role of its own.
/// </summary>
public sealed class DatabasePasswordTests(HospitalServer server)
{
    [Fact]
    public async Task PostgreSQL_takes_the_verifier_and_a_failed_step_leaves_the_password_as_it_was()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await TestDatabase.CreateAsync(ct);
        var role = "hc360_rotate_" + Guid.NewGuid().ToString("N")[..12];
        const string first = "first-Password-1";
        await ExecuteAsync(database.ConnectionString, $"CREATE ROLE \"{role}\" LOGIN PASSWORD '{first}'", ct);
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = role, Password = first }.ConnectionString;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Hospital"] = connection }).Build();
            var credentials = new DatabaseCredentials(connection);
            var roles = new PostgresRolePassword(configuration, credentials);

            const string second = "second-Password-2";
            var verifier = ScramSha256.Verifier(second);
            await roles.ChangeAsync(verifier, () => Task.CompletedTask, ct);

            (await ScalarAsync(database.ConnectionString, $"SELECT rolpassword FROM pg_authid WHERE rolname = '{role}'", ct)).ShouldBe(verifier,
                "PostgreSQL stores the verifier as sent; the password itself is never in the statement");
            (await roles.SignsInWithAsync(second, ct)).ShouldBeTrue();
            (await roles.SignsInWithAsync(first, ct)).ShouldBeFalse();

            // The settings file could not be written: the change is rolled back.
            credentials.Change(second);
            await Should.ThrowAsync<IOException>(() => roles.ChangeAsync(ScramSha256.Verifier("third-Password-3"), () => throw new IOException("Disk full."), ct));
            (await roles.SignsInWithAsync(second, ct)).ShouldBeTrue();
            (await ScalarAsync(database.ConnectionString, $"SELECT rolpassword FROM pg_authid WHERE rolname = '{role}'", ct)).ShouldBe(verifier);

            await Should.ThrowAsync<ArgumentException>(() => roles.ChangeAsync("x'; DROP ROLE postgres; --", () => Task.CompletedTask, ct));
        }
        finally
        {
            await ExecuteAsync(database.ConnectionString, $"DROP ROLE IF EXISTS \"{role}\"", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Only_the_holders_of_the_permission_may_ask_and_the_server_says_why_it_cannot_here()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var status = await hospitalAdmin.GetAsync<DatabasePasswordStatus>("/api/v1/admin/system/database-password");
        status.Username.ShouldNotBeNullOrEmpty();
        status.CanChange.ShouldBeFalse("the test server's connection string is not in a settings file");
        status.Reason.ShouldNotBeNullOrEmpty();

        using (var refused = await hospitalAdmin.SendRawAsync(HttpMethod.Post, "/api/v1/admin/system/database-password/change"))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "only SuperAdmin has admin.system.manage by default");
        }

        var superAdmin = await server.SignInAsync(HospitalServer.Users.Admin);
        using var notHere = await superAdmin.SendRawAsync(HttpMethod.Post, "/api/v1/admin/system/database-password/change");
        notHere.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // Off Windows there is no DPAPI; on Windows (CI) the connection string is the test's own, not the settings file's.
        var problem = await notHere.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        (problem.Contains("database.password_not_windows", StringComparison.Ordinal) || problem.Contains("database.password_elsewhere", StringComparison.Ordinal))
            .ShouldBeTrue(problem);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(ct);
    }
}
