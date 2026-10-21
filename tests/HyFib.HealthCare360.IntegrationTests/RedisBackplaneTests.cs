using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;
using HyFib.HealthCare360.Modules.VirtualCare.Hubs;
using HyFib.HealthCare360.Platform.Realtime;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS SCAL-02: with <c>ConnectionStrings:Redis</c>, the server's SignalR set-up (<see cref="ServerComposition"/>) shares
/// real-time groups through Redis, so an event raised on one node reaches a client connected to another. Redis being away
/// does not stop a node: it starts, reports the backplane Degraded on <c>/health/ready</c>, and an event it cannot send
/// is logged without failing the change that raised it (REL-03), telehealth pushes included; a node first used while
/// Redis was down shares everything with the others once it is back, and screens that stayed connected through an
/// outage are told to refetch once it is back, and follow what they subscribed to meanwhile once they subscribe again. The
/// two-node tests need a Redis server: set <see cref="RedisVariable"/> for one, and have <c>redis-server</c> on the PATH
/// for those that start their own (CI has neither, and skips them).
/// </summary>
public sealed class RedisBackplaneTests
{
    /// <summary>A Redis server the tests may use, such as <c>localhost:6379</c>.</summary>
    public const string RedisVariable = "HC360_TEST_REDIS";

    private const string HubPath = "/hubs/it-backplane";

    private const string OtherHubPath = "/hubs/it-backplane-other";

    /// <summary>The platform's own events hub, which screens subscribe to topics on.</summary>
    private const string EventsPath = "/hubs/it-events";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_event_raised_on_one_node_reaches_a_client_connected_to_another()
    {
        var redis = Environment.GetEnvironmentVariable(RedisVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(redis), $"Set {RedisVariable} to a Redis server (host:port) to run this.");
        await using var first = await NodeAsync(redis);
        await using var second = await NodeAsync(redis);
        var group = "ward:" + Guid.NewGuid().ToString("N");
        await using var client = await LongPollingHub.ConnectAsync(new ApiClient(second.GetTestClient()), HubPath);
        (await client.InvokeAsync(nameof(GroupHub.Join), group)).ShouldBeNull();

        // Raised on the first node, which has no connection in the group.
        await first.Services.GetRequiredService<IHubContext<GroupHub>>().Clients.Group(group).SendAsync("event", "bed 4 cleaned", Ct);

        var received = await client.ReceiveAsync(IsEvent);
        received.GetProperty("arguments")[0].GetString().ShouldBe("bed 4 cleaned");
    }

