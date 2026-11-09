using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS SEC-06 with several servers (SCAL-01): the request limits other than sign-in and one-time codes are counted on the
/// Redis server the nodes already share (<see cref="RedisRateLimitStore"/>), so a client's budget is the hospital's,
/// whichever node the reverse proxy sends it to. While Redis cannot be reached, each node counts on its own: the limits
/// still hold, per node, and System health says so. Each node here is the server's own rate limiter and Redis set-up
/// (<see cref="RateLimitPolicies.AddHospitalRateLimiter"/>, <see cref="ServerComposition.AddRealtime"/>) on a test server.
/// The tests start a Redis server of their own (<c>redis-server</c> on the PATH), or use <see cref="RedisBackplaneTests.RedisVariable"/>.
/// </summary>
public sealed class SharedRateLimitTests
{
    private const int PublicLimit = 5;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_servers_share_one_budget()
    {
        await using var redis = await RedisAsync();
        await using var first = await NodeAsync(redis.Address);
        await using var second = await NodeAsync(redis.Address);
        var client = "203.0.113." + Random.Shared.Next(1, 250).ToString(CultureInfo.InvariantCulture);

        // Three through one node and two through the other use up the budget of five, wherever the next one goes.
        var answers = new List<HttpStatusCode>();
        foreach (var node in new[] { first, first, first, second, second, second, first })
        {
            answers.Add(await GetAsync(node, client));
        }

        answers.ShouldBe([.. Enumerable.Repeat(HttpStatusCode.OK, PublicLimit), HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests]);

        // Another client has a budget of its own.
        (await GetAsync(second, "198.51.100.9")).ShouldBe(HttpStatusCode.OK);
        foreach (var node in new[] { first, second })
        {
            node.Services.GetRequiredService<SharedRateLimitState>().CountingAloneSince.ShouldBeNull();
        }
    }

