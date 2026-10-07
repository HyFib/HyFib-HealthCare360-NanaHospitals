using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Configuration;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class ProtectedSettingsTests : IDisposable
{
    private const string Database = "Host=localhost;Port=5432;Database=hc360;Username=hc360_app;Password=s3cret+/Pass;Timeout=15";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-settings-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("ConnectionStrings:Hospital", true)]
    [InlineData("connectionstrings:Analytics", true)]
    [InlineData("HealthCare360:Network:CertificatePassword", true)]
    [InlineData("HealthCare360:Integrations:ClientSecret", true)]
    [InlineData("HealthCare360:Maps:ApiKey", true)]
    [InlineData("HealthCare360:Security:MinPasswordLength", false)]
    [InlineData("HealthCare360:Security:PasswordHashIterations", false)]
    [InlineData("HealthCare360:Backup:Directory", false)]
    [InlineData("ConnectionStrings", false)]
    public void Secrets_are_connection_strings_and_settings_named_for_a_secret(string key, bool secret) =>
        ProtectedSettings.IsSecret(key).ShouldBe(secret);

    [Fact]
    public void Protecting_the_file_protects_its_secrets_and_leaves_the_rest()
    {
        var path = WriteSettings("""
            {
              // Written by Install-HC360Server.ps1
              "ConnectionStrings": { "Hospital": "Host=localhost;Database=hc360;Username=hc360_app;Password=s3cret+/Pass", "Redis": "" },
              "HealthCare360": {
                "HttpsPort": 7360,
                "DefaultFacilityName": "नाना हॉस्पिटल",
                "Network": { "CertificatePath": "D:\\certs\\hc360.pfx", "CertificatePassword": "pfx-pass", "ApprovedSsids": [ "HC360-Clinical" ] },
                "Backup": { "Enabled": true, "Directory": "D:\\Backups" },
              }
            }
            """);

        var keys = ProtectedSettings.ProtectFile(path, new FakeProtector());

        keys.ShouldBe(["ConnectionStrings:Hospital", "HealthCare360:Network:CertificatePassword"]);
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        configuration["ConnectionStrings:Hospital"].ShouldBe(ProtectedSettings.Prefix + FakeProtector.Seal("Host=localhost;Database=hc360;Username=hc360_app;Password=s3cret+/Pass"));
        configuration["HealthCare360:Network:CertificatePassword"].ShouldBe(ProtectedSettings.Prefix + FakeProtector.Seal("pfx-pass"));
        configuration["ConnectionStrings:Redis"].ShouldBe(string.Empty, "an empty setting has nothing to protect");
        configuration["HealthCare360:HttpsPort"].ShouldBe("7360");
        configuration["HealthCare360:DefaultFacilityName"].ShouldBe("नाना हॉस्पिटल");
        configuration["HealthCare360:Network:CertificatePath"].ShouldBe(@"D:\certs\hc360.pfx");
        configuration["HealthCare360:Network:ApprovedSsids:0"].ShouldBe("HC360-Clinical");
        configuration["HealthCare360:Backup:Enabled"].ShouldBe("True", StringCompareShould.IgnoreCase);
        var written = File.ReadAllText(path);
        written.ShouldContain("नाना हॉस्पिटल", Case.Sensitive, "the file stays readable");
        File.ReadAllBytes(path).AsSpan().StartsWith(Encoding.UTF8.Preamble).ShouldBeTrue("it keeps the byte order mark Windows PowerShell 5.1 needs");
        written.ShouldNotContain("\\u", Case.Sensitive, "nothing is escaped that need not be");
        ProtectedSettings.PlainSecrets(path).ShouldBeEmpty();
    }

    [Fact]
    public void Protecting_again_changes_nothing()
    {
        var path = WriteSettings($$"""{ "ConnectionStrings": { "Hospital": "{{Database}}" } }""");
        ProtectedSettings.ProtectFile(path, new FakeProtector());
        var first = File.ReadAllText(path);
        var written = File.GetLastWriteTimeUtc(path);

        ProtectedSettings.ProtectFile(path, new FakeProtector()).ShouldBeEmpty();

        File.ReadAllText(path).ShouldBe(first);
        File.GetLastWriteTimeUtc(path).ShouldBe(written, "the file is not rewritten when nothing changed");
    }

    [Fact]
    public void Plain_secrets_are_listed_by_name_only()
    {
        var path = WriteSettings($$"""
            { "ConnectionStrings": { "Hospital": "{{Database}}", "Analytics": "dpapi:c2VhbGVk" }, "HealthCare360": { "Network": { "CertificatePassword": "pfx-pass" } } }
            """);

        var plain = ProtectedSettings.PlainSecrets(path);

        plain.ShouldBe(["ConnectionStrings:Hospital", "HealthCare360:Network:CertificatePassword"]);
        string.Join(' ', plain).ShouldNotContain("s3cret");
        ProtectedSettings.PlainSecrets(Path.Combine(_root, "missing.json")).ShouldBeEmpty();
    }

    [Fact]
    public void The_server_reads_protected_values_as_plain_ones()
    {
        var path = WriteSettings($$"""{ "ConnectionStrings": { "Hospital": "{{Database}}" }, "HealthCare360": { "HttpsPort": 7443 } }""");
        ProtectedSettings.ProtectFile(path, new FakeProtector());

        var configuration = new ConfigurationBuilder().AddProtectedJsonFile(path, new FakeProtector()).Build();

        configuration.GetConnectionString("Hospital").ShouldBe(Database);
        configuration["HealthCare360:HttpsPort"].ShouldBe("7443");
    }

    [Fact]
    public void A_missing_file_is_allowed()
    {
        var configuration = new ConfigurationBuilder().AddProtectedJsonFile(Path.Combine(_root, "absent.json"), new FakeProtector()).Build();

        configuration.GetConnectionString("Hospital").ShouldBeNull();
    }

    [Fact]
    public void A_value_that_cannot_be_opened_stops_the_server_with_its_name_and_never_its_value()
    {
        var path = WriteSettings("""{ "ConnectionStrings": { "Hospital": "dpapi:bm90LWZyb20tdGhpcy1tYWNoaW5l" } }""");

        var wrongMachine = Reason(Should.Throw<Exception>(() => new ConfigurationBuilder().AddProtectedJsonFile(path, new FakeProtector()).Build()));
        wrongMachine.Message.ShouldContain("ConnectionStrings:Hospital");
        wrongMachine.Message.ShouldContain("settings protect");
        wrongMachine.Message.ShouldNotContain("bm90LWZyb20");

        var noDpapi = Reason(Should.Throw<Exception>(() => new ConfigurationBuilder().AddProtectedJsonFile(path, protector: null).Build()));
        noDpapi.Message.ShouldContain("ConnectionStrings:Hospital");
        if (!OperatingSystem.IsWindows())
        {
            noDpapi.Message.ShouldContain("can be read only on the Windows machine that protected it");
        }
    }

    /// <summary>The configuration system wraps a file's load error in its own; this is HC360's explanation inside it.</summary>
    private static InvalidOperationException Reason(Exception thrown)
    {
        for (Exception? ex = thrown; ex is not null; ex = ex.InnerException)
        {
            if (ex is InvalidOperationException reason)
            {
                return reason;
            }
        }

        throw new ShouldAssertException($"No explanation in: {thrown}");
    }

    [Fact]
    public void DPAPI_opens_what_it_protected_on_this_machine()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI exists only on Windows; the Windows CI job runs this.");
        var path = WriteSettings($$"""{ "ConnectionStrings": { "Hospital": "{{Database}}" } }""");
        var protector = ProtectedSettings.ForThisMachine()!;

        ProtectedSettings.ProtectFile(path, protector).ShouldBe(["ConnectionStrings:Hospital"]);

        File.ReadAllText(path).ShouldNotContain("s3cret");
        new ConfigurationBuilder().AddProtectedJsonFile(path).Build().GetConnectionString("Hospital").ShouldBe(Database);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteSettings(string json)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, ProtectedSettings.FileName);

        // As Windows PowerShell's Set-Content -Encoding UTF8 writes it: with a byte order mark.
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    /// <summary>Stands in for DPAPI off Windows: "sealed" text it can check, so a value from elsewhere fails to open.</summary>
    private sealed class FakeProtector : ISettingProtector
    {
        public static string Seal(string plaintext) => Convert.ToBase64String(Encoding.UTF8.GetBytes("sealed:" + plaintext));

        public string Protect(string plaintext) => Seal(plaintext);

        public string Unprotect(string protectedValue)
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
            return text.StartsWith("sealed:", StringComparison.Ordinal) ? text["sealed:".Length..] : throw new CryptographicException("Not sealed here.");
        }
    }
}
