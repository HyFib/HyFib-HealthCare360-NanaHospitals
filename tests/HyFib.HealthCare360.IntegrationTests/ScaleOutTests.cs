using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS SCAL-01: several API nodes behind a reverse proxy, on one database, behave as one server. A permission taken away
/// or a session ended on one node stops working on every other at once (the cache signals, PostgreSQL LISTEN/NOTIFY);
/// a one-time token can be spent once and a count of attempts is one count, whichever node answers
/// (<see cref="IClusterStore"/>); and the per-address limit on sign-in is the hospital's, not each node's. The second
/// node is <see cref="SecondNode"/>, beside the test server.
/// </summary>
public sealed class ScaleOutTests(HospitalServer server)
{
    /// <summary>Well within a cached permission's two minutes or a cached session's thirty seconds.</summary>
    private static readonly TimeSpan AtOnce = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_permission_taken_away_on_one_node_is_gone_on_the_other_at_once()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var role = await admin.PostAsync<RoleDetail>("/api/v1/admin/roles", new SaveRoleRequest(
            "IT scale-out " + suffix, "Reads the user list", [PlatformPermissions.UsersRead], Version: null));
        var user = await admin.PostAsync<TemporaryPasswordResponse>("/api/v1/admin/users", NewUser("it-scale-" + suffix, role.Id));
        await using var other = await SecondNode.StartAsync(server, Ct);

        var cached = await other.Permissions.GetPermissionsAsync(user.UserId, Ct);
        cached.ShouldContain(PlatformPermissions.UsersRead);
        (await other.Permissions.GetPermissionsAsync(user.UserId, Ct)).ShouldBeSameAs(cached, "the other node keeps what it read for two minutes");

        // On the test server: the role loses the permission.
        await admin.PutAsync($"/api/v1/admin/roles/{role.Id}", new SaveRoleRequest(role.Name, role.Description, [PlatformPermissions.AuditRead], role.Version));

