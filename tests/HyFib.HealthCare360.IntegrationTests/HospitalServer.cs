using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Billing.Features.Packages;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Integrations.Payments;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
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
        [Users.SecondPharmacist] = SystemRoles.Pharmacist,
        [Users.Accountant] = SystemRoles.Accountant,
        [Users.HrManager] = SystemRoles.HrManager,
        [Users.Radiographer] = SystemRoles.Radiographer,
        [Users.MisAnalyst] = SystemRoles.MisAnalyst,
        [Users.Cashier] = SystemRoles.Cashier,
        [Users.CanteenManager] = SystemRoles.CanteenManager,
        [Users.QualityManager] = SystemRoles.QualityManager,
        [Users.SecondSystemAdmin] = SystemAdministrationRole,
        [Users.BloodBankOfficer] = SystemRoles.BloodBankOfficer,
        [Users.SecondBloodBankOfficer] = SystemRoles.BloodBankOfficer,
        [Users.InsuranceDesk] = SystemRoles.InsuranceDesk,
        [Users.SecondInsuranceDesk] = SystemRoles.InsuranceDesk,
    };

    /// <summary>
    /// The names of accounts created on first use where two hold the same role and a test reads which of them did what (the
    /// audit trail names both blood bank officers). Any other is "Demo" and its role.
    /// </summary>
    private static readonly Dictionary<string, string> NamesCreatedOnFirstUse = new(StringComparer.Ordinal)
    {
        [Users.BloodBankOfficer] = "Santosh Gaikwad",
        [Users.SecondBloodBankOfficer] = "Priya Nair",
        [Users.InsuranceDesk] = "Farah Qureshi",
        [Users.SecondInsuranceDesk] = "Vikram Joshi",
    };

    /// <summary>
    /// A role of the tests' own holding <c>admin.system.manage</c> alone, which only the Super Administrator holds by default:
    /// its member is a second person who may ask for, and approve, what two super administrators decide (how long the audit
    /// trail is kept, and its purge). It approves no grants, so who may approve access is as the seed has it.
    /// </summary>
    private const string SystemAdministrationRole = "IT system administration";

    /// <summary>
    /// The months of the audit events the server holds from before anything else (<see cref="PlantOldAuditMonthsAsync"/>):
    /// the start of the chain, which AuditRetentionTests purges once the audit trail is kept 8 years.
    /// </summary>
    public static readonly IReadOnlyList<DateOnly> OldAuditMonths = [new(2015, 3, 1), new(2015, 4, 1)];

    /// <summary>How many audit events <see cref="OldAuditMonths"/> hold: chain positions 1 to this.</summary>
    public const int OldAuditEvents = 3;

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

    /// <summary>
    /// Today in the hospital's calendar (IST), on the server's clock. No test starts near midnight
    /// (<see cref="AwayFromMidnightInIndiaAttribute"/>), so a test reads the same day from its start to its end.
    /// </summary>
    public DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Services.GetRequiredService<TimeProvider>().GetUtcNow(), HospitalTimeZone.Zone).DateTime);

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
            await PlantOldAuditMonthsAsync(scope.ServiceProvider, ct);
        }

        await _app.StartAsync(ct);
    }

    /// <summary>
    /// Audit events from March and April 2015, written before the server starts and so before anything is sealed, with ids
    /// below the seed's: the sealer seals in id order, so they take chain positions 1 to <see cref="OldAuditEvents"/>, and
    /// their months, partitions of their own. A hospital whose audit trail is that old can purge those months once it keeps
    /// the trail 8 years (AuditRetentionTests); no other test's rows are in them.
    /// </summary>
    private static async Task PlantOldAuditMonthsAsync(IServiceProvider services, CancellationToken ct)
    {
        await services.GetRequiredService<HospitalDbContext>().Database.ExecuteSqlRawAsync("""
            INSERT INTO audit.audit_events (id, occurred_at, category, action, severity, user_name, summary) OVERRIDING SYSTEM VALUE VALUES
                (-3, '2015-03-04 09:00:00+00', 'phi', 'patient.accessed', 'Info', 'doctor', 'Viewed the discharge summary (it: an old month)'),
                (-2, '2015-03-20 11:30:00+00', 'data', 'Bill.update', 'Info', 'billing', 'Changed a bill (it: an old month)'),
                (-1, '2015-04-08 16:15:00+00', 'auth', 'auth.login', 'Info', 'admin', 'Signed in (it: an old month)')
            """, ct);
        await services.GetRequiredService<PartitionMaintenance>().RunAsync(ct);
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
        // Only someone who holds admin.system.manage gives it: the Super Administrator, for the tests' role that holds it.
        var admin = await SignInAsync(role == SystemAdministrationRole ? Users.Admin : Users.HospitalAdmin);
        var roles = await admin.GetAsync<List<RoleListItem>>("/api/v1/admin/roles");
        var roleId = roles.SingleOrDefault(r => r.Name == role)?.Id
                     ?? (await CreateRoleAsync(admin, new SaveRoleRequest(role, "Asks for and approves what two super administrators decide (tests)",
                         [PlatformPermissions.SystemManage], Version: null))).Id;
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");
        var temporary = "It-first-" + Guid.NewGuid().ToString("N");
        var practitionerId = userName == Users.SecondDoctor ? await SecondConsultantAsync() : null;
        await CreateUserAsync(admin, new CreateUserRequest(
            userName, NamesCreatedOnFirstUse.GetValueOrDefault(userName, $"Demo {role}"), Email: null, Phone: null, [roleId], [facilityId], facilityId,
            practitionerId, temporary));

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
    public async Task<ApprovalRequestDto> ApproveGrantAsync(Guid approvalId)
    {
        var checker = await SignInAsync(Users.Admin);
        var request = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        if (!request.CanDecide)
        {
            checker = await SignInAsync(Users.HospitalAdmin);
            request = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        }

        return await checker.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}/approve", new ApproveRequest("Approved for the tests.", request.Version));
    }

    /// <summary>
    /// A member of staff's password reset by <paramref name="maker"/> as Administration › Users resets it: the temporary
    /// password, read where it went. The administrator is never shown it: it goes to the account's own mobile number or
    /// e-mail address (the test server sends nothing, so it is read from the outbox), or, for an account with neither, the
    /// reset waits for the other administrator, who is shown it once when they approve.
    /// </summary>
    public async Task<string> ResetPasswordAsync(ApiClient maker, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(maker);
        var path = $"/api/v1/admin/users/{userId}/reset-password";
        using var response = await maker.SendRawAsync(HttpMethod.Post, path);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var approved = await ApproveGrantAsync((await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, path)).Id);
            return approved.Handover?.Secret ?? throw new InvalidOperationException($"Approving the password reset of {userId} handed over no password ({approved.Status}: {approved.Outcome}).");
        }

        var reset = await ApiClient.ReadAsync<PasswordResetResponse>(response, HttpMethod.Post, path);
        return reset.TemporaryPassword ?? await TemporaryPasswordSentAsync(userId);
    }

    /// <summary>The last temporary password sent to the account's own mobile number or e-mail address: the test server sends nothing, so it is read from the outbox.</summary>
    public async Task<string> TemporaryPasswordSentAsync(Guid userId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var user = await db.Set<User>().AsNoTracking().SingleAsync(u => u.Id == userId, ct);
        var deliveries = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == NotificationService.SmsType || m.Type == NotificationService.EmailType)
            .OrderByDescending(m => m.CreatedAt)
            .Take(200)
            .ToListAsync(ct);
        return deliveries.Select(m => m.Read<NotificationDelivery>())
            .First(d => d.TemplateKey == "auth.temporary_password" && (d.To == user.Phone || d.To == user.Email))
            .Values["password"];
    }

    /// <summary>A member of staff's two-step verification reset by <paramref name="maker"/>, approved by the other administrator.</summary>
    public async Task ResetMfaAsync(ApiClient maker, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(maker);
        var path = $"/api/v1/admin/users/{userId}/reset-mfa";
        using var response = await maker.SendRawAsync(HttpMethod.Post, path);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            await ApproveGrantAsync((await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, path)).Id);
        }
        else if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"POST {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }
    }

    /// <summary>
    /// A device enrolment code made by <paramref name="maker"/> as Administration › Devices makes it. A workstation's or a
    /// tablet's, from which the hospital can be administered, waits for the other administrator once one is enrolled (the test
    /// server's own is): approved, and then shown to the administrator who asked.
    /// </summary>
    public async Task<EnrollmentCodeResponse> EnrollmentCodeAsync(ApiClient maker, CreateEnrollmentCodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(maker);
        const string path = "/api/v1/admin/devices/enrollment-codes";
        using var response = await maker.SendRawAsync(HttpMethod.Post, path, request);
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            return await ApiClient.ReadAsync<EnrollmentCodeResponse>(response, HttpMethod.Post, path);
        }

        var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, path);
        await ApproveGrantAsync(asked.Id);
        return await maker.PostAsync<EnrollmentCodeResponse>($"{path}/requests/{asked.Id}/code");
    }

    /// <summary>
    /// A practitioner saved through the masters API (PUT), a linked doctor's registration number, HPR ID or signature, which
    /// wait for a second administrator, approved by the other administrator: the record is then as it would have been in one
    /// step.
    /// </summary>
    public async Task SavePractitionerAsync(ApiClient maker, string path, object body)
    {
        ArgumentNullException.ThrowIfNull(maker);
        using var response = await maker.SendRawAsync(HttpMethod.Put, path, body);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            await ApproveGrantAsync((await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Put, path)).Id);
        }
        else if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PUT {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }
    }

    /// <summary>
    /// A price changes only when two people agree: approves the price <paramref name="approvalId"/> asks for as whichever of
    /// the seed's two administrators did not ask for it (both may approve prices).
    /// </summary>
    public Task<ApprovalRequestDto> ApprovePriceAsync(Guid approvalId) => ApproveGrantAsync(approvalId);

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

    /// <summary>
    /// A setting saved through the admin API as the settings screen saves it, with the version of the value saved now (or
    /// none when there is none yet), a change that waits for a second administrator (a control switched off, a limit
    /// raised, where money, patients' data or alerts go: SettingGuard) approved by the other administrator: the setting is
    /// then as the test asked, as it would have been in one step.
    /// </summary>
    public async Task SaveSettingAsync(ApiClient maker, string key, SaveSettingRequest request)
    {
        ArgumentNullException.ThrowIfNull(maker);
        ArgumentNullException.ThrowIfNull(request);
        var path = $"/api/v1/admin/settings/{key}";
        var asked = request with { Version = await maker.SettingVersionAsync(key, request.FacilityId), Reason = request.Reason ?? "Set up for the tests." };
        using var response = await maker.SendRawAsync(HttpMethod.Put, path, asked);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Saving setting {key}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            await ApproveSettingAsync((await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Put, path)).Id);
        }
    }

    /// <summary>
    /// Units leave quarantine only when a second blood bank officer verifies the donation's screening results: verified as
    /// <see cref="Users.SecondBloodBankOfficer"/>, or as <see cref="Users.BloodBankOfficer"/> when the second one entered
    /// them, over the donation as it is now.
    /// </summary>
    public async Task VerifyDonationAsync(Guid donationId)
    {
        var path = $"/api/v1/blood-bank/donations/{donationId}";
        foreach (var userName in new[] { Users.SecondBloodBankOfficer, Users.BloodBankOfficer })
        {
            var checker = await SignInAsync(userName);
            var me = await checker.GetAsync<AuthUser>("/api/v1/auth/me");
            var donation = await checker.GetAsync<DonationDetail>(path);
            if (donation.LabResultsByUserId != me.Id)
            {
                await checker.PostAsync($"{path}/lab-results/verify", new VerifyLabResultsRequest(donation.Version));
                return;
            }
        }
    }

    /// <summary>
    /// Grouping, cross-match and issue are not all one person: a unit cross-matched compatible (gel card) for a request as
    /// <see cref="Users.SecondBloodBankOfficer"/>, so the officer who grouped the patient may issue it.
    /// </summary>
    public async Task<CrossMatchDto> CrossMatchAsSecondOfficerAsync(Guid requestId, Guid unitId)
    {
        var checker = await SignInAsync(Users.SecondBloodBankOfficer);
        return await checker.PostAsync<CrossMatchDto>($"/api/v1/blood-bank/requests/{requestId}/crossmatches",
            new RecordCrossMatchRequest(unitId, CrossmatchMethod.GelCard, CrossmatchResult.Compatible, null));
    }

    /// <summary>
    /// Uncross-matched blood is released on the treating doctor's own authority: authorised for an emergency request as
    /// <see cref="Users.Doctor"/>, signed in as themselves.
    /// </summary>
    public async Task AuthoriseEmergencyReleaseAsync(Guid requestId, string reason)
    {
        var doctor = await SignInAsync(Users.Doctor);
        await doctor.PostAsync($"/api/v1/blood-bank/requests/{requestId}/emergency-release", new EmergencyReleaseRequest(reason));
    }

    /// <summary>
    /// A change to a setting approved as whichever of the seed's two administrators (the Super Administrator and the hospital
    /// administrator, both of whom may change settings) did not ask for it.
    /// </summary>
    public Task ApproveSettingAsync(Guid approvalId) => ApproveGrantAsync(approvalId);

    /// <summary>
    /// A member of staff created through the admin API, their roles and facilities, and the practitioner the account is linked
    /// to, approved by the other administrator.
    /// </summary>
    public async Task<TemporaryPasswordResponse> CreateUserAsync(ApiClient maker, CreateUserRequest request)
    {
        var created = await maker.PostAsync<TemporaryPasswordResponse>("/api/v1/admin/users", request);
        if (created.ApprovalId is { } approval)
        {
            await ApproveGrantAsync(approval);
        }

        if (created.PractitionerLinkApprovalId is { } link)
        {
            await ApproveGrantAsync(link);
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

            // One save of a user may ask for several things (roles, contact details, the practitioner): the answer names
            // the first, and the user's page lists the rest.
            if (path.StartsWith("/api/v1/admin/users/", StringComparison.Ordinal))
            {
                var user = await maker.GetAsync<UserDetail>(path);
                foreach (var waiting in user.PendingChanges ?? [])
                {
                    await ApproveGrantAsync(waiting.ApprovalId);
                }

                if (user.PendingGrant is { } grant)
                {
                    await ApproveGrantAsync(grant.ApprovalId);
                }
            }

            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PUT {path} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }
    }

    /// <summary>
    /// A pharmacy return taken as the counter takes it: asked for by <paramref name="maker"/>, who did not sell the invoice
    /// (the pharmacist who sold it may not take its returns), and, when it pays money out or writes stock off, approved by
    /// the accountant, who approves every refund: the return as it is then, as it would have been in one step.
    /// </summary>
    public async Task<SaleReturnDto> ReturnSaleAsync(ApiClient maker, Guid saleId, SaleReturnRequest request)
    {
        ArgumentNullException.ThrowIfNull(maker);
        var path = $"/api/v1/pharmacy/sales/{saleId}/returns";
        using var response = await maker.SendRawAsync(HttpMethod.Post, path, request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Returning on invoice {saleId}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            await ApproveRefundAsync((await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, path)).Id);
        }

        return (await maker.GetAsync<List<SaleReturnDto>>(path)).OrderBy(r => r.ReturnedAt).Last();
    }

    /// <summary>
    /// A same-day pharmacy invoice cancelled: asked for by <paramref name="maker"/>, who did not sell it, and approved by the
    /// accountant, who approves every refund.
    /// </summary>
    public async Task CancelSaleAsync(ApiClient maker, Guid saleId, string reason)
    {
        ArgumentNullException.ThrowIfNull(maker);
        var asked = await maker.PostAsync<ApprovalRequestDto>($"/api/v1/pharmacy/sales/{saleId}/cancel", new PharmacyReasonRequest(reason));
        await ApproveRefundAsync(asked.Id);
    }

    /// <summary>
    /// Approves a refund someone else asked for (a pharmacy cancellation or return) as the accountant, who holds
    /// <c>billing.refunds.approve</c> and sells nothing.
    /// </summary>
    public async Task<ApprovalRequestDto> ApproveRefundAsync(Guid approvalId)
    {
        var checker = await SignInAsync(Users.Accountant);
        var request = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        var approved = await checker.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}/approve", new ApproveRequest("Approved for the tests.", request.Version));
        return approved.Status == ApprovalStatus.Approved
            ? approved
            : throw new InvalidOperationException($"Approving {request.Summary}: {approved.Status} ({approved.Outcome})");
    }

    /// <summary>
    /// A user's discount limit saved through the billing API as Billing › Discounts saves it: lowered at once; raised, asked
    /// for by <paramref name="maker"/> and approved by whichever of the seed's two administrators did not ask (both approve
    /// discounts, and neither is the user whose limit it is). A limit raises only when two people agree (slice 6).
    /// </summary>
    public async Task SaveDiscountLimitAsync(ApiClient maker, SaveDiscountLimitRequest request)
    {
        ArgumentNullException.ThrowIfNull(maker);
        const string path = "/api/v1/billing/discount-limits/";
        using var response = await maker.SendRawAsync(HttpMethod.Put, path, request);
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            Approved(await ApproveGrantAsync((await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Put, path)).Id));
        }
        else if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PUT {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }
    }

    /// <summary>
    /// An unbilled charge cancelled at the billing desk: at once when it is small, or asked for by <paramref name="maker"/>
    /// and approved by the accountant, who approves discounts and cancels nothing: the charge cancelled either way.
    /// </summary>
    public async Task CancelChargeAsync(ApiClient maker, Guid chargeId, string reason)
    {
        ArgumentNullException.ThrowIfNull(maker);
        var path = $"/api/v1/billing/charges/{chargeId}/cancel";
        using var response = await maker.SendRawAsync(HttpMethod.Post, path, new CancelChargeRequest(reason));
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            await ApproveRefundAsync((await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, path)).Id);
        }
        else if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"POST {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }
    }

    /// <summary>
    /// A cashier's shift closed with <paramref name="declaredCash"/> in the drawer by <paramref name="maker"/>: a shortage
    /// beyond the tolerance is accepted by the accountant, a supervisor who is not the cashier. The shift as it is then.
    /// </summary>
    public async Task<ShiftDto> CloseShiftAsync(ApiClient maker, Guid shiftId, decimal declaredCash, string? notes)
    {
        ArgumentNullException.ThrowIfNull(maker);
        var shift = await maker.GetAsync<ShiftDto>($"/api/v1/billing/shifts/{shiftId}");
        var path = $"/api/v1/billing/shifts/{shiftId}/close";
        using var response = await maker.SendRawAsync(HttpMethod.Post, path, new CloseShiftRequest(declaredCash, notes, shift.Version));
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"POST {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }

        var closed = await ApiClient.ReadAsync<ShiftDto>(response, HttpMethod.Post, path);
        if (response.StatusCode == HttpStatusCode.Accepted && closed.ShortageApprovalId is { } approval)
        {
            await ApproveRefundAsync(approval);
        }

        return await maker.GetAsync<ShiftDto>($"/api/v1/billing/shifts/{shiftId}");
    }

    /// <summary>
    /// A practitioner added through the masters API, their consultation fees approved by the other administrator (a
    /// doctor's own fee is charged instead of the service's price, so it waits as a price does): the practitioner's id.
    /// </summary>
    public async Task<Guid> CreatePractitionerAsync(ApiClient maker, SavePractitionerRequest request)
    {
        ArgumentNullException.ThrowIfNull(maker);
        var created = await maker.PostAsync<CreatedPractitioner>("/api/v1/masters/practitioners", request);
        if (created.FeeApprovalId is { } approval)
        {
            Approved(await ApprovePriceAsync(approval));
        }

        return created.Id;
    }

    private sealed record CreatedPractitioner(Guid Id, Guid? FeeApprovalId);

    /// <summary>
    /// A practitioner saved through the masters API, what it asked for (consultation fees, a linked doctor's identity)
    /// approved by the other administrator: the record as it would have been in one step.
    /// </summary>
    public async Task SavePractitionerAsync(ApiClient maker, Guid practitionerId, SavePractitionerRequest request)
    {
        ArgumentNullException.ThrowIfNull(maker);
        var path = $"/api/v1/masters/practitioners/{practitionerId}";
        using var response = await maker.SendRawAsync(HttpMethod.Put, path, request);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"PUT {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }

        var saved = await maker.GetAsync<PractitionerDto>(path);
        foreach (var waiting in new[] { saved.PendingIdentity?.ApprovalId, saved.PendingFee?.ApprovalId }.OfType<Guid>())
        {
            Approved(await ApproveGrantAsync(waiting));
        }
    }

    private static ApprovalRequestDto Approved(ApprovalRequestDto approved) =>
        approved.Status == ApprovalStatus.Approved ? approved : throw new InvalidOperationException($"Approving {approved.Summary}: {approved.Status} ({approved.Outcome})");

    /// <summary>
    /// The approvals of a patient's record (slice 7) are decided by whoever of these may decide <paramref name="approvalId"/>
    /// and did not ask for it: the medical records officer (identity, deceased status, the MLC flag), the hospital
    /// administrator (a VIP restriction), the doctor or the second doctor (a sensitive category, an allergy, DNR), or the
    /// super administrator.
    /// </summary>
    public async Task<ApprovalRequestDto> ApproveRecordChangeAsync(Guid approvalId)
    {
        foreach (var userName in new[] { Users.MrdOfficer, Users.HospitalAdmin, Users.Doctor, Users.SecondDoctor, Users.Admin })
        {
            var checker = await SignInAsync(userName);

            // Someone who neither asked nor may decide it is not shown it.
            using var shown = await checker.SendRawAsync(HttpMethod.Get, $"/api/v1/approvals/{approvalId}", null);
            if (!shown.IsSuccessStatusCode)
            {
                continue;
            }

            var request = await ApiClient.ReadAsync<ApprovalRequestDto>(shown, HttpMethod.Get, $"/api/v1/approvals/{approvalId}");
            if (request.CanDecide)
            {
                return Approved(await checker.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}/approve", new ApproveRequest("Approved for the tests.", request.Version)));
            }
        }

        throw new InvalidOperationException($"Nobody the tests sign in as may decide approval {approvalId}.");
    }

    /// <summary>
    /// A write to a patient's record (a patient's details, sensitive categories, an allergy, an alert, an admission) sent by
    /// <paramref name="maker"/>; what it asked a second person for (202) approved by another (<see cref="ApproveRecordChangeAsync"/>),
    /// together with anything else the record's read shows waiting: the record as it would have been in one step.
    /// </summary>
    public async Task SaveRecordAsync(ApiClient maker, HttpMethod method, string path, object body)
    {
        ArgumentNullException.ThrowIfNull(maker);
        using var response = await maker.SendRawAsync(method, path, body);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{method} {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }

        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            return;
        }

        var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(response, method, path);
        await ApproveRecordChangeAsync(asked.Id);
        if (asked.SubjectType == "Patient")
        {
            // One save may ask for several changes (identity, restriction, deceased status): each is approved.
            var lister = await SignInAsync(Users.HospitalAdmin);
            foreach (var waiting in (await lister.GetAsync<PagedResult<ApprovalRequestDto>>($"/api/v1/approvals?view=Subject&subjectId={asked.SubjectId}&pageSize=50")).Items
                         .Where(r => r.Status == ApprovalStatus.Pending && r.Kind.StartsWith("patients.", StringComparison.Ordinal) && r.Kind != "patients.merge"))
            {
                await ApproveRecordChangeAsync(waiting.Id);
            }
        }
    }

    /// <summary>
    /// A witness (or the doctor who explained the risks) confirms at <paramref name="at"/>'s screen with their own user name
    /// and password (ADR-014), for <paramref name="purpose"/> and the patient: the confirmation <paramref name="at"/> then
    /// sends once with the record.
    /// </summary>
    public async Task<WitnessConfirmed> WitnessAsync(ApiClient at, string witnessUserName, string purpose, Guid patientId)
    {
        ArgumentNullException.ThrowIfNull(at);
        await SignInAsync(witnessUserName); // an account created on first use exists before it witnesses
        // From the test's own address: a witness's password counts against the address's sign-in limit.
        return await FromOwnAddress(at).PostAsync<WitnessConfirmed>("/api/v1/auth/witness", new ConfirmWitnessRequest(witnessUserName, _password, null, purpose, patientId));
    }

    /// <summary>
    /// A witness confirms at <paramref name="at"/>'s screen a consent to share the patient's whole record with no signed form
    /// (maker-checker, slice 14): the confirmation to send with the consent. <paramref name="witness"/> holds
    /// <c>patients.consents</c> and is not the one recording it.
    /// </summary>
    public async Task<Guid> RecordSharingWitnessAsync(ApiClient at, Guid patientId, string witness = Users.Nurse) =>
        (await WitnessAsync(at, witness, "consent.record_sharing", patientId)).ConfirmationId;

    /// <summary>
    /// The ward reports the patient absconded (maker-checker, slice 8): the nurse asks (202) with the report, and the nurse in
    /// charge (the hospital administrator holds <c>nursing.in_charge</c>) approves; the stay is then closed as absconded.
    /// </summary>
    public async Task AbscondAsync(ApiClient maker, Guid admissionId, string notes)
    {
        var asked = await AskAbscondedAsync(maker, admissionId, notes);
        await ApproveRecordChangeAsync(asked.Id);
    }

    /// <summary>The ward asks for the stay to be closed as absconded: the request, waiting for the nurse in charge.</summary>
    public async Task<ApprovalRequestDto> AskAbscondedAsync(ApiClient maker, Guid admissionId, string notes)
    {
        ArgumentNullException.ThrowIfNull(maker);
        var path = $"/api/v1/inpatient/admissions/{admissionId}/discharge/complete";
        using var response = await maker.SendRawAsync(HttpMethod.Post, path, new Modules.Inpatient.Features.Discharge.CompleteDischargeRequest(
            Modules.Inpatient.Domain.DischargeType.Absconded, null, notes, NotifyPatient: false,
            Absconded: new Modules.Inpatient.Domain.AbscondedReport(DateTimeOffset.UtcNow.AddMinutes(-30), "Ward staff and security searched the floor and the grounds",
                PoliceInformed: false, PoliceStation: null, PoliceReference: null, RelativesInformed: true, RelativeInformed: "The patient's son, by phone")));
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            throw new InvalidOperationException($"POST {path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }

        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, path);
    }

    /// <summary>
    /// A correction of an implant or consumable line after the operation started, approved by the hospital administrator, who
    /// holds the OT in-charge's approval and did not ask for it.
    /// </summary>
    public async Task<ApprovalRequestDto> ApproveMaterialCorrectionAsync(Guid approvalId)
    {
        var checker = await SignInAsync(Users.HospitalAdmin);
        var request = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        return await checker.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}/approve", new ApproveRequest("Checked against the pack label.", request.Version));
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

    /// <summary>
    /// The front desk shares a patient's records with a family member's portal account (maker-checker, slice 14): a code goes
    /// to the patient's own mobile number, and the desk enters it as the patient reads it out (read here from the outbox).
    /// </summary>
    public async Task<Modules.Patients.Features.Patients.FamilyLinkCreated> LinkFamilyAtDeskAsync(
        ApiClient staff, Guid patientId, Modules.Patients.Features.Patients.LinkFamilyMemberRequest request)
    {
        ArgumentNullException.ThrowIfNull(staff);
        ArgumentNullException.ThrowIfNull(request);
        var sent = await staff.PostAsync<Modules.Patients.Features.Patients.FamilyDeskCodeSent>($"/api/v1/patients/{patientId}/portal-family", request);
        return await staff.PostAsync<Modules.Patients.Features.Patients.FamilyLinkCreated>(
            $"/api/v1/patients/{patientId}/portal-family/codes/{sent.CodeId}/confirm",
            new Modules.Patients.Features.Patients.ConfirmFamilyLinkRequest(await DeskFamilyCodeAsync(patientId), request.ConsentGivenBy));
    }

    /// <summary>The last code the front desk had sent to the patient's own number to share their records: read from the outbox.</summary>
    public async Task<string> DeskFamilyCodeAsync(Guid patientId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var phone = await db.Set<Modules.Patients.Domain.Patient>().AsNoTracking().Where(p => p.Id == patientId).Select(p => p.Phone).SingleAsync(ct);
        var deliveries = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == NotificationService.SmsType)
            .OrderByDescending(m => m.CreatedAt)
            .Take(200)
            .ToListAsync(ct);
        return deliveries.Select(m => m.Read<NotificationDelivery>())
            .First(d => d.TemplateKey == "portal.family_code_desk" && d.To == phone)
            .Values["code"];
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
        /// A second pharmacist, who takes the returns and asks for the cancellations of what <see cref="Pharmacist"/> sold (no
        /// one cancels or pays back their own sale), and approves <see cref="Pharmacist"/>'s ward returns not restocked.
        /// Created on first use.
        /// </summary>
        public const string SecondPharmacist = "pharmacist2";

        /// <summary>
        /// Keeps the accounts: prepares doctor payouts, which someone else (the hospital administrator) approves. Created on
        /// first use.
        /// </summary>
        public const string Accountant = "accountant";

        /// <summary>Approves payroll the hospital administrator processed (maker-checker) and exports its files. Created on first use.</summary>
        public const string HrManager = "hrmanager";

        /// <summary>
        /// The insurance (TPA) desk: records pre-authorisations, claims and their settlements. Created on first use.
        /// </summary>
        public const string InsuranceDesk = "insurance";

        /// <summary>A second insurance desk user, who checks what the first recorded (MAKER-CHECKER.md, slice 12). Created on first use.</summary>
        public const string SecondInsuranceDesk = "insurance2";

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

        /// <summary>
        /// Keeps the incident register and its reports to regulators (MvPI), but may not open a restricted (VIP) record.
        /// Created on first use.
        /// </summary>
        public const string QualityManager = "qualitymanager";

        /// <summary>
        /// A second person who holds <c>admin.system.manage</c> beside the Super Administrator (<see cref="Admin"/>), in a role
        /// that holds it alone: approves how long the audit trail is kept, and its purge, that the other asks for. Created on
        /// first use, by the Super Administrator.
        /// </summary>
        public const string SecondSystemAdmin = "sysadmin2";

        /// <summary>
        /// Groups, cross-matches and issues blood and enters donations' screening results (Santosh Gaikwad). Created on first
        /// use.
        /// </summary>
        public const string BloodBankOfficer = "bloodbank";

        /// <summary>
        /// A second blood bank officer (Priya Nair), who verifies the screening results others enter before the units leave
        /// quarantine, and cross-matches what another grouped, so no unit goes out on one officer's checks. Created on first
        /// use.
        /// </summary>
        public const string SecondBloodBankOfficer = "bloodbank2";
    }

    private sealed record SignInResult(string? AccessToken, bool MfaRequired, string? Restriction);
}