    /// <summary>
    /// A node first used while Redis is down (its screens reconnecting after a power cut, an event raised) shares everything
    /// with the other nodes once Redis is back: a message another node sends to every client reaches its clients, and
    /// another node can put one of its clients in a group and send to it, as telehealth's Admit does for a patient waiting
    /// on another node. The backplane subscribes to what the nodes share once, when it is given its connection to Redis;
    /// given one while Redis was away, it never did, and the group change waited 30 seconds for an acknowledgement.
    /// </summary>
    [Fact]
    public async Task A_node_first_used_while_Redis_is_down_shares_everything_once_Redis_is_back()
    {
        var redisServer = OnPath(OperatingSystem.IsWindows() ? "redis-server.exe" : "redis-server");
        Assert.SkipWhen(redisServer is null, "Needs redis-server on the PATH, to start a Redis server of its own after the nodes.");
        var port = FreePort();
        await using var first = await NodeAsync($"127.0.0.1:{port}");
        await using var second = await NodeAsync($"127.0.0.1:{port}");
        foreach (var node in new[] { first, second })
        {
            await TryAsync(async () =>
            {
                await using var screen = await LongPollingHub.ConnectAsync(new ApiClient(node.GetTestClient()), HubPath);
                await screen.InvokeAsync(nameof(GroupHub.Join), "ward");
            });
            await TryAsync(() => node.Services.GetRequiredService<IHubContext<GroupHub>>().Clients.All.SendAsync("event", "while Redis is down", Ct));
        }

        using var redis = StartRedis(redisServer!, port);
        try
        {
            await using var client = await ConnectOnceRedisIsBackAsync(second);
            var hub = first.Services.GetRequiredService<IHubContext<GroupHub>>();

            await hub.Clients.All.SendAsync("event", "to every client", Ct);
            (await client.ReceiveAsync(IsEvent)).GetProperty("arguments")[0].GetString().ShouldBe("to every client");

            var room = "room:" + Guid.NewGuid().ToString("N");
            await Should.NotThrowAsync(
                () => hub.Groups.AddToGroupAsync(client.ConnectionId, room, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct),
                "the node of the client should acknowledge the group change at once");
            await hub.Clients.Group(room).SendAsync("event", "admitted", Ct);
            (await client.ReceiveAsync(IsEvent)).GetProperty("arguments")[0].GetString().ShouldBe("admitted");
        }
        finally
        {
            redis.Kill(entireProcessTree: true);
            await redis.WaitForExitAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A screen connected before Redis went away stays connected throughout: its connection is to its node, not to Redis.
    /// Every event raised meanwhile was lost, and it neither reconnects nor refetches by itself, so once Redis is back its
    /// node tells it to refetch (<c>resync</c>), on every hub, without it reconnecting.
    /// </summary>
    [Fact]
    public async Task Screens_connected_through_a_Redis_outage_are_told_to_refetch_once_it_is_back()
    {
        var redisServer = OnPath(OperatingSystem.IsWindows() ? "redis-server.exe" : "redis-server");
        Assert.SkipWhen(redisServer is null, "Needs redis-server on the PATH, to stop and start a Redis server of its own.");
        var port = FreePort();
        var redis = StartRedis(redisServer!, port);
        try
        {
            await using var node = await NodeAsync($"127.0.0.1:{port}");
            await using var screen = await ConnectOnceRedisIsBackAsync(node);
            await using var call = await ConnectOnceRedisIsBackAsync(node, OtherHubPath);
            await PassOverStartAsync(screen, call);

            // Redis goes away; an event raised meanwhile is lost.
            redis.Kill(entireProcessTree: true);
            await redis.WaitForExitAsync(CancellationToken.None);
            redis.Dispose();
            await TryAsync(() => node.Services.GetRequiredService<IHubContext<GroupHub>>().Clients.All.SendAsync("event", "while Redis is away", Ct));
            redis = StartRedis(redisServer!, port);

            (await ReceiveWithinAsync(screen, IsResync, TimeSpan.FromSeconds(60)))
                .ShouldBeTrue("a screen that stayed connected should be told to refetch once Redis is back");
            (await ReceiveWithinAsync(call, IsResync, TimeSpan.FromSeconds(10)))
                .ShouldBeTrue("so should one connected to another hub");
            (await screen.InvokeAsync(nameof(GroupHub.Join), "ward")).ShouldBeNull("the screen kept its connection");
        }
        finally
        {
            redis.Kill(entireProcessTree: true);
            await redis.WaitForExitAsync(CancellationToken.None);
            redis.Dispose();
        }
    }

    /// <summary>
    /// The events a node could not send while its own connection to Redis was away were lost to the screens on every node,
    /// and those nodes may have kept theirs, or got it back first: once it is back, it tells the other nodes to tell their
    /// screens to refetch.
    /// </summary>
    [Fact]
    public async Task Screens_on_every_node_are_told_to_refetch_when_one_nodes_Redis_connection_comes_back()
    {
        var redisServer = OnPath(OperatingSystem.IsWindows() ? "redis-server.exe" : "redis-server");
        Assert.SkipWhen(redisServer is null, "Needs redis-server on the PATH, to cut one node's connection to a Redis server of its own.");
        var port = FreePort();
        using var redis = StartRedis(redisServer!, port);
        try
        {
            await using var first = await NodeAsync($"127.0.0.1:{port},name=it-first-node");
            await using var second = await NodeAsync($"127.0.0.1:{port}");
            await using var screen = await ConnectOnceRedisIsBackAsync(second);

            // The first node raises an event, so it is connected to Redis; then Redis drops its connections, and only its.
            await SendOnceConnectedAsync(first, "from the first node");
            (await screen.ReceiveAsync(IsEvent)).GetProperty("arguments")[0].GetString().ShouldBe("from the first node");
            await PassOverStartAsync(screen);
            await using (var admin = await ConnectionMultiplexer.ConnectAsync($"127.0.0.1:{port},allowAdmin=true"))
            {
                var server = admin.GetServer(admin.GetEndPoints()[0]);
                var cut = (await server.ClientListAsync()).Where(c => c.Name == "it-first-node").ToList();
                cut.ShouldNotBeEmpty();
                foreach (var client in cut)
                {
                    await server.ClientKillAsync(client.Id);
                }
            }

            (await ReceiveWithinAsync(screen, IsResync, TimeSpan.FromSeconds(60)))
                .ShouldBeTrue("a screen on another node should be told to refetch once the first node's connection is back");
        }
        finally
        {
            redis.Kill(entireProcessTree: true);
            await redis.WaitForExitAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A screen that opens a ward, a queue or a chart while Redis is away subscribes to its topic, and the subscription fails
    /// (the apps take no notice). SignalR had counted the connection in the topic's group all the same, so subscribing again
    /// once Redis was back did nothing: the screen refetched once when told to, then got none of the topic's events for as
    /// long as it stayed connected, though it looked live. A subscription that fails is now undone, so subscribing again,
    /// as the apps do when told to refetch, follows the topic.
    /// </summary>
    [Fact]
    public async Task A_topic_subscribed_to_while_Redis_is_away_is_followed_once_the_screen_subscribes_again()
    {
        var redisServer = OnPath(OperatingSystem.IsWindows() ? "redis-server.exe" : "redis-server");
        Assert.SkipWhen(redisServer is null, "Needs redis-server on the PATH, to stop and start a Redis server of its own.");
        var port = FreePort();
        var redis = StartRedis(redisServer!, port);
        try
        {
            await using var first = await NodeAsync($"127.0.0.1:{port}");
            await using var second = await NodeAsync($"127.0.0.1:{port}");
            await using var screen = await ConnectOnceRedisIsBackAsync(second, EventsPath);
            await PassOverStartAsync(screen);

            // Redis goes away, and the screen opens a ward meanwhile: subscribing to it fails.
            redis.Kill(entireProcessTree: true);
            await redis.WaitForExitAsync(CancellationToken.None);
            redis.Dispose();
            var ward = await SubscribeWhileRedisIsAwayAsync(screen);
            redis = StartRedis(redisServer!, port);
            (await ReceiveWithinAsync(screen, IsResync, TimeSpan.FromSeconds(60)))
                .ShouldBeTrue("the screen should be told to refetch once Redis is back");

            // Told to, the screen subscribes again, as the apps do; then the ward changes on the other node.
            (await screen.InvokeAsync(nameof(EventsHub.Subscribe), ward)).ShouldBeNull();
            var hub = first.Services.GetRequiredService<IHubContext<EventsHub>>();
            await OnceConnectedAsync(() => hub.Clients.Group(ward).SendAsync(
                "event", new RealtimeEnvelope(ward, "bed.changed", null, DateTimeOffset.UtcNow), Ct));

            (await ReceiveWithinAsync(screen, message => IsEvent(message) && message.GetProperty("arguments")[0].GetProperty("topic").GetString() == ward, TimeSpan.FromSeconds(10)))
                .ShouldBeTrue("the ward the screen opened while Redis was away should send it its events once it subscribed again");
        }
        finally
        {
            redis.Kill(entireProcessTree: true);
            await redis.WaitForExitAsync(CancellationToken.None);
            redis.Dispose();
        }
    }

    [Fact]
    public async Task A_node_whose_Redis_is_away_starts_reports_it_degraded_and_keeps_its_changes()
    {
        // Nothing listens on port 1. The client's own timeouts are left as the server has them (5 s for a command).
        await using var node = await NodeAsync("127.0.0.1:1,connectTimeout=500");

        var report = await node.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(Ct);
        report.Entries["redis-backplane"].Status.ShouldBe(HealthStatus.Degraded);

        // What the check says, in the log of every probe, is what happens: every event goes through Redis, to the clients of
        // the node that raised it too, so none gets any, and a screen that connects meanwhile is refused.
        report.Entries["redis-backplane"].Description.ShouldBe(
            "The Redis backplane cannot be reached: until it is back, no screen gets real-time updates, whichever server it is " +
            "connected to, and screens that connect meanwhile are refused and try again. Requests are answered as usual.");
        var refused = false;
        try
        {
            await using var screen = await LongPollingHub.ConnectAsync(new ApiClient(node.GetTestClient()), HubPath);
            await screen.InvokeAsync(nameof(GroupHub.Join), "ward");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            refused = true;
        }

        refused.ShouldBeTrue("a screen that connects while Redis is away is refused");

        // The platform's publisher, as every module raises events: it logs what it cannot send and returns.
        var publisher = new SignalRRealtimePublisher(
            node.Services.GetRequiredService<IHubContext<EventsHub>>(), TimeProvider.System, NullLogger<SignalRRealtimePublisher>.Instance);
        // At once: a publish that waited for Redis would hold the request that raised the event for the client's timeout.
        var publishing = Stopwatch.StartNew();
        await Should.NotThrowAsync(() => publisher.PublishAsync(Topics.Facility(Guid.NewGuid()), "bed.changed", cancellationToken: Ct));
        publishing.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "while Redis is away a publish should fail at once, not wait for it");

        // A telehealth chat message is saved before it is pushed to the call: the request that saved it succeeds, so nobody
        // sends it again and puts it in the consultation record twice.
        var telehealth = new TelehealthBroadcaster(
            node.Services.GetRequiredService<IHubContext<TelehealthHub>>(), publisher, NullLogger<TelehealthBroadcaster>.Instance);
        var message = new TelehealthMessageDto(Guid.NewGuid(), TelehealthRole.Clinician, "Dr. Rao", "Can you hear me?", DateTimeOffset.UtcNow);
        await Should.NotThrowAsync(() => telehealth.ChatAsync(Guid.NewGuid(), message, Ct));
    }

    /// <summary>
    /// A node with the server's real-time set-up and hubs to join a group on, on the Redis server given: two of the tests'
    /// own, and the platform's events hub, for a member of staff who may follow every ward.
    /// </summary>
    private static async Task<WebApplication> NodeAsync(string? redis)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = redis });
        ServerComposition.AddRealtime(builder.Services, builder.Configuration);
        builder.Services.AddAuthentication(StaffSignIn.Name).AddScheme<AuthenticationSchemeOptions, StaffSignIn>(StaffSignIn.Name, null);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(Substitute.For<IPermissionService>());
        builder.Services.AddRealtimeTopic(new RealtimeTopicPolicy("ward", RealtimeTopicScope.Record, [], (_, _, _, _) => Task.FromResult(true)));
        builder.Services.AddScoped<RealtimeTopicAuthorizer>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // A poll that waits for nothing ends in 2 seconds, so a message that never comes fails a test in seconds.
        app.MapHub<GroupHub>(HubPath, hub => hub.LongPolling.PollTimeout = TimeSpan.FromSeconds(2));
        app.MapHub<OtherHub>(OtherHubPath, hub => hub.LongPolling.PollTimeout = TimeSpan.FromSeconds(2));
        app.MapHub<EventsHub>(EventsPath, hub => hub.LongPolling.PollTimeout = TimeSpan.FromSeconds(2));
        await app.StartAsync(Ct);
        return app;
    }

