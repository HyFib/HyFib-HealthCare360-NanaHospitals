using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;
using HyFib.HealthCare360.Modules.VirtualCare.Hubs;
using HyFib.HealthCare360.Platform.Realtime;
using HyFib.HealthCare360.Server;
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

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS SCAL-02: with <c>ConnectionStrings:Redis</c>, the server's SignalR set-up (<see cref="ServerComposition"/>) shares
/// real-time groups through Redis, so an event raised on one node reaches a client connected to another. Redis being away
/// does not stop a node: it starts, reports the backplane Degraded on <c>/health/ready</c>, and an event it cannot send
/// is logged without failing the change that raised it (REL-03), telehealth pushes included; a node first used while
/// Redis was down shares everything with the others once it is back. The two-node tests need a Redis server: set
/// <see cref="RedisVariable"/> for one, and have <c>redis-server</c> on the PATH for the one that starts its own late (CI
/// has neither, and skips them).
/// </summary>
public sealed class RedisBackplaneTests
{
    /// <summary>A Redis server the tests may use, such as <c>localhost:6379</c>.</summary>
    public const string RedisVariable = "HC360_TEST_REDIS";

    private const string HubPath = "/hubs/it-backplane";

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

    /// <summary>A node with the server's real-time set-up and a hub to join a group on, on the Redis server given.</summary>
    private static async Task<WebApplication> NodeAsync(string? redis)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = redis });
        ServerComposition.AddRealtime(builder.Services, builder.Configuration);

        var app = builder.Build();

        // A poll that waits for nothing ends in 2 seconds, so a message that never comes fails a test in seconds.
        app.MapHub<GroupHub>(HubPath, hub => hub.LongPolling.PollTimeout = TimeSpan.FromSeconds(2));
        await app.StartAsync(Ct);
        return app;
    }

    private static bool IsEvent(JsonElement message) =>
        message.TryGetProperty("type", out var type) && type.GetInt32() == 1 && message.GetProperty("target").GetString() == "event";

    /// <summary>A client of <paramref name="node"/> in a group of its own, as soon as the node can connect one.</summary>
    private static async Task<LongPollingHub> ConnectOnceRedisIsBackAsync(WebApplication node)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            LongPollingHub? client = null;
            try
            {
                client = await LongPollingHub.ConnectAsync(new ApiClient(node.GetTestClient()), HubPath);
                if (await client.InvokeAsync(nameof(GroupHub.Join), "ward:" + Guid.NewGuid().ToString("N")) is null)
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
}
