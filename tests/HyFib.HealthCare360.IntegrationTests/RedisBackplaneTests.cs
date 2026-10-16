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
/// is logged without failing the change that raised it (REL-03). The two-node test needs a Redis server: set
/// <see cref="RedisVariable"/> (CI does not run one, and skips it).
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

        var received = await client.ReceiveAsync(m => m.TryGetProperty("type", out var type) && type.GetInt32() == 1
                                                      && m.GetProperty("target").GetString() == "event");
        received.GetProperty("arguments")[0].GetString().ShouldBe("bed 4 cleaned");
    }

    [Fact]
    public async Task A_node_whose_Redis_is_away_starts_reports_it_degraded_and_keeps_its_changes()
    {
        // Nothing listens on port 1.
        await using var node = await NodeAsync("127.0.0.1:1,connectTimeout=500,asyncTimeout=1000");

        var report = await node.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(Ct);
        report.Entries["redis-backplane"].Status.ShouldBe(HealthStatus.Degraded);

        // The platform's publisher, as every module raises events: it logs what it cannot send and returns.
        var publisher = new SignalRRealtimePublisher(
            node.Services.GetRequiredService<IHubContext<EventsHub>>(), TimeProvider.System, NullLogger<SignalRRealtimePublisher>.Instance);
        await Should.NotThrowAsync(() => publisher.PublishAsync(Topics.Facility(Guid.NewGuid()), "bed.changed", cancellationToken: Ct));
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
        app.MapHub<GroupHub>(HubPath);
        await app.StartAsync(Ct);
        return app;
    }

    /// <summary>Joins a group on request, as the platform's hubs join a user to the topics they may see.</summary>
    public sealed class GroupHub : Hub
    {
        public Task Join(string group) => Groups.AddToGroupAsync(Context.ConnectionId, group);
    }
}
