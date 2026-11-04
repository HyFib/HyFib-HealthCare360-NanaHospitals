using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Billing.Features.Packages;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Integrations.Payments;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.Platform.Updates;
using HyFib.HealthCare360.Server;
using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.WindowsInterop.Updates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CheckupPackageDto = HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups.PackageDto;
using CheckupPackageRequest = HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups.SavePackageRequest;

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
        [Users.SecondPathologist] = SystemRoles.Pathologist,
        [Users.MrdOfficer] = SystemRoles.MrdOfficer,
        [Users.MrdVerifier] = SystemRoles.MrdOfficer,
        [Users.CssdTechnician] = SystemRoles.CssdTechnician,
        [Users.CardiologyTechnician] = SystemRoles.CardiologyTechnician,
        [Users.SecondDoctor] = SystemRoles.Doctor,
        [Users.SecondNurse] = SystemRoles.Nurse,
        [Users.Accountant] = SystemRoles.Accountant,
        [Users.Radiographer] = SystemRoles.Radiographer,
        [Users.MisAnalyst] = SystemRoles.MisAnalyst,
        [Users.Cashier] = SystemRoles.Cashier,
        [Users.CanteenManager] = SystemRoles.CanteenManager,
    };

    // A test-only password for the staff accounts, new for every run.
    private readonly string _password = "It-" + Guid.NewGuid().ToString("N");

    // The workstation IT enrolled, which every sign-in comes from unless a test signs in from a browser: administration
    // works only on an enrolled HC360 Desktop (AdministrationOnDesktop, SRS SEC-08).
    private readonly string _desktopSecret = SecretTokens.NewOpaqueToken(32);
    private Guid _desktopId;
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hc360-tests", "it-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentDictionary<string, ApiClient> _signedIn = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _created = new(StringComparer.Ordinal);
    private TestDatabase? _database;
    private WebApplication? _app;

    public IServiceProvider Services => App.Services;

    /// <summary>The ABDM gateway the server calls (<see cref="FakeAbdmGateway"/>).</summary>
    public FakeAbdmGateway AbdmGateway { get; } = new();

    /// <summary>Cashfree's API the server calls when a test sets Cashfree up (<see cref="FakeCashfree"/>).</summary>
    public FakeCashfree Cashfree { get; } = new();

    /// <summary>
    /// A release signing key made for this run, which the test server trusts as it trusts HyFib's (DesktopUpdateTests).
    /// </summary>
    public ECDsa ReleaseSigningKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The run's password of the seeded staff accounts, for a test that signs in on its own.</summary>
    public string Password => _password;

    /// <summary>The enrolled workstation the sign-ins come from (<see cref="SignInFromBrowserAsync"/> aside): its id and secret.</summary>
    public (Guid Id, string Secret) Desktop => (_desktopId, _desktopSecret);

    /// <summary>Every endpoint the server maps, as routing sees them.</summary>
    public IReadOnlyList<RouteEndpoint> Endpoints =>
        [.. ((IEndpointRouteBuilder)App).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()];

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
            ["HealthCare360:TimeSync:Enabled"] = "false",
            ["HealthCare360:Backup:Enabled"] = "false",
            ["HealthCare360:Backup:BeforeMigrations"] = "false",
        });

        // As Program.cs does, except that no certificate is created: the test server does not listen on the network.
        var options = builder.Configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>()!;
        var directories = new DataDirectories(options);
        directories.EnsureCreated();
        var certificates = new LanCertificateManager(directories, options, new EphemeralDataProtectionProvider(), NullLogger<LanCertificateManager>.Instance);

        // HC360 Desktop packages signed with a throwaway key made for this run (DesktopUpdateTests) are trusted too, as
        // HyFib's are; the server registers only the built-in keys when nothing was registered before.
        builder.Services.AddSingleton(new ReleaseTrust([.. ReleaseKeys.Trusted, ReleaseKey.FromSubjectPublicKeyInfo(ReleaseSigningKey.ExportSubjectPublicKeyInfo())]));
        builder.AddServerApplication(certificates);

        // The ABDM gateway is a fake one of this run (AbdmLinkingTests): the gateway client, the key-set fetch and the
        // pushes to an HIU send their requests to it, and nothing goes to the network.
        builder.Services.AddHttpClient<HttpAbdmGatewayClient>().ConfigurePrimaryHttpMessageHandler(AbdmGateway.Handler);
        builder.Services.AddHttpClient<AbdmGatewayKeys>().ConfigurePrimaryHttpMessageHandler(AbdmGateway.Handler);
        builder.Services.AddHttpClient<AbdmDataPush>().ConfigurePrimaryHttpMessageHandler(AbdmGateway.Handler);
        builder.Services.AddHttpClient<AbhaClient>().ConfigurePrimaryHttpMessageHandler(AbdmGateway.Handler);

        // Cashfree is a fake one of this run (GatewayReconciliationTests): nothing goes to the network.
        builder.Services.AddHttpClient<CashfreeGateway>().ConfigurePrimaryHttpMessageHandler(Cashfree.Handler);

        // PDFs as a test asks for them in a request header (TestPdfRenderer); the server's own renderer for everything else.
        var renderer = builder.Services.Last(d => d.ServiceType == typeof(IPdfRenderer));
        builder.Services.Remove(renderer);
        builder.Services.AddSingleton<IPdfRenderer>(services => new TestPdfRenderer(
            (IPdfRenderer)(renderer.ImplementationInstance
                ?? renderer.ImplementationFactory?.Invoke(services)
                ?? ActivatorUtilities.CreateInstance(services, renderer.ImplementationType!)),
            services.GetRequiredService<IHttpContextAccessor>()));

        _app = builder.Build();
        var platform = _app.Services.GetRequiredService<IOptions<PlatformOptions>>().Value;
        _app.Use((context, next) =>
        {
            if (IPAddress.TryParse(context.Request.Headers[ClientAddressHeader].ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            // The address a client used, which Kestrel tells by the port it arrived on (ListenerPaths): the port in the
            // client's base address, and the staff app's when it has none.
            context.Connection.LocalPort = context.Request.Host.Port ?? platform.HttpsPort;
            return next(context);
        });
        _app.UseListenerGate();
        _app.UseServerApplication();
        await _app.InitializeDatabaseAsync(ct);

        // Many tests sign in the same seeded accounts, each with a session of its own, while other tests keep using the
        // cached ones: no limit on sessions per account, except in a test that sets one (SessionControlTests).
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(AuthService.MaxSessionsSetting, 0, cancellationToken: ct);

            var now = DateTimeOffset.UtcNow;
            var desktop = new Device
            {
                Name = "IT workstation",
                MachineName = "IT-01",
                Kind = DeviceKind.Workstation,
                SecretHash = SecretTokens.Hash(_desktopSecret),
                EnrolledAt = now,
                LastSeenAt = now,
            };
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.Add(desktop);
            await db.SaveChangesAsync(ct);
            _desktopId = desktop.Id;
        }

        await _app.StartAsync(ct);
    }

    /// <summary>
    /// A caller who has not signed in. With <paramref name="ownAddress"/> its requests come from the test's own address,
    /// for calls limited per address (one-time codes allow only 5 requests per address in 15 minutes).
    /// </summary>
    public ApiClient Anonymous(bool ownAddress = false) => new(CreateHttpClient(ownAddress));

    /// <summary>
    /// A visitor to the patient portal's address (<see cref="PortalAddress"/>), who has not signed in. With
    /// <paramref name="ownAddress"/>, as for <see cref="Anonymous"/>.
    /// </summary>
    public ApiClient AnonymousOnPortal(bool ownAddress = false) => new(CreateHttpClient(ownAddress, portal: true));

    /// <summary>
    /// The patient portal's address, an origin of its own (<see cref="ListenerPaths"/>). Clients made without a base
    /// address are on the staff app's.
    /// </summary>
    public Uri PortalAddress => new($"https://localhost:{Platform.PortalHttpsPort}/");

    /// <summary>The API reference's address (<see cref="ListenerPaths"/>), on because the test server runs as development.</summary>
    public Uri ApiReferenceAddress => new($"https://localhost:{Platform.ApiReferencePort}/");

    private PlatformOptions Platform => Services.GetRequiredService<IOptions<PlatformOptions>>().Value;

    /// <summary>
    /// The server as a hospital's live server, until the result is disposed: one set up without demonstration data
    /// (<see cref="PlatformOptions.SeedDemoData"/>), where what only a training or test server allows is refused, such as
    /// the integrations' simulators. Those rules read the setting at each request, and tests run one at a time, so only
    /// the test that asked sees the change.
    /// </summary>
    public IDisposable AsLiveServer()
    {
        var platform = Platform;
        var before = platform.SeedDemoData;
        platform.SeedDemoData = false;
        return new Restore(() => platform.SeedDemoData = before);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>A caller who has not signed in, at one of the server's addresses, from the test's own address.</summary>
    public ApiClient AnonymousAt(Uri address)
    {
        var http = CreateHttpClient(ownAddress: true);
        http.BaseAddress = address;
        return new ApiClient(http);
    }

    /// <summary>The sign-in of <paramref name="client"/>, its bearer token, used on the staff app's address.</summary>
    public ApiClient OnStaffAddress(ApiClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var http = CreateHttpClient();
        http.DefaultRequestHeaders.Authorization = client.Http.DefaultRequestHeaders.Authorization;
        return new ApiClient(http);
    }

    /// <summary>
    /// The sign-in of <paramref name="client"/>, its bearer token, from the test's own address: for a test whose calls
    /// count against an address's sign-in limit (a witness typing their password at this user's screen), so that they do
    /// not use up the address the other tests sign in from.
    /// </summary>
    public ApiClient FromOwnAddress(ApiClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var http = CreateHttpClient(ownAddress: true);
        http.DefaultRequestHeaders.Authorization = client.Http.DefaultRequestHeaders.Authorization;
        return new ApiClient(http);
    }

    /// <summary>
    /// A caller who has not signed in, from <paramref name="address"/>: for a test that signs in more often than one
    /// address may in a minute, each attempt from an address of its own.
    /// </summary>
    public ApiClient AnonymousFrom(string address)
    {
        var http = CreateHttpClient();
        http.DefaultRequestHeaders.Add(ClientAddressHeader, address);
        return new ApiClient(http);
    }

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

        ReleaseSigningKey.Dispose();
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
    /// A new sign-in of a staff account in a browser, on no enrolled device: a client for the new session, and the refresh
    /// cookie the server set (<c>name=value</c>). It comes from the test's own address, so the test has a rate-limit budget
    /// of its own.
    /// </summary>
    public async Task<(ApiClient Client, string RefreshCookie)> SignInFromBrowserAsync(string userName)
    {
        var http = CreateHttpClient(ownAddress: true);
        using var response = await PostLoginAsync(http, userName, _password, fromBrowser: true);
        var result = await ApiClient.ReadAsync<SignInResult>(response, HttpMethod.Post, "/api/v1/auth/login");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FullAccess(userName, result));
        return (new ApiClient(http), RefreshCookieOf(response) ?? throw new InvalidOperationException($"Signing in as '{userName}' set no refresh cookie."));
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
    public static string? RefreshCookieOf(HttpResponseMessage response) => CookieOf(response, AuthService.RefreshCookieName);

    /// <summary>The portal's refresh cookie a response set, as <c>name=value</c>; null when it set none (or cleared it).</summary>
    public static string? PortalRefreshCookieOf(HttpResponseMessage response) => CookieOf(response, AuthService.PortalRefreshCookieName);

    private static string? CookieOf(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith(name + "=", StringComparison.Ordinal) && c.Length > name.Length + 1)
            : null;

    private async Task<SignInResult> LogInAsync(HttpClient http, string userName, string password)
    {
        using var response = await PostLoginAsync(http, userName, password);
        return await ApiClient.ReadAsync<SignInResult>(response, HttpMethod.Post, "/api/v1/auth/login");
    }

    // Sign-in allows 10 attempts a minute per address, and test-server requests share one unless a test has its own (see
    // ClientAddressHeader): past that, wait for the next minute rather than fail the run. From the enrolled workstation
    // (Desktop), as HC360 Desktop signs in, unless fromBrowser.
    private async Task<HttpResponseMessage> PostLoginAsync(HttpClient http, string userName, string password, bool fromBrowser = false)
    {
        var ct = TestContext.Current.CancellationToken;
        var request = fromBrowser ? new LoginRequest(userName, password) : new LoginRequest(userName, password, DeviceId: _desktopId, DeviceSecret: _desktopSecret);
        var response = await http.PostAsJsonAsync("/api/v1/auth/login", request, ApiClient.Json, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(1);
            response.Dispose();
            await Task.Delay(wait + TimeSpan.FromSeconds(1), ct);
            response = await http.PostAsJsonAsync("/api/v1/auth/login", request, ApiClient.Json, ct);
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
        var practitionerId = userName == Users.SecondDoctor ? await SecondConsultantAsync() : null;
        await CreateUserAsync(admin, new CreateUserRequest(
            userName, $"Demo {role}", Email: null, Phone: null, [roleId], [facilityId], facilityId, practitionerId, temporary));

        var first = await LogInAsync(http, userName, temporary);
        if (first.Restriction != AuthService.RestrictionPasswordChange || first.AccessToken is not { Length: > 0 } restricted)
        {
            throw new InvalidOperationException($"The new account '{userName}' should first have to change its password (restriction: {first.Restriction}).");
        }

        using var response = await PostChangePasswordAsync(http, restricted, temporary);
        return FullAccess(userName, await ApiClient.ReadAsync<SignInResult>(response, HttpMethod.Post, "/api/v1/auth/change-password"));
    }

    /// <summary>
    /// Access is given by two people: approves the grant <paramref name="approvalId"/> as whichever of the seed's two
    /// administrators (the Super Administrator and the hospital administrator) did not ask for it, as a second
    /// administrator approves a new member of staff's roles.
    /// </summary>
    public async Task ApproveGrantAsync(Guid approvalId)
    {
        var checker = await SignInAsync(Users.Admin);
        var request = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        if (!request.CanDecide)
        {
            checker = await SignInAsync(Users.HospitalAdmin);
            request = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        }

        await checker.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}/approve", new ApproveRequest("Approved for the tests.", request.Version));
    }

    /// <summary>
    /// A price changes only when two people agree: approves the price <paramref name="approvalId"/> asks for as whichever of
    /// the seed's two administrators did not ask for it (both may approve prices).
    /// </summary>
    public Task ApprovePriceAsync(Guid approvalId) => ApproveGrantAsync(approvalId);

    /// <summary>
    /// A service added through the masters API, its price approved by the other administrator, so it is in use at that
    /// price as a test expects: the service as it is then.
    /// </summary>
    public async Task<ServiceItemDto> CreateServiceAsync(ApiClient maker, SaveServiceItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(maker);
        var created = await maker.PostAsync<CreatedService>("/api/v1/masters/services", request);
        if (created.ApprovalId is { } approval)
        {
            await ApprovePriceAsync(approval);
        }

        return await maker.GetAsync<ServiceItemDto>($"/api/v1/masters/services/{created.Id}");
    }

    private sealed record CreatedService(Guid Id, Guid? ApprovalId);

    /// <summary>
    /// A tariff plan saved through the billing API, new or (with <paramref name="planId"/>) existing, its terms approved by
    /// the other administrator when they wait (a plan's terms change only when two people agree): the plan as it is then.
    /// </summary>
    public async Task<TariffPlanDetail> SaveTariffPlanAsync(ApiClient maker, SaveTariffPlanRequest request, Guid? planId = null)
    {
        ArgumentNullException.ThrowIfNull(maker);
        if (planId is { } existing)
        {
            using var response = await maker.SendRawAsync(HttpMethod.Put, $"/api/v1/billing/tariffs/{existing}", request);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Saving tariff plan {existing}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                await ApprovePriceAsync((await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json))!.Id);
            }

            return await maker.GetAsync<TariffPlanDetail>($"/api/v1/billing/tariffs/{existing}");
        }

        var created = await maker.PostAsync<TariffPlanDetail>("/api/v1/billing/tariffs", request);
        if (created.PendingTerms is { } pending)
        {
            await ApprovePriceAsync(pending.ApprovalId);
        }

        return await maker.GetAsync<TariffPlanDetail>($"/api/v1/billing/tariffs/{created.Id}");
    }

    /// <summary>
    /// Prices of a tariff plan asked for through the billing API as a bulk revision (which replaces the prices it lists on
    /// purpose) and approved by the other administrator: a plan's prices change only when two people agree.
    /// </summary>
    public async Task SaveTariffPricesAsync(ApiClient maker, Guid planId, params SaveTariffPriceRequest[] prices)
    {
        ArgumentNullException.ThrowIfNull(maker);
        ArgumentNullException.ThrowIfNull(prices);
        var approval = await maker.PostAsync<ApprovalRequestDto>($"/api/v1/billing/tariffs/{planId}/prices/bulk", new BulkTariffPricesRequest(prices));
        await ApprovePriceAsync(approval.Id);
    }

    /// <summary>
    /// A payer added through the insurance API, its terms approved by the other administrator when they wait (what a payer's
    /// patients pay changes only when two people agree): the payer as it is then.
    /// </summary>
    public async Task<PayerDetail> CreatePayerAsync(ApiClient maker, SavePayerRequest request)
    {
        ArgumentNullException.ThrowIfNull(maker);
        var created = await maker.PostAsync<PayerDetail>("/api/v1/insurance/payers", request);
        if (created.PendingTerms is { } pending)
        {
            await ApprovePriceAsync(pending.ApprovalId);
        }

        return await maker.GetAsync<PayerDetail>($"/api/v1/insurance/payers/{created.Id}");
    }

    /// <summary>
    /// A health check-up package saved through the clinical API, new or (with <paramref name="packageId"/>) existing, its
    /// price approved by the other administrator when it waits (a check-up's price is its service's, which changes only
    /// when two people agree): the package as it is then.
    /// </summary>
    public async Task<CheckupPackageDto> SaveCheckupPackageAsync(ApiClient maker, CheckupPackageRequest request, Guid? packageId = null)
    {
        ArgumentNullException.ThrowIfNull(maker);
        const string Packages = "/api/v1/health-checkups/packages";
        if (packageId is { } existing)
        {
            using var response = await maker.SendRawAsync(HttpMethod.Put, $"{Packages}/{existing}", request);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Saving check-up package {existing}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                await ApprovePriceAsync((await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json))!.Id);
            }

            return await maker.GetAsync<CheckupPackageDto>($"{Packages}/{existing}");
        }

        var created = await maker.PostAsync<CheckupPackageDto>(Packages, request);
        if (created.PendingPrice is { } pending)
        {
            await ApprovePriceAsync(pending.ApprovalId);
        }

        return await maker.GetAsync<CheckupPackageDto>($"{Packages}/{created.Id}");
    }

    /// <summary>
    /// A billing package saved through the billing API, new or (with <paramref name="packageId"/>) existing, its price and
    /// cover approved by the other administrator when they wait (what a package's patients pay changes only when two people
    /// agree): the package as it is then.
    /// </summary>
    public async Task<PackageDetail> SavePackageAsync(ApiClient maker, SavePackageRequest request, Guid? packageId = null)
    {
        ArgumentNullException.ThrowIfNull(maker);
        if (packageId is { } existing)
        {
            using var response = await maker.SendRawAsync(HttpMethod.Put, $"/api/v1/billing/packages/{existing}", request);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Saving package {existing}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }

            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                await ApprovePriceAsync((await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json))!.Id);
            }

            return await maker.GetAsync<PackageDetail>($"/api/v1/billing/packages/{existing}");
        }

        var created = await maker.PostAsync<PackageDetail>("/api/v1/billing/packages", request);
        if (created.PendingTerms is { } pending)
        {
            await ApprovePriceAsync(pending.ApprovalId);
        }

        return await maker.GetAsync<PackageDetail>($"/api/v1/billing/packages/{created.Id}");
    }

    /// <summary>A member of staff created through the admin API, their roles and facilities approved by the other administrator.</summary>
    public async Task<TemporaryPasswordResponse> CreateUserAsync(ApiClient maker, CreateUserRequest request)
    {
        var created = await maker.PostAsync<TemporaryPasswordResponse>("/api/v1/admin/users", request);
        if (created.ApprovalId is { } approval)
        {
            await ApproveGrantAsync(approval);
        }

        return created;
    }

    /// <summary>A role created through the admin API, its permissions approved by the other administrator where one may.</summary>
    public async Task<RoleDetail> CreateRoleAsync(ApiClient maker, SaveRoleRequest request)
    {
        var role = await maker.PostAsync<RoleDetail>("/api/v1/admin/roles", request);
        if (role.PendingGrant is not { } pending)
        {
            return role;
        }

        await ApproveGrantAsync(pending.ApprovalId);
        return await maker.GetAsync<RoleDetail>($"/api/v1/admin/roles/{role.Id}");
    }

    /// <summary>
    /// A user or role changed through the admin API (PUT), whatever it added approved by the other administrator: the
    /// change is then as it would have been in one step.
    /// </summary>
    public async Task UpdateAndApproveAsync(ApiClient maker, string path, object body)
    {
        using var response = await maker.SendRawAsync(HttpMethod.Put, path, body);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            await ApproveGrantAsync((await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Put, path)).Id);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PUT {path} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }
    }

    // The seed signs the first consultant (by code) in as the doctor; the second doctor is the next one.
    private async Task<Guid?> SecondConsultantAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Practitioner>()
            .Where(p => p.Kind == PractitionerKind.Consultant).OrderBy(p => p.Code).Skip(1).Select(p => (Guid?)p.Id)
            .FirstAsync(TestContext.Current.CancellationToken);
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
    /// time, on the portal's address (<see cref="PortalAddress"/>). The test server sends no SMS: the code is read from the
    /// delivery waiting in the outbox. The requests come from the test's own address: one-time codes allow only 5 requests
    /// per address in 15 minutes.
    /// </summary>
    public async Task<ApiClient> SignInToPortalAsync(string phone) => (await SignInToPortalWithCookieAsync(phone)).Client;

    /// <summary><see cref="SignInToPortalAsync"/>, with the portal's refresh cookie the sign-in set (<c>name=value</c>).</summary>
    public async Task<(ApiClient Client, string RefreshCookie)> SignInToPortalWithCookieAsync(string phone)
    {
        var ct = TestContext.Current.CancellationToken;
        var http = CreateHttpClient(ownAddress: true, portal: true);
        OtpChallengeResponse challenge;
        using (var requested = await http.PostAsJsonAsync("/api/v1/auth/otp/request", new OtpRequest(phone), ApiClient.Json, ct))
        {
            challenge = await ApiClient.ReadAsync<OtpChallengeResponse>(requested, HttpMethod.Post, "/api/v1/auth/otp/request");
        }

        using var verified = await http.PostAsJsonAsync("/api/v1/auth/otp/verify", new OtpVerifyRequest(challenge.ChallengeId, await PortalCodeAsync(phone)), ApiClient.Json, ct);
        var result = await ApiClient.ReadAsync<SignInResult>(verified, HttpMethod.Post, "/api/v1/auth/otp/verify");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FullAccess(phone, result));
        return (new ApiClient(http), PortalRefreshCookieOf(verified) ?? throw new InvalidOperationException($"Signing in to the portal as {phone} set no refresh cookie."));
    }

    /// <summary>The last portal sign-in code sent to <paramref name="phone"/>: the test server sends no SMS, so it is read from the outbox.</summary>
    public async Task<string> PortalCodeAsync(string phone)
    {
        var ct = TestContext.Current.CancellationToken;
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

    /// <summary>
    /// The test server's handler, for a client of its own, such as the load test's (tools/HyFib.HealthCare360.LoadTest),
    /// as the staff app on the test's own address: sign-in is limited per address.
    /// </summary>
    public HttpMessageHandler CreateHandler() => new StaffAppClient(App.GetTestServer().CreateHandler(), AddressOfCurrentTest());

    /// <summary>
    /// A caller who has not signed in, from the test's own address, that sends no proof of work unless the test adds one
    /// (AntiAutomationTests). Every other client solves the server's challenges as the portal does (<see cref="BrowserProofHandler"/>).
    /// </summary>
    public ApiClient AnonymousWithoutProof(bool portal = true) => new(CreateHttpClient(ownAddress: true, portal: portal, solveChallenges: false));

    private HttpClient CreateHttpClient(bool ownAddress = false, bool portal = false, bool solveChallenges = true)
    {
        var server = App.GetTestServer();
        var http = solveChallenges
            ? new HttpClient(new BrowserProofHandler(server.CreateHandler())) { BaseAddress = server.BaseAddress }
            : App.GetTestClient();
        if (portal)
        {
            http.BaseAddress = PortalAddress;
        }

        http.DefaultRequestHeaders.Add("X-HC360-Client", portal ? "portal" : "web");
        if (ownAddress)
        {
            http.DefaultRequestHeaders.Add(ClientAddressHeader, AddressOfCurrentTest());
        }

        return http;
    }

    private sealed class StaffAppClient(HttpMessageHandler inner, string address) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("X-HC360-Client", "web");
            request.Headers.TryAddWithoutValidation(ClientAddressHeader, address);
            return base.SendAsync(request, cancellationToken);
        }
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
        /// A second pathologist, who approves corrections <see cref="Pathologist"/> asks for where the lab requires a
        /// separate verifier. Created on first use.
        /// </summary>
        public const string SecondPathologist = "pathologist2";

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

        /// <summary>
        /// A second doctor, linked to the next consultant after <see cref="Doctor"/>'s: may sign prescriptions, but not
        /// <see cref="Doctor"/>'s. Created on first use.
        /// </summary>
        public const string SecondDoctor = "doctor2";

        /// <summary>A second nurse, who witnesses <see cref="Nurse"/>'s controlled doses at her screen. Created on first use.</summary>
        public const string SecondNurse = "nurse2";

        /// <summary>
        /// Keeps the accounts: prepares doctor payouts, which someone else (the hospital administrator) approves. Created on
        /// first use.
        /// </summary>
        public const string Accountant = "accountant";

        /// <summary>Performs scans and records the woman's Form F declaration, but does not sign Form F. Created on first use.</summary>
        public const string Radiographer = "radiographer";

        /// <summary>Runs MIS reports and schedules them, but holds no finance reports. Created on first use.</summary>
        public const string MisAnalyst = "misanalyst";

        /// <summary>
        /// Takes payments at a counter of their own (no other test opens or closes this cashier's shift) and asks for
        /// refunds and credit notes, which an approver issues. Created on first use.
        /// </summary>
        public const string Cashier = "cashier";

        /// <summary>Keeps the canteen's menu and bills its sales. Created on first use.</summary>
        public const string CanteenManager = "canteen";
    }

    private sealed record SignInResult(string? AccessToken, bool MfaRequired, string? Restriction);
}
