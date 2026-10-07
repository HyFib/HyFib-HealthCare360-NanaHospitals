using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class DatabasePasswordTests : IDisposable
{
    private const string Hospital = "Host=localhost;Port=5432;Database=hc360;Username=hc360_app;Password=first-password;Timeout=15";
    private const string Replica = "Host=replica;Port=5432;Database=hc360;Username=hc360_app;Password=first-password";
    private const string OtherAccount = "Host=localhost;Database=reports;Username=hc360_reports;Password=reports-password";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-db-password-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_verifier_is_the_one_RFC_7677_describes()
    {
        // RFC 7677, section 3: user "user", password "pencil".
        var salt = Convert.FromBase64String("W22ZaJ0SNY7soEsUEjb6gQ==");
        const string authMessage = "n=user,r=rOprNGfwEbeRWgbNEkqO,"
                                   + "r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096,"
                                   + "c=biws,r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0";

        var (clientKey, storedKey, serverKey) = ScramSha256.Keys("pencil", salt, 4096);

        var signature = HMACSHA256.HashData(storedKey, Encoding.UTF8.GetBytes(authMessage));
        var proof = clientKey.Zip(signature, (a, b) => (byte)(a ^ b)).ToArray();
        Convert.ToBase64String(proof).ShouldBe("dHzbZapWIk4jUhN+Ute9ytag9zjfMHgsqmmiz7AndVQ=");
        Convert.ToBase64String(HMACSHA256.HashData(serverKey, Encoding.UTF8.GetBytes(authMessage))).ShouldBe("6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=");
        ScramSha256.Verifier("pencil", salt).ShouldBe(
            $"SCRAM-SHA-256$4096:W22ZaJ0SNY7soEsUEjb6gQ==${Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}");
    }

    [Fact]
    public void Each_verifier_has_its_own_salt() =>
        ScramSha256.Verifier("same-password").ShouldNotBe(ScramSha256.Verifier("same-password"));

    [Fact]
    public void Connections_get_the_password_as_they_open_and_follow_a_change()
    {
        var credentials = new DatabaseCredentials(Hospital);
        credentials.Username.ShouldBe("hc360_app");
        credentials.HasPassword.ShouldBeTrue();

        var (connection, configure) = credentials.For(Hospital);
        connection.ShouldNotContain("first-password");
        new NpgsqlConnectionStringBuilder(connection).Host.ShouldBe("localhost");

        // Npgsql refuses a password provider next to a password in the connection string; this data source builds.
        var builder = new NpgsqlDataSourceBuilder(connection);
        configure(builder);
        using (builder.Build())
        {
        }

        credentials.Change("second-password");
        credentials.WithCurrentPassword(Replica).ShouldContain("Password=second-password");
        credentials.WithCurrentPassword(OtherAccount).ShouldContain("Password=reports-password", Case.Sensitive, "another account keeps its own password");
    }

    [Fact]
    public void A_connection_without_a_password_is_used_as_it_is()
    {
        // Signing in with Windows (SSPI) or Kerberos needs no password.
        const string windowsSignIn = "Host=localhost;Database=hc360;Username=hc360_app";
        var credentials = new DatabaseCredentials(windowsSignIn);

        credentials.HasPassword.ShouldBeFalse();
        credentials.For(windowsSignIn).ConnectionString.ShouldBe(windowsSignIn);
    }

    [Fact]
    public async Task Changing_sets_the_database_and_the_file_together_and_uses_the_new_password()
    {
        var path = WriteSettings(new Dictionary<string, string>
        {
            ["Hospital"] = Hospital,
            ["Analytics"] = Replica,
            ["Reports"] = OtherAccount,
            ["Redis"] = "localhost:6379,password=redis-password",
        });
        var database = new FakeDatabase();
        var (service, credentials, audit) = Service(database, Configuration(path));

        var changed = (await service.ChangeAsync(TestContext.Current.CancellationToken)).Value;

        changed.SettingsChanged.ShouldBe(["ConnectionStrings:Hospital", "ConnectionStrings:Analytics"]);
        var settings = Configuration(path);
        var password = new NpgsqlConnectionStringBuilder(settings.GetConnectionString("Hospital")).Password!;
        password.ShouldNotBe("first-password");
        password.Length.ShouldBeGreaterThanOrEqualTo(40);
        new NpgsqlConnectionStringBuilder(settings.GetConnectionString("Analytics")).Password.ShouldBe(password);
        settings.GetConnectionString("Reports").ShouldBe(OtherAccount, "another account keeps its password");
        settings.GetConnectionString("Redis").ShouldBe("localhost:6379,password=redis-password");
        File.ReadAllText(path).ShouldNotContain(password, Case.Sensitive, "the new password is written protected");

        // The database got the verifier of the password the file holds, and the server uses it from now on.
        var parts = database.Verifier!.Split('$', ':');
        ScramSha256.Verifier(password, Convert.FromBase64String(parts[2])).ShouldBe(database.Verifier);
        credentials.Password.ShouldBe(password);
        await audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e => e.Action == DatabasePasswordService.AuditAction && !e.Summary!.Contains(password)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_the_database_refuses_the_file_is_put_back_and_the_old_password_stays()
    {
        var path = WriteSettings(new Dictionary<string, string> { ["Hospital"] = Hospital });
        var before = File.ReadAllBytes(path);
        var database = new FakeDatabase { FailCommit = true };
        var (service, credentials, audit) = Service(database, Configuration(path));

        var result = await service.ChangeAsync(TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("database.password_not_changed");
        database.FileSeenBeforeCommit.ShouldBeTrue("the file is written before the change is committed");
        File.ReadAllBytes(path).ShouldBe(before);
        credentials.Password.ShouldBe("first-password");
        await audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_commit_that_reported_an_error_but_took_effect_is_kept()
    {
        var path = WriteSettings(new Dictionary<string, string> { ["Hospital"] = Hospital });
        var database = new FakeDatabase { FailCommit = true, CommittedAnyway = true };
        var (service, credentials, _) = Service(database, Configuration(path));

        (await service.ChangeAsync(TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        credentials.Password.ShouldBe(new NpgsqlConnectionStringBuilder(Configuration(path).GetConnectionString("Hospital")).Password);
    }

    [Fact]
    public async Task A_file_edited_to_sign_in_as_another_account_is_left_as_it_is()
    {
        var path = WriteSettings(new Dictionary<string, string> { ["Hospital"] = Hospital });
        var (service, credentials, _) = Service(new FakeDatabase(), Configuration(path));

        // Edited after start-up: only the replica still signs in as the server's account.
        WriteSettings(new Dictionary<string, string> { ["Hospital"] = OtherAccount, ["Analytics"] = Replica });
        var before = File.ReadAllBytes(path);

        var result = await service.ChangeAsync(TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("database.password_not_changed");
        File.ReadAllBytes(path).ShouldBe(before);
        credentials.Password.ShouldBe("first-password");
    }

    [Fact]
    public async Task It_is_not_offered_where_it_cannot_work()
    {
        var path = WriteSettings(new Dictionary<string, string> { ["Hospital"] = Hospital });

        var (noDpapi, _, _) = Service(new FakeDatabase(), Configuration(path), onWindows: false);
        (await noDpapi.ChangeAsync(TestContext.Current.CancellationToken)).Error!.Code.ShouldBe("database.password_not_windows");

        var fromEnvironment = new ConfigurationBuilder()
            .AddProtectedJsonFile(path, new FakeProtector())
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Hospital"] = Hospital })
            .Build();
        var (elsewhere, _, _) = Service(new FakeDatabase(), fromEnvironment);
        (await elsewhere.ChangeAsync(TestContext.Current.CancellationToken)).Error!.Code.ShouldBe("database.password_elsewhere");

        var windowsSignIn = WriteSettings(new Dictionary<string, string> { ["Hospital"] = "Host=localhost;Database=hc360;Username=hc360_app" });
        var (noPassword, _, _) = Service(new FakeDatabase(), Configuration(windowsSignIn));
        (await noPassword.ChangeAsync(TestContext.Current.CancellationToken)).Error!.Code.ShouldBe("database.password_none");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteSettings(Dictionary<string, string> connectionStrings)
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = _root });
        Directory.CreateDirectory(directories.Config);
        var path = ProtectedSettings.PathIn(directories);
        var entries = string.Join(", ", connectionStrings.Select(c => $"\"{c.Key}\": \"{c.Value}\""));
        File.WriteAllText(path, $$"""{ "ConnectionStrings": { {{entries}} } }""", new UTF8Encoding(true));
        ProtectedSettings.ProtectFile(path, new FakeProtector());
        return path;
    }

    private static IConfigurationRoot Configuration(string path) => new ConfigurationBuilder().AddProtectedJsonFile(path, new FakeProtector()).Build();

    private (DatabasePasswordService Service, DatabaseCredentials Credentials, IAuditLogger Audit) Service(
        FakeDatabase database, IConfiguration configuration, bool onWindows = true)
    {
        var credentials = new DatabaseCredentials(configuration.GetConnectionString("Hospital")!);
        var audit = Substitute.For<IAuditLogger>();
        var service = new DatabasePasswordService(
            configuration,
            credentials,
            new DataDirectories(new PlatformOptions { DataDirectory = _root }),
            new SettingsFileProtection(onWindows ? new FakeProtector() : null),
            database,
            ModelOnlyContext(),
            audit,
            TimeProvider.System,
            NullLogger<DatabasePasswordService>.Instance);
        return (service, credentials, audit);
    }

    private static HospitalDbContext ModelOnlyContext() => new(
        new DbContextOptionsBuilder<HospitalDbContext>().UseNpgsql("Host=localhost;Database=hc360_model_check").UseSnakeCaseNamingConvention().Options,
        new ModuleCatalog([]));

    /// <summary>Stands in for PostgreSQL: records the verifier, and checks that the file is written before the commit.</summary>
    private sealed class FakeDatabase : IDatabaseRolePassword
    {
        public string? Verifier { get; private set; }
        public bool FileSeenBeforeCommit { get; private set; }
        public bool FailCommit { get; init; }
        public bool CommittedAnyway { get; init; }

        public async Task ChangeAsync(string verifier, Func<Task> beforeCommit, CancellationToken ct)
        {
            Verifier = verifier;
            await beforeCommit();
            FileSeenBeforeCommit = true;
            if (FailCommit)
            {
                throw new NpgsqlException("The connection was lost during the commit.");
            }
        }

        public Task<bool> SignsInWithAsync(string password, CancellationToken ct) => Task.FromResult(CommittedAnyway);
    }

    private sealed class FakeProtector : ISettingProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(Encoding.UTF8.GetBytes("sealed:" + plaintext));

        public string Unprotect(string protectedValue)
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
            return text.StartsWith("sealed:", StringComparison.Ordinal) ? text["sealed:".Length..] : throw new CryptographicException("Not sealed here.");
        }
    }
}
