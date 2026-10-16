using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The warm standby's replication slot as a real PostgreSQL has it (SRS DED-20): the server's own account, which is not a
/// superuser and has no statistics role, reads it, and System health says when the standby is away. A slot belongs to the
/// whole PostgreSQL server, not to one database, so each test makes one of its own and drops it again. The CI Windows job
/// also streams to a real standby (tests/deploy/Test-StandbyReplication.ps1).
/// </summary>
public sealed class StandbyReplicationTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task System_health_says_when_the_warm_standby_is_away()
    {
        var slot = "hc360_it_" + Guid.NewGuid().ToString("N")[..12];
        await ExecuteAsync(Superuser(), $"SELECT pg_create_physical_replication_slot('{slot}', true)");
        try
        {
            var admin = await server.SignInAsync(HospitalServer.Users.Admin);
            var health = await admin.GetAsync<HealthWarnings>("/api/v1/admin/system/health");

            var warning = health.Warnings.Where(w => w.Message.Contains(slot, StringComparison.Ordinal)).ShouldHaveSingleItem();
            (warning.Code, warning.Severity).ShouldBe(("replication.standby_disconnected", "warning"));
        }
        finally
        {
            await ExecuteAsync(Superuser(), $"SELECT pg_drop_replication_slot('{slot}')");
        }
    }

    [Fact]
    public async Task An_account_without_extra_rights_reads_the_slot_and_the_archiver()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var (slot, role) = ("hc360_it_" + suffix, "hc360_it_reader_" + suffix);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await ExecuteAsync(Superuser(), $"CREATE ROLE {role} LOGIN PASSWORD '{password}'");
        await ExecuteAsync(Superuser(), $"SELECT pg_create_physical_replication_slot('{slot}', true)");
        try
        {
            var asRole = new NpgsqlConnectionStringBuilder(Superuser()) { Username = role, Password = password, Pooling = false };
            await using var connection = new NpgsqlConnection(asRole.ConnectionString);
            await connection.OpenAsync(Ct);

            var state = await StandbyReplication.ReadAsync(connection, Ct);

            var found = state.Standbys.Where(s => s.Name == slot).ShouldHaveSingleItem();
            (found.Active, found.WalStatus).ShouldBe((false, "reserved"));
            found.BehindBytes.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(0, "the slot keeps WAL from where it was made");
            // The test PostgreSQL does not archive WAL, so there is no archiver to report on.
            state.Archiver.ShouldBeNull();
        }
        finally
        {
            await ExecuteAsync(Superuser(), $"SELECT pg_drop_replication_slot('{slot}')");
            await ExecuteAsync(Superuser(), $"DROP ROLE IF EXISTS {role}");
        }
    }

    /// <summary>The test server's own connection, which signs in as a superuser.</summary>
    private string Superuser() => server.Services.GetRequiredService<IConfiguration>().GetConnectionString("Hospital")!;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private sealed record HealthWarnings(IReadOnlyList<HealthWarning> Warnings);
}