        (await SecondNode.EventuallyAsync(
                async () => !(await other.Permissions.GetPermissionsAsync(user.UserId, Ct)).Contains(PlatformPermissions.UsersRead), AtOnce, Ct))
            .ShouldBeTrue("the other node should drop the permission as soon as it is taken away");
        (await other.Permissions.GetPermissionsAsync(user.UserId, Ct)).ShouldContain(PlatformPermissions.AuditRead);
    }

    [Fact]
    public async Task A_session_ended_on_one_node_stops_working_on_the_other_at_once()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var (user, session) = await SignedInUserAsync(admin);
        await using var other = await SecondNode.StartAsync(server, Ct);
        (await other.Sessions.GetStateAsync(session, Ct)).ShouldBe(SessionState.Active);

        // On the test server: an administrator signs the user out everywhere.
        await admin.PostAsync($"/api/v1/admin/users/{user}/revoke-sessions");

        (await SecondNode.EventuallyAsync(async () => await other.Sessions.GetStateAsync(session, Ct) == SessionState.Ended, AtOnce, Ct))
            .ShouldBeTrue("the other node should refuse the session as soon as it ends");
    }

    /// <summary>
    /// The signal that a session ended goes out once the end is saved. Sent before, it would reach the other node while the
    /// session was still active in the database: a request there would read it as active and keep that for 30 seconds,
    /// with no signal after the save to undo it (a stolen device, or the account of someone who left, kept working).
    /// </summary>
    [Theory]
    [InlineData("revoke-sessions")]
    [InlineData("revoke-device")]
    public async Task A_session_read_on_another_node_while_it_is_being_ended_is_not_kept_there(string how)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var device = how == "revoke-device" ? await EnrolAsync(admin) : ((Guid Id, string Secret)?)null;
        var (user, session) = await SignedInUserAsync(admin, device);
        var heard = new SignalsHeard(SessionValidator.SignalTopic);
        await using var other = await SecondNode.StartAsync(server, Ct, services => services.AddSingleton<IClusterCache>(heard));
        (await other.Sessions.GetStateAsync(session, Ct)).ShouldBe(SessionState.Active);

        // The session's row is held, so the administrator's change waits at its save, past where a signal sent too early
        // would have gone.
        await using var holder = await OpenAsync();
        await using var hold = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM identity.user_sessions WHERE id = @id FOR UPDATE", holder, hold))
        {
            lockRow.Parameters.AddWithValue("id", session);
            await lockRow.ExecuteNonQueryAsync(Ct);
        }

        var ending = how == "revoke-device"
            ? admin.PostAsync($"/api/v1/admin/devices/{device!.Value.Id}/revoke")
            : admin.PostAsync($"/api/v1/admin/users/{user}/revoke-sessions");
        (await SecondNode.EventuallyAsync(() => IsWaitingForAsync(holder.ProcessID), TimeSpan.FromSeconds(30), Ct))
            .ShouldBeTrue("the change should be waiting for the session's row");

        // A request on the other node now, once any signal sent so far has arrived, finds the session still active.
        await SecondNode.EventuallyAsync(() => Task.FromResult(heard.Contains(session.ToString("N"))), TimeSpan.FromSeconds(2), Ct);
        (await other.Sessions.GetStateAsync(session, Ct)).ShouldBe(SessionState.Active, "the change is not saved yet");

        await hold.RollbackAsync(Ct);
        await ending;

        (await SecondNode.EventuallyAsync(async () => await other.Sessions.GetStateAsync(session, Ct) == SessionState.Ended, AtOnce, Ct))
            .ShouldBeTrue("the other node should refuse the session as soon as its end is saved");
    }

    [Fact]
    public async Task A_node_that_cannot_hear_the_others_reads_every_session_from_the_database()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var (_, session) = await SignedInUserAsync(admin);
        var deaf = new SessionValidator(new MemoryCache(new MemoryCacheOptions()), server.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, new DeafSignals());
        (await deaf.GetStateAsync(session, Ct)).ShouldBe(SessionState.Active);

        // Ended where this node would hear nothing of it.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserSession>().Where(s => s.Id == session)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), Ct);
        }

        (await deaf.GetStateAsync(session, Ct)).ShouldBe(SessionState.Ended);
    }

    [Fact]
    public async Task A_token_is_spent_once_whichever_node_spends_it()
    {
        await using var other = await SecondNode.StartAsync(server, Ct);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var (a, b) = (Store(server.Services, clock), Store(other.Services, clock));
        var key = "it:challenge:" + Guid.NewGuid().ToString("N");

        (await a.TryAddAsync(key, TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue();
        (await b.TryAddAsync(key, TimeSpan.FromMinutes(5), Ct)).ShouldBeFalse();
        (await a.TryAddAsync(key, TimeSpan.FromMinutes(5), Ct)).ShouldBeFalse();

        // Once it has ended it is not there any more, on either node.
        clock.Now += TimeSpan.FromMinutes(5);
        (await b.TryAddAsync(key, TimeSpan.FromMinutes(5), Ct)).ShouldBeTrue();
        (await a.TryAddAsync(key, TimeSpan.FromMinutes(5), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Attempts_made_on_every_node_at_once_are_each_counted_once()
    {
        await using var other = await SecondNode.StartAsync(server, Ct);
        var (a, b) = (Store(server.Services, TimeProvider.System), Store(other.Services, TimeProvider.System));
        var key = "it:failures:" + Guid.NewGuid().ToString("N");

        var counts = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => (i % 2 == 0 ? a : b).IncrementAsync(key, TimeSpan.FromMinutes(10), Ct)));

        counts.Select(c => c.Count).Order().ShouldBe(Enumerable.Range(1, 40).Select(i => (long)i));
        counts.Select(c => c.WindowEndsAt).Distinct().ShouldHaveSingleItem();
        (await b.GetCountAsync(key, Ct)).ShouldBe(40);

        await a.RemoveAsync(key, Ct);
        (await b.GetCountAsync(key, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_count_starts_again_when_its_window_ends()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = Store(server.Services, clock);
        var key = "it:window:" + Guid.NewGuid().ToString("N");

        var first = await store.IncrementAsync(key, TimeSpan.FromMinutes(1), Ct);
        clock.Now += TimeSpan.FromSeconds(59);
        var second = await store.IncrementAsync(key, TimeSpan.FromMinutes(1), Ct);
        clock.Now += TimeSpan.FromSeconds(1);
        (await store.GetCountAsync(key, Ct)).ShouldBe(0);
        var third = await store.IncrementAsync(key, TimeSpan.FromMinutes(1), Ct);

        (first.Count, second.Count, third.Count).ShouldBe((1, 2, 1));
        second.WindowEndsAt.ShouldBe(first.WindowEndsAt, TimeSpan.FromMilliseconds(1));
        third.WindowEndsAt.ShouldBe(clock.Now + TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task The_sign_in_limit_per_address_is_the_hospitals_not_each_nodes()
    {
        await using var first = await AddressLimitedNodeAsync(severalNodes: true);
        await using var second = await AddressLimitedNodeAsync(severalNodes: true);
        var address = NewAddress();

        var answers = new List<(HttpStatusCode Status, string? RetryAfter)>();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            answers.Add(await SignInAsync(attempt % 2 == 0 ? first : second, address));
        }

        // Each node has seen six attempts, under its own limit of ten; together they have seen twelve.
        answers.Take(ClusterRateLimits.SignIn.PermitLimit).ShouldAllBe(a => a.Status == HttpStatusCode.OK);
        answers.Skip(ClusterRateLimits.SignIn.PermitLimit).ShouldAllBe(a => a.Status == HttpStatusCode.TooManyRequests);
        int.Parse(answers[^1].RetryAfter.ShouldNotBeNull(), System.Globalization.CultureInfo.InvariantCulture).ShouldBeInRange(1, 60);

        // Another address has a budget of its own.
        (await SignInAsync(first, NewAddress())).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public void The_address_a_limit_counts_by_is_kept_as_a_keyed_hash_that_cannot_be_worked_back()
    {
        var address = NewAddress();
        var key = ClusterRateLimits.Key(ClusterRateLimits.SignIn, Address(address));

        key.ShouldStartWith("rate:auth:");
        key.ShouldNotContain(address);

        // Not a plain hash, which hashing every IPv4 address would give back.
        key.ShouldNotEndWith(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address)), 0, 16));
        ClusterRateLimits.Key(ClusterRateLimits.SignIn, Address(address)).ShouldBe(key, "every server counts the address under one key");
    }

    [Fact]
    public async Task A_single_server_counts_sign_in_attempts_in_its_own_memory_only()
    {
        await using var first = await AddressLimitedNodeAsync(severalNodes: false);
        await using var second = await AddressLimitedNodeAsync(severalNodes: false);
        var address = NewAddress();

        for (var attempt = 0; attempt < 12; attempt++)
        {
            (await SignInAsync(attempt % 2 == 0 ? first : second, address)).Status.ShouldBe(HttpStatusCode.OK);
        }

        (await Store(server.Services, TimeProvider.System).GetCountAsync(ClusterRateLimits.Key(ClusterRateLimits.SignIn, Address(address)), Ct)).ShouldBe(0);
    }

    private static ClusterStore Store(IServiceProvider node, TimeProvider clock) =>
        new(node.GetRequiredService<IServiceScopeFactory>(), clock);

    /// <summary>
    /// A new staff account, signed in once (with its temporary password), from <paramref name="device"/> when given; its id
    /// and the session's.
    /// </summary>
    private async Task<(Guid User, Guid Session)> SignedInUserAsync(ApiClient admin, (Guid Id, string Secret)? device = null)
    {
        var roleId = (await admin.GetAsync<List<RoleListItem>>("/api/v1/admin/roles")).Single(r => r.Name == SystemRoles.Billing).Id;
        var facility = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId;
        var name = "it-scale-" + Guid.NewGuid().ToString("N")[..8];
        var created = await admin.PostAsync<TemporaryPasswordResponse>("/api/v1/admin/users", new CreateUserRequest(
            name, name, Email: null, Phone: null, [roleId], facility is { } f ? [f] : [], facility, PractitionerId: null, TemporaryPassword: null));
        using var signedIn = await server.AnonymousFrom(NewAddress()).SendRawAsync(HttpMethod.Post, "/api/v1/auth/login",
            new LoginRequest(name, created.TemporaryPassword, DeviceId: device?.Id, DeviceSecret: device?.Secret));
        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var scope = server.Services.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserSession>()
            .Where(s => s.UserId == created.UserId).Select(s => s.Id).SingleAsync(Ct);
        return (created.UserId, session);
    }

    /// <summary>A newly enrolled workstation: its id and secret.</summary>
    private async Task<(Guid Id, string Secret)> EnrolAsync(ApiClient admin)
    {
        var code = await admin.PostAsync<EnrollmentCodeResponse>("/api/v1/admin/devices/enrollment-codes",
            new CreateEnrollmentCodeRequest(DeviceKind.Workstation, null, "Scale-out test", 30));
        var enrolled = await server.Anonymous(ownAddress: true).PostAsync<EnrollDeviceResponse>("/api/v1/devices/enroll",
            new EnrollDeviceRequest(code.Code, "Scale-out test " + Guid.NewGuid().ToString("N")[..6], "OPD-PC-1", "Windows 11", "1.0.0"));
        return (enrolled.DeviceId, enrolled.DeviceSecret);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(server.Services.GetRequiredService<IConfiguration>().GetConnectionString("Hospital"));
        await connection.OpenAsync(Ct);
        return connection;
    }

    /// <summary>Whether a statement on another connection is waiting for a lock the connection <paramref name="pid"/> holds.</summary>
    private async Task<bool> IsWaitingForAsync(int pid)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @pid = ANY(pg_blocking_pids(pid)))", connection);
        command.Parameters.AddWithValue("pid", pid);
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>
    /// A node with the server's rate limiter and one endpoint limited as sign-in is, sharing the test server's database.
    /// With <paramref name="severalNodes"/> it is one of several (it has a shared folder, <see cref="DataDirectories.IsShared"/>).
    /// </summary>
    private async Task<WebApplication> AddressLimitedNodeAsync(bool severalNodes)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var root = Path.Combine(Path.GetTempPath(), "hc360-tests", "node-" + Guid.NewGuid().ToString("N"));
        builder.Services.AddSingleton(new DataDirectories(new PlatformOptions
        {
            DataDirectory = root,
            Cluster = { SharedDirectory = severalNodes ? Path.Combine(root, "shared") : null },
        }));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(server.Services.GetRequiredService<IClusterStore>());

        // Every node has the hospital's data key (in the shared folder), which the counts' keys are hashed with.
        builder.Services.AddSingleton(server.Services.GetRequiredService<IFieldProtector>());
        builder.Services.AddHospitalRateLimiter();

        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Client-Address"].ToString());
            return next(context);
        });
        app.UseRateLimiter();
        app.MapPost("/sign-in", () => Results.Ok()).RequireAddressRateLimit(ClusterRateLimits.SignIn);
        await app.StartAsync(Ct);
        return app;
    }

    private static async Task<(HttpStatusCode Status, string? RetryAfter)> SignInAsync(WebApplication node, string address)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sign-in") { Content = JsonContent.Create(new { }) };
        request.Headers.Add("X-Test-Client-Address", address);
        using var response = await node.GetTestClient().SendAsync(request, Ct);
        return (response.StatusCode, response.Headers.TryGetValues("Retry-After", out var values) ? values.Single() : null);
    }

    /// <summary>A request from <paramref name="address"/>, on the test server's services (the rate-limit key is keyed with its data key).</summary>
    private HttpContext Address(string address)
    {
        var context = new DefaultHttpContext { RequestServices = server.Services };
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return context;
    }

    private static string NewAddress() => $"198.18.{RandomNumberGenerator.GetInt32(0, 256)}.{RandomNumberGenerator.GetInt32(1, 255)}";

    private static CreateUserRequest NewUser(string userName, Guid roleId) =>
        new(userName, userName, Email: null, Phone: null, [roleId], FacilityIds: [], DefaultFacilityId: null, PractitionerId: null, TemporaryPassword: null);

    private sealed class DeafSignals : IClusterSignals
    {
        public bool IsListening => false;

        public void Publish(string topic, string? argument = null)
        {
        }
    }

    /// <summary>A cache on a node that keeps nothing but the signals it hears for <paramref name="topic"/>.</summary>
    private sealed class SignalsHeard(string topic) : IClusterCache
    {
        private readonly ConcurrentDictionary<string, bool> _heard = new(StringComparer.Ordinal);

        public string Topic => topic;

        public bool Contains(string argument) => _heard.ContainsKey(argument);

        public void Expire(string? argument) => _heard.TryAdd(argument ?? string.Empty, true);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
