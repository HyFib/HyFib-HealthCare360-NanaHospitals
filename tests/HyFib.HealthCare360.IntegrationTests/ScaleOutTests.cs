using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
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
    public async Task A_count_of_failures_lasts_until_a_quiet_spell_whichever_node_counts()
    {
        await using var other = await SecondNode.StartAsync(server, Ct);
        var clock = new ManualClock(WholeSecond());
        var (a, b) = (Store(server.Services, clock), Store(other.Services, clock));
        var key = "it:quiet:" + Guid.NewGuid().ToString("N");
        var quiet = TimeSpan.FromMinutes(15);

        (await a.IncrementUntilQuietAsync(key, quiet, Ct)).Count.ShouldBe(1);
        clock.Now += TimeSpan.FromMinutes(10);
        var second = await b.IncrementUntilQuietAsync(key, quiet, Ct);

        // Each count moves the end on: twenty minutes after the first, ten after the last, it is still counted.
        (second.Count, second.WindowEndsAt).ShouldBe((2, clock.Now + quiet));
        clock.Now += TimeSpan.FromMinutes(10);
        (await a.GetCountAsync(key, Ct)).ShouldBe(2);

        // A quiet quarter of an hour, and the next counts from nothing.
        clock.Now += TimeSpan.FromMinutes(5);
        (await b.GetCountAsync(key, Ct)).ShouldBe(0);
        (await a.IncrementUntilQuietAsync(key, quiet, Ct)).Count.ShouldBe(1);
    }

    /// <summary>
    /// A single Server Node keeps the same entries in its memory (<see cref="MemoryClusterStore"/>): the same calls at the
    /// same moments, the ends of windows and lifetimes included, get the same answers as from the database.
    /// </summary>
    [Fact]
    public async Task A_single_servers_memory_answers_as_the_database_does()
    {
        var start = WholeSecond();
        var databaseClock = new ManualClock(start);
        var memoryClock = new ManualClock(start);

        var database = await ContractScriptAsync(Store(server.Services, databaseClock), databaseClock);
        var memory = await ContractScriptAsync(new MemoryClusterStore(new MemoryCache(new MemoryCacheOptions()), memoryClock), memoryClock);

        memory.ShouldBe(database);
        database.ShouldContain("05:00: TryAdd token True", "an entry that ended is taken over at the moment it ends");
    }

    [Fact]
    public async Task A_proof_of_work_is_spent_once_and_failures_are_counted_once_for_every_node()
    {
        var shared = NewRoot();
        var keyRing = new EphemeralDataProtectionProvider();
        var (firstAudit, secondAudit) = (new RecordingAudit(), new RecordingAudit());
        await using var first = await ProofOfWorkNodeAsync(shared, keyRing, firstAudit);
        await using var second = await ProofOfWorkNodeAsync(shared, keyRing, secondAudit);
        var phone = NewMobile();

        // A challenge from one node, solved and sent to the other, cannot be sent to the first again.
        var challenge = await ChallengeAsync(first, NewAddress(), phone);
        var proof = ProofOfWork.Solve(challenge.Challenge!, challenge.Bits);
        (await SendCodeAsync(second, NewAddress(), phone, proof)).ShouldBe(HttpStatusCode.OK);
        (await SendCodeAsync(first, NewAddress(), phone, proof)).ShouldBe(HttpStatusCode.Forbidden);

        // Bad proofs for another number, from one address, two to one node and one to the other: three failures, which raise
        // the work both nodes ask for that number and that address. The refusal is audited once, by the node that saw it first.
        var script = NewAddress();
        var target = NewMobile();
        var before = (await ChallengeAsync(second, NewAddress(), target)).Bits;
        foreach (var node in new[] { first, second, first })
        {
            (await SendCodeAsync(node, script, target, "junk")).ShouldBe(HttpStatusCode.Forbidden);
        }

        (await ChallengeAsync(second, NewAddress(), target)).Bits.ShouldBe(before + AutomationSuspicion.BitsPerStep);
        (await ChallengeAsync(first, script, null)).Bits.ShouldBe(before + AutomationSuspicion.BitsPerStep);
        (firstAudit.Malformed(), secondAudit.Malformed()).ShouldBe((1, 0));

        // The store holds them under keyed hashes only: neither the address nor the number.
        (await EntriesMentioningAsync(script)).ShouldBe(0);
        (await EntriesMentioningAsync(target[^10..])).ShouldBe(0);
        var suspicion = first.Services.GetRequiredService<AutomationSuspicion>();
        (await Store(server.Services, TimeProvider.System).GetCountAsync("pow:fail:ip:" + suspicion.AddressHash(script), Ct)).ShouldBe(3);
    }

    [Fact]
    public async Task A_single_server_keeps_its_proof_of_work_in_its_own_memory()
    {
        await using var node = await ProofOfWorkNodeAsync(shared: null, new EphemeralDataProtectionProvider(), new RecordingAudit());
        var script = NewAddress();
        var before = (await ChallengeAsync(node, script, null)).Bits;

        for (var i = 0; i < AutomationSuspicion.FailuresPerStep; i++)
        {
            (await SendCodeAsync(node, script, NewMobile(), "junk")).ShouldBe(HttpStatusCode.Forbidden);
        }

        (await ChallengeAsync(node, script, null)).Bits.ShouldBe(before + AutomationSuspicion.BitsPerStep);
        var suspicion = node.Services.GetRequiredService<AutomationSuspicion>();
        (await Store(server.Services, TimeProvider.System).GetCountAsync("pow:fail:ip:" + suspicion.AddressHash(script), Ct)).ShouldBe(0);
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
        var root = NewRoot();
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

    /// <summary>
    /// Calls every method of <paramref name="store"/> in turn, the clock moving between them, and writes down what it
    /// answered and when (from the start, with the ends of windows), under keys of its own.
    /// </summary>
    private static async Task<List<string>> ContractScriptAsync(IClusterStore store, ManualClock clock)
    {
        var run = "it:contract:" + Guid.NewGuid().ToString("N") + ":";
        var start = clock.Now;
        var said = new List<string>();
        void Say(string call, object answer) => said.Add($"{clock.Now - start:mm\\:ss}: {call} {answer}");
        string Count(ClusterCount count) => $"{count.Count} until {count.WindowEndsAt - start:mm\\:ss}";

        Say("TryAdd token", await store.TryAddAsync(run + "token", TimeSpan.FromMinutes(5), Ct));
        Say("TryAdd token", await store.TryAddAsync(run + "token", TimeSpan.FromMinutes(5), Ct));
        Say("Get token", await store.GetCountAsync(run + "token", Ct));
        Say("Increment window", Count(await store.IncrementAsync(run + "window", TimeSpan.FromMinutes(1), Ct)));
        Say("UntilQuiet quiet", Count(await store.IncrementUntilQuietAsync(run + "quiet", TimeSpan.FromMinutes(1), Ct)));
        clock.Now += TimeSpan.FromSeconds(30);
        Say("Increment window", Count(await store.IncrementAsync(run + "window", TimeSpan.FromMinutes(1), Ct)));
        Say("UntilQuiet quiet", Count(await store.IncrementUntilQuietAsync(run + "quiet", TimeSpan.FromMinutes(1), Ct)));
        clock.Now += TimeSpan.FromSeconds(30);
        Say("Get window", await store.GetCountAsync(run + "window", Ct));
        Say("Get quiet", await store.GetCountAsync(run + "quiet", Ct));
        Say("Increment window", Count(await store.IncrementAsync(run + "window", TimeSpan.FromMinutes(1), Ct)));
        clock.Now += TimeSpan.FromSeconds(60);
        Say("Get quiet", await store.GetCountAsync(run + "quiet", Ct));
        Say("UntilQuiet quiet", Count(await store.IncrementUntilQuietAsync(run + "quiet", TimeSpan.FromMinutes(1), Ct)));
        clock.Now += TimeSpan.FromMinutes(3);
        Say("TryAdd token", await store.TryAddAsync(run + "token", TimeSpan.FromMinutes(5), Ct));
        await store.RemoveAsync(run + "token", Ct);
        Say("Get token", await store.GetCountAsync(run + "token", Ct));
        Say("TryAdd token", await store.TryAddAsync(run + "token", TimeSpan.FromMinutes(5), Ct));
        await store.RemoveAsync(run + "missing", Ct);
        Say("Get missing", await store.GetCountAsync(run + "missing", Ct));
        return said;
    }

    /// <summary>
    /// A node with the server's rate limiter and proof of work, made as the platform makes them, its challenge endpoint, and
    /// an endpoint that asks for a proof for a mobile number as the portal's code request does, on the test server's
    /// database. With <paramref name="shared"/> it is one of the nodes sharing that folder, whose keys folder holds the
    /// anti-automation key; <paramref name="keyRing"/> stands in for their shared Data Protection key ring.
    /// </summary>
    private async Task<WebApplication> ProofOfWorkNodeAsync(string? shared, IDataProtectionProvider keyRing, RecordingAudit audit)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = NewRoot(), Cluster = { SharedDirectory = shared } });
        Directory.CreateDirectory(directories.Keys);
        builder.Services.AddSingleton(directories);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton(keyRing);
        builder.Services.AddSingleton(server.Services.GetRequiredService<IClusterStore>());
        builder.Services.AddScoped<ISettingsService, DefaultSettings>();
        builder.Services.AddSingleton<IAuditLogger>(audit);
        builder.Services.AddHospitalRateLimiter();
        builder.Services.AddAntiAutomation(directories);

        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Client-Address"].ToString());
            return next(context);
        });
        app.UseRateLimiter();
        AntiAutomationEndpoints.Map(app);
        app.MapPost("/code", (OtpRequest request) => Results.Ok())
            .RequireProofOfWork(AntiAutomationActions.Otp, context => context.Arguments.OfType<OtpRequest>().FirstOrDefault()?.Phone);
        await app.StartAsync(Ct);
        return app;
    }

    private static async Task<AntiAutomationChallenge> ChallengeAsync(WebApplication node, string address, string? phone)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/public/challenge")
        {
            Content = JsonContent.Create(new AntiAutomationChallengeRequest(AntiAutomationActions.Otp, phone)),
        };
        request.Headers.Add("X-Test-Client-Address", address);
        using var response = await node.GetTestClient().SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AntiAutomationChallenge>(Ct))!;
    }

    private static async Task<HttpStatusCode> SendCodeAsync(WebApplication node, string address, string phone, string proof)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/code") { Content = JsonContent.Create(new OtpRequest(phone)) };
        request.Headers.Add("X-Test-Client-Address", address);
        request.Headers.Add(AntiAutomation.ProofHeader, proof);
        using var response = await node.GetTestClient().SendAsync(request, Ct);
        return response.StatusCode;
    }

    /// <summary>The cluster store's entries whose key holds <paramref name="text"/>.</summary>
    private async Task<int> EntriesMentioningAsync(string text)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ClusterEntry>().CountAsync(e => e.Key.Contains(text), Ct);
    }

    /// <summary>Now, to the whole second: the database keeps microseconds, so moments a whole number of seconds apart compare exactly.</summary>
    private static DateTimeOffset WholeSecond() =>
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "hc360-tests", "node-" + Guid.NewGuid().ToString("N"));

    private static string NewMobile() => "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + RandomNumberGenerator.GetInt32(10))));

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

    /// <summary>Every setting at its default: the proof of work on, at its default work.</summary>
    private sealed class DefaultSettings : ISettingsService
    {
        public Task<T?> GetAsync<T>(string key, Guid? facilityId = null, CancellationToken cancellationToken = default) => Task.FromResult(default(T));

        public Task SetAsync<T>(string key, T value, Guid? facilityId = null, bool secret = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetAsync<T>(string key, T value, Guid? facilityId, bool secret, uint? expectedVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(string key, Guid? facilityId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>A node's audit trail: the entries it wrote.</summary>
    private sealed class RecordingAudit : IAuditLogger
    {
        private readonly ConcurrentQueue<AuditEntry> _written = new();

        /// <summary>The refusals of a malformed proof audited.</summary>
        public int Malformed() => _written.Count(e => e.Action == "security.anti_automation_rejected" && e.Summary!.Contains("(malformed)"));

        public void Add(AuditEntry entry) => _written.Enqueue(entry);

        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            _written.Enqueue(entry);
            return Task.CompletedTask;
        }

        public Task PatientRecordAccessedAsync(Guid patientId, string what, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
