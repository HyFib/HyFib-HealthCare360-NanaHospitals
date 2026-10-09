using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.Server;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The whole HC360 server for the end-to-end journeys: every module with its real middleware, authorization and
/// endpoints (<see cref="ServerComposition"/>, as in production), on an in-memory test server over a new PostgreSQL
/// database. The real migrations create the database, and the seeders fill it with the demonstration data and the staff
/// accounts in <see cref="Users"/> (roles the seed has no account for are created on first use). One server is shared by
/// every test and tests run one at a time, so each journey registers its own patients rather than relying on another test's.
/// </summary>
public sealed class HospitalServer : IAsyncLifetime
{
    /// <summary>
    /// The caller's address, for a request from a test that needs a rate-limit budget of its own. The server limits
    /// sign-in, refresh and one-time codes per address, and test-server requests otherwise have none, so every test would
    /// share one budget with the tests that ran before it in the same minute.
    /// </summary>
    private const string ClientAddressHeader = "X-Test-Client-Address";

    /// <summary>Accounts the demonstration seed does not create, with their role (see <see cref="CreateAccountAsync"/>).</summary>
    private static readonly Dictionary<string, string> RolesCreatedOnFirstUse = new(StringComparer.Ordinal)
    {
        [Users.Pathologist] = SystemRoles.Pathologist,
        [Users.MrdOfficer] = SystemRoles.MrdOfficer,
        [Users.MrdVerifier] = SystemRoles.MrdOfficer,
        [Users.CssdTechnician] = SystemRoles.CssdTechnician,
        [Users.CardiologyTechnician] = SystemRoles.CardiologyTechnician,
    };

    // A test-only password for the staff accounts, new for every run.
    private readonly string _password = "It-" + Guid.NewGuid().ToString("N");
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hc360-tests", "it-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<string, ApiClient> _signedIn = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _created = new(StringComparer.Ordinal);
    private TestDatabase? _database;
    private WebApplication? _app;

    public IServiceProvider Services => App.Services;

    /// <summary>The run's password of the seeded staff accounts, for a test that signs in on its own.</summary>
    public string Password => _password;

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
        _app.Use((context, next) =>
        {
            if (IPAddress.TryParse(context.Request.Headers[ClientAddressHeader].ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            return next(context);
        });
        _app.UseServerApplication();
        await _app.InitializeDatabaseAsync(ct);

        // Many tests sign in the same seeded accounts, each with a session of its own, while other tests keep using the
        // cached ones: no limit on sessions per account, except in a test that sets one (SessionControlTests).
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(AuthService.MaxSessionsSetting, 0, cancellationToken: ct);
        }

        await _app.StartAsync(ct);
    }

    /// <summary>
    /// A caller who has not signed in. With <paramref name="ownAddress"/> its requests come from the test's own address,
    /// for calls limited per address (one-time codes allow only 5 requests per address in 15 minutes).
    /// </summary>
    public ApiClient Anonymous(bool ownAddress = false) => new(CreateHttpClient(ownAddress));

    /// <summary>
    /// A caller signed in as one of the staff accounts in <see cref="Users"/>, with that role's real permissions.
    /// Each account signs in once per run: sign-in is rate-limited, and every test-server request has the same address.
    /// </summary>
    public async Task<ApiClient> SignInAsync(string userName)
    {
        if (_signedIn.TryGetValue(userName, out var client))
        {
            return client;
        }

        var http = CreateHttpClient();
        var token = RolesCreatedOnFirstUse.TryGetValue(userName, out var role) && _created.TryAdd(userName, 0)
            ? await CreateAccountAsync(http, userName, role)
            : FullAccess(userName, await LogInAsync(http, userName, _password));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _signedIn.GetOrAdd(userName, new ApiClient(http));
    }

    /// <summary>
    /// Forgets the cached sign-ins, for a test that signed everybody out: the next <see cref="SignInAsync"/> of each
    /// account signs in again.
    /// </summary>
    public void ForgetSignIns() => _signedIn.Clear();

    /// <summary>
    /// A new staff account with the role and the run's password, for a test that changes the account itself (its
    /// sessions, its two-step verification), so the seeded accounts other tests use are left alone.
    /// </summary>
    public async Task CreateStaffAccountAsync(string userName, string role)
    {
        using var http = CreateHttpClient(ownAddress: true);
        _created.TryAdd(userName, 0);
        await CreateAccountAsync(http, userName, role);
    }

    /// <summary>
    /// A new sign-in of a staff account with a session of its own: a client for it, and the refresh cookie the server set
    /// (<c>name=value</c>). It does not disturb the account's cached client, and it comes from the test's own address.
    /// </summary>
    public async Task<(ApiClient Client, string RefreshCookie)> SignInNewSessionAsync(string userName)
    {
        var http = CreateHttpClient(ownAddress: true);
        using var response = await PostLoginAsync(http, userName, _password);
        var result = await ApiClient.ReadAsync<SignInResult>(response, HttpMethod.Post, "/api/v1/auth/login");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FullAccess(userName, result));
        return (new ApiClient(http), RefreshCookieOf(response) ?? throw new InvalidOperationException($"Signing in as '{userName}' set no refresh cookie."));
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

