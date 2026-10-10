using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The Windows sign-in to PostgreSQL (SSPI) with no password anywhere, as the HC360 service signs in: through Npgsql for
/// the server's own connections, and through pg_dump for backups. Runs where CI has let the runner's Windows account sign
/// in that way with the installer's own functions (tests/deploy/Enable-TestWindowsSignIn.ps1, which sets
/// HC360_TEST_WINDOWS_SIGN_IN); skipped elsewhere.
/// </summary>
public sealed class WindowsDatabaseSignInTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_server_signs_in_with_its_Windows_account_and_no_password()
    {
        var connection = WindowsSignIn();
        var (connectionString, configure) = new DatabaseCredentials(connection).For(connection);
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        configure(builder);
        await using var dataSource = builder.Build();

        await using var command = dataSource.CreateCommand("SELECT system_user, current_user");
        await using var reader = await command.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).ShouldBeTrue();

        // system_user names the way PostgreSQL authenticated the connection, and the Windows account it took.
        reader.GetString(0).ShouldStartWith("sspi:");
        reader.GetString(1).ShouldBe(new NpgsqlConnectionStringBuilder(connection).Username);
    }

    [Fact]
    public async Task Backups_dump_the_database_with_the_Windows_account()
    {
        var connection = WindowsSignIn();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Hospital"] = connection })
            .Build();
        var tools = new PostgresTools(configuration, new DatabaseCredentials(connection), Options.Create(new PlatformOptions()), NullLogger<PostgresTools>.Instance);
        var dump = Path.Combine(Path.GetTempPath(), $"hc360-windows-sign-in-{Guid.NewGuid():N}.dump");
        try
        {
            await tools.DumpAsync(dump, Ct);

            new FileInfo(dump).Length.ShouldBeGreaterThan(0);
            (await tools.TryValidateDumpAsync(dump, Ct)).ShouldBeTrue("pg_restore reads the whole dump");
        }
        finally
        {
            File.Delete(dump);
        }
    }

    private static string WindowsSignIn()
    {
        var connection = Environment.GetEnvironmentVariable("HC360_TEST_WINDOWS_SIGN_IN");
        Assert.SkipUnless(OperatingSystem.IsWindows() && !string.IsNullOrEmpty(connection),
            "Needs a PostgreSQL that takes this Windows account with SSPI: CI's Windows job sets one up.");
        new NpgsqlConnectionStringBuilder(connection).Password.ShouldBeNull("the Windows sign-in needs no password");
        return connection!;
    }
}
