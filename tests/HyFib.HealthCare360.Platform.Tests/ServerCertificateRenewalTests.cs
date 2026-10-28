using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The LAN server certificate is issued again before it expires while the server runs, whether or not its network
/// changed: HC360 Desktop, browsers and tablets refuse an expired certificate, so a server left running would lock every
/// workstation out on the day it expired.
/// </summary>
public sealed class ServerCertificateRenewalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_server_that_runs_for_a_year_without_a_network_change_serves_a_renewed_certificate()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var options = new PlatformOptions { DataDirectory = _root };
        using var server = new LanCertificateManager(
            new DataDirectories(options), options, new EphemeralDataProtectionProvider(), NullLogger<LanCertificateManager>.Instance, clock);
        server.EnsureCertificates();
        var issued = server.ServerExpiresAt.ShouldNotBeNull();
        var ca = server.CaFingerprint;

        // Two months before it expires the certificate is kept.
        clock.MoveTo(issued.AddDays(-60));
        server.ServerExpiresAt.ShouldBe(issued);

        // Four weeks before, the server issues it again by itself, from the CA every workstation pinned.
        clock.MoveTo(issued.AddDays(-28));
        server.ServerExpiresAt.ShouldNotBeNull().ShouldBeGreaterThan(issued.AddDays(300));
        server.ServerCertificate.NotAfter.ToUniversalTime().ShouldBe(server.ServerExpiresAt.Value.UtcDateTime);
        server.CaFingerprint.ShouldBe(ca);
    }

    /// <summary>A clock the test moves on: each timer made from it comes round when it is due, once per move.</summary>
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private readonly List<Timer> _timers = [];

        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(callback, state, _now + dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void MoveTo(DateTimeOffset at)
        {
            _now = at;
            foreach (var timer in _timers.Where(t => !t.Stopped && t.Due <= at).ToList())
            {
                timer.Due = at + timer.Period;
                timer.Callback(timer.State);
            }
        }

        private sealed class Timer(TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period) : ITimer
        {
            public TimerCallback Callback { get; } = callback;

            public object? State { get; } = state;

            public DateTimeOffset Due { get; set; } = due;

            public TimeSpan Period { get; private set; } = period;

            public bool Stopped { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Period = period;
                return true;
            }

            public void Dispose() => Stopped = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
