using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The whole HC360 server for the end-to-end journeys: every module with its real middleware, authorization and
/// endpoints (<see cref="ServerComposition"/>, as in production), on an in-memory test server over a new PostgreSQL
/// database. The real migrations create the database, and the seeders fill it with the demonstration data and the staff
/// accounts in <see cref="Users"/>. One server is shared by every test and tests run one at a time, so each journey
/// registers its own patients rather than relying on another test's.
/// </summary>
public sealed class HospitalServer : IAsyncLifetime
{
    // A test-only password for the seeded accounts, new for every run.
    private readonly string _password = "It-" + Guid.NewGuid().ToString("N");
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hc360-tests", "it-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<string, ApiClient> _signedIn = new(StringComparer.Ordinal);
    private TestDatabase? _database;
    private WebApplication? _app;

    public IServiceProvider Services => App.Services;

    private WebApplication App => _app ?? throw new InvalidOperationException("The test server has not started.");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _database = await TestDatabase.CreateAsync(ct);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Hospital"] = _database.ConnectionString,
            ["HealthCare360:DataDirectory"] = _dataDirectory,
            ["HealthCare360:SeedDemoData"] = "true",
            ["HealthCare360:Bootstrap:AdminPassword"] = _password,
            ["HealthCare360:Bootstrap:RequirePasswordChange"] = "false",
            ["HealthCare360:Security:EnforceMfa"] = "false",
            ["HealthCare360:Security:AccessTokenMinutes"] = "120",
            ["HealthCare360:Network:EnableMdns"] = "false",
            ["HealthCare360:Network:EnableWifiMonitor"] = "false",
            ["HealthCare360:Backup:Enabled"] = "false",
            ["HealthCare360:Backup:BeforeMigrations"] = "false",
        });

        // As Program.cs does, except that no certificate is created: the test server does not listen on the network.
        var options = builder.Configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>()!;
        var directories = new DataDirectories(options);
        directories.EnsureCreated();
        var certificates = new LanCertificateManager(directories, options, new EphemeralDataProtectionProvider(), NullLogger<LanCertificateManager>.Instance);
        builder.AddServerApplication(certificates);

        _app = builder.Build();
        _app.UseServerApplication();
        await _app.InitializeDatabaseAsync(ct);
        await _app.StartAsync(ct);
    }

    /// <summary>A caller who has not signed in.</summary>
    public ApiClient Anonymous() => new(CreateHttpClient());

    /// <summary>
    /// A caller signed in as one of the seeded staff accounts (<see cref="Users"/>), with that role's real permissions.
    /// Each account signs in once per run: sign-in is rate-limited, and every test-server request has the same address.
    /// </summary>
    public async Task<ApiClient> SignInAsync(string userName)
    {
        if (_signedIn.TryGetValue(userName, out var client))
        {
            return client;
        }

        var http = CreateHttpClient();
        using var response = await http.PostAsJsonAsync("/api/v1/auth/login", new { userName, password = _password }, ApiClient.Json);
        var result = await ApiClient.ReadAsync<SignInResult>(response, HttpMethod.Post, "/api/v1/auth/login");
        if (result.AccessToken is not { Length: > 0 } token || result.MfaRequired || result.Restriction is not null)
        {
            throw new InvalidOperationException($"'{userName}' could not sign in fully (MFA required: {result.MfaRequired}, restriction: {result.Restriction}).");
        }

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _signedIn.GetOrAdd(userName, new ApiClient(http));
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private HttpClient CreateHttpClient()
    {
        var http = App.GetTestClient();
        http.DefaultRequestHeaders.Add("X-HC360-Client", "web");
        return http;
    }

    /// <summary>The staff accounts the demonstration seed creates, one per role.</summary>
    public static class Users
    {
        public const string FrontOffice = "frontoffice";
        public const string Doctor = "doctor";
        public const string Nurse = "nurse";
        public const string Lab = "lab";
        public const string Pharmacist = "pharmacist";
        public const string Billing = "billing";
        public const string HospitalAdmin = "hospitaladmin";
        public const string Admin = "admin";
    }

    private sealed record SignInResult(string? AccessToken, bool MfaRequired, string? Restriction);
}
