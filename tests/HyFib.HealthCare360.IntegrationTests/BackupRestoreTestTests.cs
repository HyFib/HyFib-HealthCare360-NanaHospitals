using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Test restores over the real server and PostgreSQL (DED-16): the run's own backup, of the whole HC360 schema, restored
/// into a scratch database by an account that may create databases and is no superuser, as on a Server Node
/// (DEPLOYMENT.md section 8, "Test restores"); its key tables' rows checked against those its manifest recorded; the
/// scratch database dropped; the outcome in the Backups list. And the account the server works as, which may not create
/// databases, refused with what to do, the administrators told in the app.
/// </summary>
public sealed class BackupRestoreTestTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_newest_backup_restores_into_a_scratch_database_with_the_rows_it_recorded_and_the_scratch_database_is_dropped()
    {
        Assert.SkipUnless(ScriptedRestore.PgRestoreInstalled(), "Needs PostgreSQL's pg_restore, in PGBIN or on the PATH.");
        var settings = server.Services.GetRequiredService<IOptions<PlatformOptions>>().Value.Backup.TestRestore;
        var checkRoom = settings.CheckRoom;
        var account = "hc360_it_restore_" + Guid.NewGuid().ToString("N")[..10];
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        try
        {
            await ExecuteAsync(Live(), $"CREATE ROLE {account} LOGIN CREATEDB PASSWORD 'restore-{account}'; GRANT pg_read_all_settings TO {account}");

            // A container's disk is not this machine's: there the room cannot be checked from here.
            settings.CheckRoom = await DataDirectoryIsHereAsync();
            var made = await BackupAsync();
            var manifest = await ManifestAsync(made);
            manifest.RowCounts.ShouldNotBeNull()["patients.patients"].ShouldBe(await PatientsAsync(), "the rows counted in the dump's snapshot");
            manifest.RowCounts.ShouldContainKey("audit.audit_events");

            await using var scope = server.Services.CreateAsyncScope();
            var outcome = await Tester(scope.ServiceProvider, CreatedBy(account)).RunAsync(force: true, Ct);

            outcome.ShouldNotBeNull().Passed.ShouldBeTrue(outcome.Note);
            outcome.RunId.ShouldBe(made.Id, "the newest backup");
            var listed = (await admin.GetAsync<List<BackupRunDto>>("/api/v1/admin/backups")).Single(r => r.Id == made.Id);
            listed.RestoreTestPassed.ShouldBe(true);
            listed.RestoreTestedAt.ShouldNotBeNull();
            listed.RestoreTestNote.ShouldNotBeNull().ShouldContain($"its {manifest.RowCounts.Count} key tables hold the");
            (await ScratchDatabasesAsync()).ShouldBeEmpty("the scratch database, a copy of every patient's record, is dropped");
        }
        finally
        {
            // The server is shared by every test of the run: as it was.
            settings.CheckRoom = checkRoom;
            await DropAccountAsync(account);
        }
    }

    [Fact]
    public async Task A_test_restore_by_an_account_that_may_not_create_databases_fails_saying_what_to_do()
    {
        Assert.SkipUnless(ScriptedRestore.PgRestoreInstalled(), "Needs PostgreSQL's pg_restore, in PGBIN or on the PATH.");
        var account = "hc360_it_app_" + Guid.NewGuid().ToString("N")[..10];
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        try
        {
            await ExecuteAsync(Live(), $"CREATE ROLE {account} LOGIN PASSWORD 'restore-{account}'");
            var made = await BackupAsync();

            await using var scope = server.Services.CreateAsyncScope();
            var outcome = await Tester(scope.ServiceProvider, CreatedBy(account)).RunAsync(force: true, Ct);

            outcome.ShouldNotBeNull().Passed.ShouldBeFalse();
            outcome.Note.ShouldStartWith($"The account test restores sign in as, {account}, may not create databases");
            var run = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<BackupRun>().AsNoTracking().SingleAsync(r => r.Id == made.Id, Ct);
            (run.RestoreTestPassed, run.RestoreTestNote).ShouldBe((false, outcome.Note));

            var told = (await admin.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=200")).Items
                .Where(n => n.Title == BackupRestoreTest.FailedTitle && n.Body?.Contains(made.FileName!, StringComparison.Ordinal) == true);
            told.ShouldHaveSingleItem("the administrators are told in the app");
            (await ScratchDatabasesAsync()).ShouldBeEmpty();
        }
        finally
        {
            await DropAccountAsync(account);
        }
    }

    /// <summary>The test restore as the upkeep runs it, with the scratch database made by <paramref name="scratch"/>.</summary>
    private static BackupRestoreTest Tester(IServiceProvider services, IScratchDatabase scratch) => new(
        services.GetRequiredService<IDbContextFactory<HospitalDbContext>>(),
        services.GetRequiredService<IOptions<PlatformOptions>>(),
        services.GetRequiredService<DataDirectories>(),
        services.GetRequiredService<RecoveryKeyStore>(),
        services.GetRequiredService<IPostgresTools>(),
        scratch,
        services.GetRequiredService<INotificationService>(),
        services.GetRequiredService<TimeProvider>(),
        services.GetRequiredService<ILogger<BackupRestoreTest>>());

    /// <summary>The scratch databases made by <paramref name="account"/>, as <c>ConnectionStrings:BackupRestoreTest</c> names it.</summary>
    private PostgresTools CreatedBy(string account)
    {
        var restore = new NpgsqlConnectionStringBuilder(Live()) { Username = account, Password = "restore-" + account, Database = "postgres" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Hospital"] = Live(),
            [$"ConnectionStrings:{BackupRestoreTest.ConnectionName}"] = restore.ConnectionString,
        }).Build();
        return new PostgresTools(configuration, server.Services.GetRequiredService<DatabaseCredentials>(), server.Services.GetRequiredService<IOptions<PlatformOptions>>(),
            server.Services.GetRequiredService<ILogger<PostgresTools>>());
    }

    /// <summary>A backup as Administration › Backups › Back up now makes it.</summary>
    private async Task<BackupRunDto> BackupAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<BackupService>().RunAsync(BackupKind.Manual, null, Ct);
        run.Status.ShouldBe(BackupStatus.Succeeded, run.Error);
        return run;
    }

    /// <summary>The backup's manifest, read with the recovery key that opens it.</summary>
    private async Task<BackupManifest> ManifestAsync(BackupRunDto run)
    {
        var key = server.Services.GetRequiredService<RecoveryKeyStore>().Current();
        await using var input = File.OpenRead(Path.Combine(server.Services.GetRequiredService<DataDirectories>().Backups, run.FileName!));
        await using var payload = await BackupEncryption.OpenAsync(input, key.Key, Ct);
        return await BackupArchive.ReadManifestAsync(payload, Ct);
    }

    private async Task<long> PatientsAsync()
    {
        await using var connection = new NpgsqlConnection(Live());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM patients.patients", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<bool> DataDirectoryIsHereAsync()
    {
        await using var connection = new NpgsqlConnection(Live());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT setting FROM pg_settings WHERE name = 'data_directory'", connection);
        return await command.ExecuteScalarAsync(Ct) is string directory && Directory.Exists(directory);
    }

    private async Task<List<string>> ScratchDatabasesAsync()
    {
        await using var connection = new NpgsqlConnection(Live());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT datname FROM pg_database WHERE starts_with(datname, @prefix)", connection);
        command.Parameters.AddWithValue("prefix", PostgresTools.ScratchPrefix);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>The account, and any scratch database a failed test left it owning, which would keep it from being dropped.</summary>
    private async Task DropAccountAsync(string account)
    {
        await using var connection = new NpgsqlConnection(Live());
        await connection.OpenAsync(CancellationToken.None);
        var owned = new List<string>();
        await using (var find = new NpgsqlCommand("SELECT datname FROM pg_database WHERE pg_get_userbyid(datdba) = @account", connection))
        {
            find.Parameters.AddWithValue("account", account);
            await using var reader = await find.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                owned.Add(reader.GetString(0));
            }
        }

        foreach (var name in owned)
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var role = new NpgsqlCommand($"DROP ROLE IF EXISTS {account}", connection);
        await role.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>The run's database, signed in as the superuser the test server uses.</summary>
    private string Live() => server.Services.GetRequiredService<IConfiguration>().GetConnectionString("Hospital")!;
}