    private static bool IsEvent(JsonElement message) =>
        message.TryGetProperty("type", out var type) && type.GetInt32() == 1 && message.GetProperty("target").GetString() == "event";

    private static bool IsResync(JsonElement message) =>
        message.TryGetProperty("type", out var type) && type.GetInt32() == 1 && message.GetProperty("target").GetString() == "resync";

    /// <summary>
    /// Takes in any <c>resync</c> a node's start sent: a backplane connection the node had to ask for again, as Redis or its
    /// connection was not ready yet, counts as an outage (what it was asked for meanwhile was lost), and is not what a test
    /// that cuts the connection afterwards looks for.
    /// </summary>
    private static Task PassOverStartAsync(params LongPollingHub[] clients) =>
        Task.WhenAll(clients.Select(client => ReceiveWithinAsync(client, IsResync, TimeSpan.FromSeconds(3))));

    /// <summary>Raises an event on <paramref name="node"/> as soon as its connection to Redis is ready.</summary>
    private static Task SendOnceConnectedAsync(WebApplication node, string text) =>
        OnceConnectedAsync(() => node.Services.GetRequiredService<IHubContext<GroupHub>>().Clients.All.SendAsync("event", text, Ct));

    /// <summary>Sends as soon as the node that sends is connected to Redis.</summary>
    private static async Task OnceConnectedAsync(Func<Task> send)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                await send();
                return;
            }
            catch (RedisConnectionException) when (DateTime.UtcNow < until)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
            }
        }
    }

    /// <summary>
    /// A ward that <paramref name="screen"/> subscribes to and could not, as Redis is away: until the node notices that it
    /// is, a subscription may still be made, so each try is to a ward of its own.
    /// </summary>
    private static async Task<string> SubscribeWhileRedisIsAwayAsync(LongPollingHub screen)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var ward = Topics.Ward(Guid.NewGuid());
            if (await screen.InvokeAsync(nameof(EventsHub.Subscribe), ward) is not null)
            {
                return ward;
            }

            DateTime.UtcNow.ShouldBeLessThan(until, "subscribing should fail while Redis is away");
            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
        }
    }

    /// <summary>Whether <paramref name="client"/> receives a message <paramref name="wanted"/> accepts within <paramref name="time"/>.</summary>
    private static async Task<bool> ReceiveWithinAsync(LongPollingHub client, Func<JsonElement, bool> wanted, TimeSpan time)
    {
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until)
        {
            try
            {
                await client.ReceiveAsync(wanted);
                return true;
            }
            catch (InvalidOperationException)
            {
                // Nothing yet: poll again.
            }
        }

        return false;
    }

    /// <summary>A client of <paramref name="node"/> in a group of its own, as soon as the node can connect one.</summary>
    private static async Task<LongPollingHub> ConnectOnceRedisIsBackAsync(WebApplication node, string path = HubPath)
    {
        // The tests' hubs join a group by Join, the platform's by Subscribe.
        var join = path == EventsPath ? nameof(EventsHub.Subscribe) : nameof(GroupHub.Join);
        var until = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            LongPollingHub? client = null;
            try
            {
                client = await LongPollingHub.ConnectAsync(new ApiClient(node.GetTestClient()), path);
                if (await client.InvokeAsync(join, Topics.Ward(Guid.NewGuid())) is null)
                {
                    return client;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && DateTime.UtcNow < until)
            {
                // Redis is not back yet, or the node has not reconnected to it: the connection was refused.
            }

            if (client is not null)
            {
                await TryAsync(async () => await client.DisposeAsync());
            }

            DateTime.UtcNow.ShouldBeLessThan(until, "the node should take real-time connections again once Redis is back");
            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct);
        }
    }

    private static Process StartRedis(string redisServer, int port)
    {
        var redis = Process.Start(new ProcessStartInfo(redisServer)
        {
            ArgumentList = { "--port", port.ToString(CultureInfo.InvariantCulture), "--bind", "127.0.0.1", "--save", "", "--appendonly", "no" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }).ShouldNotBeNull();
        redis.OutputDataReceived += (_, _) => { };
        redis.ErrorDataReceived += (_, _) => { };
        redis.BeginOutputReadLine();
        redis.BeginErrorReadLine();
        return redis;
    }

    private static string? OnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, program))
            .FirstOrDefault(File.Exists);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Something that fails while Redis is down: what matters is that the node was used.</summary>
    private static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Expected while Redis is away.
        }
    }

    /// <summary>Joins a group on request, as the platform's hubs join a user to the topics they may see.</summary>
    public sealed class GroupHub : Hub
    {
        public Task Join(string group) => Groups.AddToGroupAsync(Context.ConnectionId, group);
    }

    /// <summary>A second hub, as telehealth's is beside the platform's.</summary>
    public sealed class OtherHub : Hub
    {
        public Task Join(string group) => Groups.AddToGroupAsync(Context.ConnectionId, group);
    }

    /// <summary>Every request is a member of staff signed in to a facility, as the events hub needs.</summary>
    private sealed class StaffSignIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "it-staff";

        private static readonly ClaimsPrincipal Staff = new(new ClaimsIdentity(
            [
                new Claim(HcClaims.UserId, Guid.NewGuid().ToString()),
                new Claim(HcClaims.UserType, HcClaims.StaffType),
                new Claim(HcClaims.Facility, Guid.NewGuid().ToString()),
            ],
            Name));

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(Staff, Name)));
    }
}
