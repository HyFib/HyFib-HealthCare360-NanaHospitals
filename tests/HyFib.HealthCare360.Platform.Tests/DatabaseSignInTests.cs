using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>Switching the server between the Windows sign-in to PostgreSQL and a password, in the settings file.</summary>
public sealed class DatabaseSignInTests : IDisposable
{
    private const string Hospital = "Host=localhost;Port=5432;Database=hc360;Username=hc360_app;Password=app-password;Maximum Pool Size=200;Timeout=15";
    private const string LocalReplica = "Host=127.0.0.1;Port=5433;Database=hc360;Username=hc360_app;Password=app-password";
    private const string RemoteReplica = "Host=replica;Port=5432;Database=hc360;Username=hc360_app;Password=replica-password";
    private const string OtherAccount = "Host=localhost;Database=reports;Username=hc360_reports;Password=reports-password";
    private const string Redis = "localhost:6379,password=redis-password";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-db-sign-in-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProtector _protector = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Reads_the_hospital_connection_without_its_password()
    {
        var path = WriteSettings(Hospital);

        DatabaseSignIn.Read(path, _protector).ShouldBe(new DatabaseSignInState("localhost", 5432, "hc360", "hc360_app", WithPassword: true, OnThisMachine: true));
    }

    [Fact]
    public void The_Windows_sign_in_takes_the_password_out_of_the_servers_own_connections_to_this_machine()
    {
        var path = WriteSettings(Hospital, analytics: LocalReplica, archive: RemoteReplica, reports: OtherAccount);

        var changed = DatabaseSignIn.UseWindows(path, _protector);

        changed.ShouldBe(["ConnectionStrings:Hospital", "ConnectionStrings:Analytics"]);
        var hospital = Builder(Secret(path, "ConnectionStrings:Hospital"));
        hospital.Password.ShouldBeNull();
        (hospital.Host, hospital.Username, hospital.Database, hospital.MaxPoolSize).ShouldBe(("localhost", "hc360_app", "hc360", 200));
        Builder(Secret(path, "ConnectionStrings:Analytics")).Password.ShouldBeNull();
        DatabaseSignIn.Read(path, _protector).WithPassword.ShouldBeFalse();

        // Another machine's PostgreSQL, another account, and other secrets keep theirs, and every one stays protected.
        Secret(path, "ConnectionStrings:Archive").ShouldBe(RemoteReplica);
        Secret(path, "ConnectionStrings:Reports").ShouldBe(OtherAccount);
        Secret(path, "ConnectionStrings:Redis").ShouldBe(Redis);
        Secret(path, "HealthCare360:Network:CertificatePassword").ShouldBe("pfx-password");
        ProtectedSettings.PlainSecrets(path).ShouldBeEmpty();
        File.ReadAllText(path).ShouldNotContain("app-password");

        DatabaseSignIn.UseWindows(path, _protector).ShouldBeEmpty("a second run has nothing to change");
    }

    [Fact]
    public void A_password_goes_back_into_the_same_connections()
    {
        var path = WriteSettings(Hospital, analytics: LocalReplica, archive: RemoteReplica, reports: OtherAccount);
        DatabaseSignIn.UseWindows(path, _protector);

        var changed = DatabaseSignIn.UsePassword(path, _protector, "new-password");

        changed.ShouldBe(["ConnectionStrings:Hospital", "ConnectionStrings:Analytics"]);
        Builder(Secret(path, "ConnectionStrings:Hospital")).Password.ShouldBe("new-password");
        Builder(Secret(path, "ConnectionStrings:Analytics")).Password.ShouldBe("new-password");
        Secret(path, "ConnectionStrings:Archive").ShouldBe(RemoteReplica);
        Secret(path, "ConnectionStrings:Reports").ShouldBe(OtherAccount);
        DatabaseSignIn.Read(path, _protector).WithPassword.ShouldBeTrue();
        ProtectedSettings.PlainSecrets(path).ShouldBeEmpty();
    }

    [Fact]
    public void A_PostgreSQL_on_another_machine_keeps_the_password()
    {
        // The Windows sign-in is set up in this machine's PostgreSQL only (Install-HC360Server.ps1).
        var path = WriteSettings("Host=db-server;Port=5432;Database=hc360;Username=hc360_app;Password=app-password");
        var before = File.ReadAllBytes(path);

        Should.Throw<InvalidOperationException>(() => DatabaseSignIn.UseWindows(path, _protector))
            .Message.ShouldBe("PostgreSQL is reached at db-server, not through localhost: only a PostgreSQL on this machine takes the server's Windows account.");

        File.ReadAllBytes(path).ShouldBe(before);
        DatabaseSignIn.Read(path, _protector).OnThisMachine.ShouldBeFalse();
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("HC360-SRV", false)]
    [InlineData("10.0.5.20", false)]
    [InlineData("localhost,replica", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_loopback_address_is_this_machine(string? host, bool onThisMachine) =>
        DatabaseSignIn.IsOnThisMachine(host).ShouldBe(onThisMachine);

    [Fact]
    public void Reading_opens_only_the_hospital_connection()
    {
        // A setting protected on another machine cannot be opened here; reading the connection does not need it.
        var path = WriteSettings(Hospital);
        var sealedHere = ProtectedSettings.Prefix + _protector.Protect("pfx-password");
        File.WriteAllText(path, File.ReadAllText(path).Replace(sealedHere, ProtectedSettings.Prefix + "bm90LXNlYWxlZA==", StringComparison.Ordinal));

        DatabaseSignIn.Read(path, _protector).Username.ShouldBe("hc360_app");
        Should.Throw<InvalidOperationException>(() => DatabaseSignIn.UseWindows(path, _protector))
            .Message.ShouldContain("HealthCare360:Network:CertificatePassword");
    }

    private string WriteSettings(string hospital, string? analytics = null, string? archive = null, string? reports = null)
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = _root });
        Directory.CreateDirectory(directories.Config);
        var path = ProtectedSettings.PathIn(directories);
        var connections = new Dictionary<string, string?>
        {
            ["Hospital"] = hospital,
            ["Analytics"] = analytics,
            ["Archive"] = archive,
            ["Reports"] = reports,
            ["Redis"] = Redis,
        };
        var entries = string.Join(", ", connections.Where(c => c.Value is not null).Select(c => $"\"{c.Key}\": \"{c.Value}\""));
        File.WriteAllText(path, $$"""
            {
              "ConnectionStrings": { {{entries}} },
              "HealthCare360": { "HttpsPort": 7360, "Network": { "CertificatePassword": "pfx-password" } }
            }
            """, new UTF8Encoding(true));
        ProtectedSettings.ProtectFile(path, _protector);
        return path;
    }

    private string Secret(string path, string key) => ProtectedSettings.ReadSecret(path, key, _protector).ShouldNotBeNull();

    private static NpgsqlConnectionStringBuilder Builder(string connectionString) => new(connectionString);

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