    /// <summary>Requests at the same moment on both servers are each counted once: the budget is never overrun.</summary>
    [Fact]
    public async Task Requests_at_the_same_moment_on_both_servers_are_each_counted_once()
    {
        await using var redis = await RedisAsync();
        await using var first = await NodeAsync(redis.Address);
        await using var second = await NodeAsync(redis.Address);

        var answers = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => GetAsync(i % 2 == 0 ? first : second, "192.0.2.77")));

        answers.Count(a => a == HttpStatusCode.OK).ShouldBe(PublicLimit);
        answers.Count(a => a == HttpStatusCode.TooManyRequests).ShouldBe(40 - PublicLimit);
    }

    /// <summary>
    /// Redis cannot be reached: each server counts on its own, as a single Server Node does. A client gets its budget on
    /// each, never more (fail-safe, not fail-open), and System health on each says the limits are counted per server.
    /// </summary>
    [Fact]
    public async Task Without_Redis_each_server_counts_on_its_own_and_says_so()
    {
        var nowhere = $"127.0.0.1:{FreePort()},connectTimeout=200";
        await using var first = await NodeAsync(nowhere);
        await using var second = await NodeAsync(nowhere);

        foreach (var node in new[] { first, second })
        {
            var answers = new List<HttpStatusCode>();
            for (var i = 0; i <= PublicLimit; i++)
            {
                answers.Add(await GetAsync(node, "203.0.113.200"));
            }

            answers.ShouldBe([.. Enumerable.Repeat(HttpStatusCode.OK, PublicLimit), HttpStatusCode.TooManyRequests], "each server still holds the limit");
            node.Services.GetRequiredService<SharedRateLimitState>().CountingAloneSince.ShouldNotBeNull();
            await using var scope = node.Services.CreateAsyncScope();
            var warnings = new List<HealthWarning>();
            foreach (var source in scope.ServiceProvider.GetServices<IHealthWarningSource>())
            {
                warnings.AddRange(await source.WarningsAsync(Ct));
            }

            warnings.ShouldContain(w => w.Code == "ratelimit.per_server" && w.Severity == "warning");
        }
    }

    /// <summary>The token bucket counts in Redis as ASP.NET Core's does: a full bucket, then one more token each period.</summary>
    [Fact]
    public async Task The_token_bucket_refills_by_the_period_on_Redis_clock()
    {
        await using var redis = await RedisAsync();
        await using var store = Store(redis.Address);
        var key = "rl:test:" + Guid.NewGuid().ToString("N");
        var bucket = new SharedLimit(SharedLimitKind.TokenBucket, 3, 1, TimeSpan.FromMilliseconds(400));

        for (var i = 0; i < 3; i++)
        {
            (await store.TryTakeAsync(key, bucket, Ct)).Allowed.ShouldBeTrue();
        }

        var empty = await store.TryTakeAsync(key, bucket, Ct);
        empty.Allowed.ShouldBeFalse();
        empty.RetryAfter.ShouldBeInRange(TimeSpan.Zero, TimeSpan.FromMilliseconds(400));

        await Task.Delay(empty.RetryAfter + TimeSpan.FromMilliseconds(50), Ct);
        (await store.TryTakeAsync(key, bucket, Ct)).Allowed.ShouldBeTrue("one token came back after the period");
        (await store.TryTakeAsync(key, bucket, Ct)).Allowed.ShouldBeFalse("and only one");
    }

    /// <summary>The sliding window allows the limit in any window, and says when the oldest request leaves it.</summary>
    [Fact]
    public async Task The_sliding_window_frees_a_place_when_the_oldest_request_leaves_it()
    {
        await using var redis = await RedisAsync();
        await using var store = Store(redis.Address);
        var key = "rl:test:" + Guid.NewGuid().ToString("N");
        var window = new SharedLimit(SharedLimitKind.SlidingWindow, 2, 0, TimeSpan.FromMilliseconds(600));

        (await store.TryTakeAsync(key, window, Ct)).Allowed.ShouldBeTrue();
        (await store.TryTakeAsync(key, window, Ct)).Allowed.ShouldBeTrue();
        var full = await store.TryTakeAsync(key, window, Ct);
        full.Allowed.ShouldBeFalse();
        full.RetryAfter.ShouldBeInRange(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(600));

        await Task.Delay(full.RetryAfter + TimeSpan.FromMilliseconds(50), Ct);
        (await store.TryTakeAsync(key, window, Ct)).Allowed.ShouldBeTrue();
    }

    private static RedisRateLimitStore Store(string redis) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = redis }).Build());

    private static async Task<HttpStatusCode> GetAsync(WebApplication node, string client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/public");
        request.Headers.Add("X-Test-Client", client);
        using var response = await node.GetTestClient().SendAsync(request, Ct);
        return response.StatusCode;
    }

    /// <summary>A server's rate limiter and Redis set-up, with a public endpoint and a budget of <see cref="PublicLimit"/> a minute.</summary>
    private static async Task<WebApplication> NodeAsync(string redis)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = redis,
            ["HealthCare360:RateLimits:Public:PermitLimit"] = PublicLimit.ToString(CultureInfo.InvariantCulture),
            ["HealthCare360:RateLimits:Public:Window"] = "00:01:00",
        });
        builder.Services.AddOptions<PlatformOptions>().Bind(builder.Configuration.GetSection(PlatformOptions.SectionName));

        // Every node derives the same key from the hospital's data key.
        var protector = Substitute.For<IFieldProtector>();
        protector.DeriveKey(Arg.Any<string>()).Returns(new byte[32]);
        builder.Services.AddSingleton(protector);
        builder.Services.AddHospitalRateLimiter();
        ServerComposition.AddRealtime(builder.Services, builder.Configuration);

        var app = builder.Build();

        // The test server has no client address: the test names one, as a reverse proxy would.
        app.Use((http, next) =>
        {
            http.Connection.RemoteIpAddress = IPAddress.Parse(http.Request.Headers["X-Test-Client"].ToString());
            return next(http);
        });
        app.UseRateLimiter();
        app.MapGet("/public", () => "ok").RequireRateLimiting(RateLimitPolicies.Public);
        await app.StartAsync(Ct);
        return app;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A Redis server for one test: one of its own when <c>redis-server</c> is on the PATH, else the one named.</summary>
    private static async Task<TestRedis> RedisAsync()
    {
        var program = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? "redis-server.exe" : "redis-server"))
            .FirstOrDefault(File.Exists);
        if (program is null)
        {
            var named = Environment.GetEnvironmentVariable(RedisBackplaneTests.RedisVariable);
            Assert.SkipWhen(string.IsNullOrEmpty(named), $"Needs redis-server on the PATH, or {RedisBackplaneTests.RedisVariable} set to a Redis server.");
            return new TestRedis(named!, null);
        }

        var port = FreePort();
        var process = Process.Start(new ProcessStartInfo(program)
        {
            ArgumentList = { "--port", port.ToString(CultureInfo.InvariantCulture), "--bind", "127.0.0.1", "--save", "", "--appendonly", "no" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }).ShouldNotBeNull();
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Ready once it takes a connection.
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port, Ct);
                break;
            }
            catch (SocketException)
            {
                await Task.Delay(50, Ct);
            }
        }

        return new TestRedis($"127.0.0.1:{port}", process);
    }

    private sealed class TestRedis(string address, Process? process) : IAsyncDisposable
    {
        public string Address => address;

        public async ValueTask DisposeAsync()
        {
            if (process is null)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            process.Dispose();
        }
    }
}