        await DeleteDataDirectoryAsync();
    }

    /// <summary>
    /// Deletes the run's data directory. The server has closed its files by now (the PDF renderer waits for the browser's
    /// processes to exit), but on Windows a virus scanner or the search indexer can still hold one for a moment, so the
    /// delete is tried for a few seconds; a folder still in use after that is left in the temp directory rather than
    /// failing a run whose tests all finished.
    /// </summary>
    private async Task DeleteDataDirectoryAsync()
    {
        for (var attempt = 1; Directory.Exists(_dataDirectory); attempt++)
        {
            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 20)
                {
                    TestContext.Current.SendDiagnosticMessage($"The test data directory {_dataDirectory} was left behind: {ex.Message}");
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }
    }

    /// <summary>
    /// A new sign-in of a seeded staff account, with a session of its own: the refresh cookie the server set, as
    /// <c>name=value</c>, for tests of the refresh flow. It does not disturb the account's cached client. Like
    /// <see cref="RefreshAsync"/>, it comes from the test's own address, so the test has a rate-limit budget of its own:
    /// a refresh cannot wait out a 429, because the wait could outlast the refresh grace the test is about.
    /// </summary>
    public async Task<string> SignInForRefreshCookieAsync(string userName)
    {
        using var http = CreateHttpClient(ownAddress: true);
        using var response = await PostLoginAsync(http, userName, _password);
        await ApiClient.ReadAsync<SignInResult>(response, HttpMethod.Post, "/api/v1/auth/login");
        return RefreshCookieOf(response) ?? throw new InvalidOperationException($"Signing in as '{userName}' set no refresh cookie.");
    }

    /// <summary>
    /// A new sign-in of a seeded staff account presenting an enrolled device's credential, as HC360 Desktop does: a
    /// client for the new session. It comes from the test's own address, so the test has a rate-limit budget of its
    /// own.
    /// </summary>
    public async Task<ApiClient> SignInFromDeviceAsync(string userName, Guid deviceId, string deviceSecret)
    {
        var ct = TestContext.Current.CancellationToken;
        var http = CreateHttpClient(ownAddress: true);
        using var response = await http.PostAsJsonAsync("/api/v1/auth/login",
            new LoginRequest(userName, _password, DeviceId: deviceId, DeviceSecret: deviceSecret), ApiClient.Json, ct);
        var result = await ApiClient.ReadAsync<SignInResult>(response, HttpMethod.Post, "/api/v1/auth/login");
        var token = FullAccess(userName, result);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new ApiClient(http);
    }

    /// <summary>
    /// A refresh of the session behind <paramref name="refreshCookie"/>, as the staff app sends it: with
    /// <paramref name="idleSeconds"/>, saying how long nobody has used the app.
    /// </summary>
    public async Task<HttpResponseMessage> RefreshAsync(string refreshCookie, int? idleSeconds = null)
    {
        using var http = CreateHttpClient(ownAddress: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", refreshCookie);
        if (idleSeconds is { } idle)
        {
            request.Headers.Add(AuthService.IdleHeader, idle.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return await http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>The staff refresh cookie a response set, as <c>name=value</c>; null when it set none (or cleared it).</summary>
    public static string? RefreshCookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith("hc360_rt=", StringComparison.Ordinal) && c.Length > "hc360_rt=".Length)
            : null;

    private static async Task<SignInResult> LogInAsync(HttpClient http, string userName, string password)
    {
        using var response = await PostLoginAsync(http, userName, password);
        return await ApiClient.ReadAsync<SignInResult>(response, HttpMethod.Post, "/api/v1/auth/login");
    }

    // Sign-in allows 10 attempts a minute per address, and test-server requests share one unless a test has its own (see
    // ClientAddressHeader): past that, wait for the next minute rather than fail the run.
    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient http, string userName, string password)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await http.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(userName, password), ApiClient.Json, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1);
            response.Dispose();
            await Task.Delay(wait + TimeSpan.FromSeconds(1), ct);
            response = await http.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(userName, password), ApiClient.Json, ct);
        }

        return response;
    }

    private static string FullAccess(string userName, SignInResult result) =>
        result.AccessToken is { Length: > 0 } token && !result.MfaRequired && result.Restriction is null
            ? token
            : throw new InvalidOperationException($"'{userName}' could not sign in fully (MFA required: {result.MfaRequired}, restriction: {result.Restriction}).");

    /// <summary>
    /// An account for a role the demonstration seed has none for. The hospital administrator creates it through the admin
    /// API with a temporary password, and the first sign-in, which may do nothing else, sets the run's password — as a new
    /// member of staff would.
    /// </summary>
    private async Task<string> CreateAccountAsync(HttpClient http, string userName, string role)
    {
        var admin = await SignInAsync(Users.HospitalAdmin);
        var roleId = (await admin.GetAsync<List<RoleListItem>>("/api/v1/admin/roles")).Single(r => r.Name == role).Id;
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");
        var temporary = "It-first-" + Guid.NewGuid().ToString("N");
        await admin.PostAsync<TemporaryPasswordResponse>("/api/v1/admin/users", new CreateUserRequest(
            userName, $"Demo {role}", Email: null, Phone: null, [roleId], [facilityId], facilityId, PractitionerId: null, temporary));

        var first = await LogInAsync(http, userName, temporary);
        if (first.Restriction != AuthService.RestrictionPasswordChange || first.AccessToken is not { Length: > 0 } restricted)
        {
            throw new InvalidOperationException($"The new account '{userName}' should first have to change its password (restriction: {first.Restriction}).");
        }

        using var response = await PostChangePasswordAsync(http, restricted, temporary);
        return FullAccess(userName, await ApiClient.ReadAsync<SignInResult>(response, HttpMethod.Post, "/api/v1/auth/change-password"));
    }

    // Changing the password shares sign-in's budget of 10 a minute per address: past that, wait for the next minute, as a
    // sign-in does, rather than leave the account half made.
    private async Task<HttpResponseMessage> PostChangePasswordAsync(HttpClient http, string restricted, string temporary)
    {
        var ct = TestContext.Current.CancellationToken;
        HttpRequestMessage Request()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password")
            {
                Content = JsonContent.Create(new ChangePasswordRequest(temporary, _password), options: ApiClient.Json),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", restricted);
            return request;
        }

        using (var first = Request())
        {
            var response = await http.SendAsync(first, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1);
            response.Dispose();
            await Task.Delay(wait + TimeSpan.FromSeconds(1), ct);
        }

        using var retry = Request();
        return await http.SendAsync(retry, ct);
    }

    /// <summary>
    /// A patient signed in to the portal with a one-time code, as a new portal account for a number seen for the first
    /// time. The test server sends no SMS: the code is read from the delivery waiting in the outbox. The requests come from
    /// the test's own address: one-time codes allow only 5 requests per address in 15 minutes.
    /// </summary>
    public async Task<ApiClient> SignInToPortalAsync(string phone)
    {
        var ct = TestContext.Current.CancellationToken;
        var http = CreateHttpClient(ownAddress: true);
        OtpChallengeResponse challenge;
        using (var requested = await http.PostAsJsonAsync("/api/v1/auth/otp/request", new OtpRequest(phone), ApiClient.Json, ct))
        {
            challenge = await ApiClient.ReadAsync<OtpChallengeResponse>(requested, HttpMethod.Post, "/api/v1/auth/otp/request");
        }

        using var verified = await http.PostAsJsonAsync("/api/v1/auth/otp/verify", new OtpVerifyRequest(challenge.ChallengeId, await ReadOtpAsync(phone, ct)), ApiClient.Json, ct);
        var result = await ApiClient.ReadAsync<SignInResult>(verified, HttpMethod.Post, "/api/v1/auth/otp/verify");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FullAccess(phone, result));
        return new ApiClient(http);
    }

    private async Task<string> ReadOtpAsync(string phone, CancellationToken ct)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var deliveries = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == NotificationService.SmsType)
            .OrderByDescending(m => m.CreatedAt)
            .Take(50)
            .ToListAsync(ct);
        return deliveries.Select(m => m.Read<NotificationDelivery>())
            .First(d => d.TemplateKey == "auth.otp" && d.To == Phone.Normalize(phone))
            .Values["code"];
    }

    private HttpClient CreateHttpClient(bool ownAddress = false)
    {
        var http = App.GetTestClient();
        http.DefaultRequestHeaders.Add("X-HC360-Client", "web");
        if (ownAddress)
        {
            http.DefaultRequestHeaders.Add(ClientAddressHeader, AddressOfCurrentTest());
        }

        return http;
    }

    // A private address that stays the same for the whole of the running test, and differs from other tests'.
    private static string AddressOfCurrentTest()
    {
        var test = TestContext.Current.Test?.UniqueID ?? throw new InvalidOperationException("No test is running.");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(test));
        return $"10.{hash[0]}.{hash[1]}.{hash[2]}";
    }

    /// <summary>The staff accounts: one per role the demonstration seed creates, and those created on first use.</summary>
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

        /// <summary>
        /// Verifies and releases lab reports (the lab technician enters results but cannot sign them off). The demonstration
        /// seed has no pathologist, so the hospital administrator creates this account the first time a test signs in.
        /// </summary>
        public const string Pathologist = "pathologist";

        /// <summary>
        /// Codes episodes and keeps the ICD catalogue (Medical records). Created on first use, as the pathologist is.
        /// </summary>
        public const string MrdOfficer = "mrdofficer";

        /// <summary>
        /// A second medical records officer, who verifies the coding <see cref="MrdOfficer"/> did: a coder cannot verify
        /// their own. Created on first use.
        /// </summary>
        public const string MrdVerifier = "mrdverifier";

        /// <summary>Washes, packs and sterilizes the instrument trays in CSSD. Created on first use.</summary>
        public const string CssdTechnician = "cssdtech";

        /// <summary>Records ECGs and echos and attaches the machines' files. Created on first use.</summary>
        public const string CardiologyTechnician = "cardiotech";
    }

    private sealed record SignInResult(string? AccessToken, bool MfaRequired, string? Restriction);
}
