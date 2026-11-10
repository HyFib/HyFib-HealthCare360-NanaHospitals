using System.Threading.RateLimiting;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The request limits several servers share (SEC-06, SCAL-01): each request is counted once in the shared store; while the
/// store cannot be reached, each server counts on its own (never without a limit), says so on System health, and goes
/// back to the store after a while.
/// </summary>
public sealed class SharedRateLimitsTests
{
    private static readonly SharedLimit Three = SharedLimit.From(new FixedWindowLimit { PermitLimit = 3, Window = TimeSpan.FromMinutes(1) });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_request_is_counted_once_and_only_when_it_waits()
    {
        var store = new CountingStore(allowed: 2);
        using var limiter = Limiter(store, new ManualTime());

        // The middleware asks without waiting first: that must count nothing, or a request would count twice.
        using (var first = limiter.AttemptAcquire())
        {
            first.IsAcquired.ShouldBeFalse();
        }

        store.Calls.ShouldBe(0);

        (await limiter.AcquireAsync(1, Ct)).IsAcquired.ShouldBeTrue();
        (await limiter.AcquireAsync(1, Ct)).IsAcquired.ShouldBeTrue();
        using var refused = await limiter.AcquireAsync(1, Ct);
        refused.IsAcquired.ShouldBeFalse();
        refused.TryGetMetadata(MetadataName.RetryAfter, out var wait).ShouldBeTrue();
        wait.ShouldBe(TimeSpan.FromSeconds(42));
        store.Calls.ShouldBe(3);
        store.Keys.Distinct().ShouldHaveSingleItem().ShouldBe("rl:public:K");
    }

    [Fact]
    public async Task While_the_store_fails_each_server_counts_its_own_and_says_so()
    {
        var time = new ManualTime();
        var state = new SharedRateLimitState(time, NullLogger<SharedRateLimitState>.Instance);
        var store = new FailingStore();
        using var limiter = Limiter(store, time, state);

        // The first request finds the store down and is counted here; the next ones are counted here without asking it.
        (await limiter.AcquireAsync(1, Ct)).IsAcquired.ShouldBeTrue();
        limiter.AttemptAcquire().IsAcquired.ShouldBeTrue("counted here at once, without waiting for a store that is down");
        (await limiter.AcquireAsync(1, Ct)).IsAcquired.ShouldBeTrue();
        (await limiter.AcquireAsync(1, Ct)).IsAcquired.ShouldBeFalse("this server's own limit still holds: fail-safe, not fail-open");
        store.Calls.ShouldBe(1, "the store is not asked again for a while");

        state.CountingAloneSince.ShouldBe(time.GetUtcNow());
        var warning = (await new SharedRateLimitWarnings(state).WarningsAsync(Ct)).ShouldHaveSingleItem();
        (warning.Code, warning.Severity).ShouldBe(("ratelimit.per_server", "warning"));
        warning.Message.ShouldContain("Redis is down");

        // After a while the store is asked again; once it answers, the warning goes.
        time.Advance(SharedRateLimitState.RetryStoreAfter);
        store.Down = false;
        (await limiter.AcquireAsync(1, Ct)).IsAcquired.ShouldBeTrue();
        store.Calls.ShouldBe(2);
        state.CountingAloneSince.ShouldBeNull();
        (await new SharedRateLimitWarnings(state).WarningsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_store_that_does_not_answer_in_time_counts_as_down()
    {
        var state = new SharedRateLimitState(TimeProvider.System, NullLogger<SharedRateLimitState>.Instance);
        var hanging = Substitute.For<ISharedRateLimitStore>();
        hanging.TryTakeAsync(default!, default!, Ct).ReturnsForAnyArgs(new TaskCompletionSource<SharedLimitDecision>().Task);
        using var limiter = Limiter(hanging, TimeProvider.System, state);

        var started = TimeProvider.System.GetTimestamp();
        (await limiter.AcquireAsync(1, Ct)).IsAcquired.ShouldBeTrue("counted on this server instead");
        TimeProvider.System.GetElapsedTime(started).ShouldBeLessThan(SharedRateLimitState.CallTimeout + TimeSpan.FromSeconds(2));
        state.CountingAloneSince.ShouldNotBeNull();
        state.Problem.ShouldNotBeNull().ShouldContain("did not answer within 500 ms");
    }

    [Fact]
    public void The_store_holds_no_address()
    {
        var protector = Substitute.For<IFieldProtector>();
        protector.DeriveKey("shared-rate-limit").Returns(new byte[32]);
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(protector).BuildServiceProvider() };
        var limits = new SharedRateLimits(new CountingStore(1), new SharedRateLimitState(TimeProvider.System, NullLogger<SharedRateLimitState>.Instance), TimeProvider.System);

        var key = limits.StoreKey(http, RateLimitPolicies.Public, "203.0.113.7");
        key.ShouldStartWith("rl:public:");
        key.ShouldNotContain("203.0.113.7");
        key.ShouldBe(limits.StoreKey(http, RateLimitPolicies.Public, "203.0.113.7"), "the same client always counts under one key");
        key.ShouldNotBe(limits.StoreKey(http, RateLimitPolicies.Public, "203.0.113.8"));
        key.ShouldNotBe(limits.StoreKey(http, RateLimitPolicies.Device, "203.0.113.7"), "each policy counts on its own");
    }

    [Fact]
    public void Limits_count_in_the_store_as_the_settings_say()
    {
        var defaults = new RateLimitOptions();
        SharedLimit.From(defaults.Public).ShouldBe(new SharedLimit(SharedLimitKind.SlidingWindow, 60, 0, TimeSpan.FromMinutes(1)));
        SharedLimit.From(defaults.PerUser).ShouldBe(new SharedLimit(SharedLimitKind.TokenBucket, 400, 200, TimeSpan.FromSeconds(10)));
    }

    private static SharedRateLimiter Limiter(ISharedRateLimitStore store, TimeProvider time, SharedRateLimitState? state = null) =>
        new("rl:public:K", Three, store, state ?? new SharedRateLimitState(time, NullLogger<SharedRateLimitState>.Instance),
            new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromMinutes(1), AutoReplenishment = false }),
            time);

    private sealed class CountingStore(int allowed) : ISharedRateLimitStore
    {
        public int Calls { get; private set; }

        public List<string> Keys { get; } = [];

        public Task<SharedLimitDecision> TryTakeAsync(string key, SharedLimit limit, CancellationToken cancellationToken)
        {
            Calls++;
            Keys.Add(key);
            return Task.FromResult(new SharedLimitDecision(Calls <= allowed, Calls <= allowed ? TimeSpan.Zero : TimeSpan.FromSeconds(42)));
        }
    }

    private sealed class FailingStore : ISharedRateLimitStore
    {
        public bool Down { get; set; } = true;

        public int Calls { get; private set; }

        public Task<SharedLimitDecision> TryTakeAsync(string key, SharedLimit limit, CancellationToken cancellationToken)
        {
            Calls++;
            return Down
                ? Task.FromException<SharedLimitDecision>(new InvalidOperationException("Redis is down"))
                : Task.FromResult(new SharedLimitDecision(true, TimeSpan.Zero));
        }
    }

    /// <summary>A clock the test moves on; timers still run on the system's.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by) => _now += by;
    }
}
