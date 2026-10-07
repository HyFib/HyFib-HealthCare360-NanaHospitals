using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>Queued messages are retried until they are delivered or expire (SRS DED-11).</summary>
public sealed class OutboxRetryPolicyTests
{
    private static readonly DateTimeOffset Queued = new(2026, 10, 6, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Waits_twice_as_long_after_each_failure_up_to_an_hour() =>
        Enumerable.Range(1, 12).Select(n => OutboxRetryPolicy.WaitAfter(n).TotalSeconds)
            .ShouldBe([10, 20, 40, 80, 160, 320, 640, 1280, 2560, 3600, 3600, 3600]);

    [Fact]
    public void Keeps_trying_through_an_internet_outage_that_lasts_all_day()
    {
        // Twelve tries used to be the limit, about 3.4 hours: a longer outage lost the message.
        var message = Message(expiresAt: null);
        var now = Queued;
        while (now < Queued + TimeSpan.FromHours(24))
        {
            OutboxDispatcher.Fail(message, now, "No route to host", permanent: false, TimeSpan.Zero);
            message.Status.ShouldBe(OutboxStatus.Pending);
            now = message.NextAttemptAt;
        }

        message.Attempts.ShouldBeGreaterThan(12);
        (message.NextAttemptAt - now).ShouldBeLessThanOrEqualTo(OutboxRetryPolicy.LongestWait);
    }

    [Fact]
    public void Gives_up_as_failed_when_the_next_try_would_come_after_it_expires()
    {
        // A sign-in code's SMS is worth sending only while the code works.
        var expires = Queued + TimeSpan.FromMinutes(10);
        var message = Message(expiresAt: expires);
        var now = Queued;
        while (message.Status == OutboxStatus.Pending)
        {
            now.ShouldBeLessThan(expires);
            OutboxDispatcher.Fail(message, now, "SMS gateway unreachable", permanent: false, TimeSpan.Zero);
            now = message.NextAttemptAt;
        }

        message.Status.ShouldBe(OutboxStatus.Failed);
        message.ProcessedAt.ShouldNotBeNull().ShouldBeLessThan(expires);
        message.LastError.ShouldBe("SMS gateway unreachable");
    }

    [Fact]
    public void A_permanent_failure_ends_the_message_at_once()
    {
        var message = Message(expiresAt: null);

        OutboxDispatcher.Fail(
            message, Queued, "No handler registered for 'notify.fax'.", permanent: true, TimeSpan.Zero);

        message.Status.ShouldBe(OutboxStatus.Failed);
        message.Attempts.ShouldBe(1);
        message.ProcessedAt.ShouldBe(Queued);
    }

    [Fact]
    public void A_message_never_tried_before_it_expired_is_expired_and_one_that_kept_failing_is_failed()
    {
        var expires = Queued + TimeSpan.FromHours(1);
        var neverTried = Message(expiresAt: expires);
        var keptFailing = Message(expiresAt: expires);
        OutboxDispatcher.Fail(keptFailing, Queued, "Timeout", permanent: false, TimeSpan.Zero);

        OutboxDispatcher.ExpireIfDue(neverTried, expires).ShouldBeTrue();
        OutboxDispatcher.ExpireIfDue(keptFailing, expires).ShouldBeTrue();

        neverTried.Status.ShouldBe(OutboxStatus.Expired);
        keptFailing.Status.ShouldBe(OutboxStatus.Failed);
        keptFailing.LastError.ShouldBe("Timeout");
        keptFailing.ProcessedAt.ShouldBe(expires);
    }

    [Fact]
    public void A_message_with_time_left_is_tried()
    {
        var message = Message(expiresAt: Queued + TimeSpan.FromHours(1));

        OutboxDispatcher.ExpireIfDue(message, Queued + TimeSpan.FromMinutes(59)).ShouldBeFalse();

        message.Status.ShouldBe(OutboxStatus.Pending);
        message.ProcessedAt.ShouldBeNull();
    }

    [Fact]
    public void A_message_queued_without_an_expiry_by_an_older_version_is_tried_for_the_default_lifetime()
    {
        var message = Message(expiresAt: null);

        var expires = Queued + OutboxRetryPolicy.DefaultLifetime;
        OutboxRetryPolicy.ExpiryOf(message).ShouldBe(expires);
        OutboxDispatcher.ExpireIfDue(message, expires - TimeSpan.FromMinutes(1)).ShouldBeFalse();
        OutboxDispatcher.ExpireIfDue(message, expires).ShouldBeTrue();
    }

    [Fact]
    public void Queuing_without_an_expiry_gives_the_default_lifetime_from_when_the_message_is_due()
    {
        using var db = ModelOnlyContext();
        var outbox = new Outbox(db, Substitute.For<ICurrentUser>(), new FixedClock(Queued));

        outbox.Enqueue("billing.tally.check", new { Day = "2026-10-05" }, delay: TimeSpan.FromSeconds(10));

        var message = db.ChangeTracker.Entries<OutboxMessage>().ShouldHaveSingleItem().Entity;
        message.NextAttemptAt.ShouldBe(Queued + TimeSpan.FromSeconds(10));
        message.ExpiresAt.ShouldBe(Queued + TimeSpan.FromSeconds(10) + OutboxRetryPolicy.DefaultLifetime);
    }

    [Fact]
    public void Queuing_with_an_expiry_keeps_it()
    {
        using var db = ModelOnlyContext();
        var outbox = new Outbox(db, Substitute.For<ICurrentUser>(), new FixedClock(Queued));

        outbox.Enqueue("notify.sms", new { Code = "123456" }, expiresIn: TimeSpan.FromMinutes(10));

        var message = db.ChangeTracker.Entries<OutboxMessage>().ShouldHaveSingleItem().Entity;
        message.ExpiresAt.ShouldBe(Queued + TimeSpan.FromMinutes(10));
    }

    private static OutboxMessage Message(DateTimeOffset? expiresAt) => new()
    {
        Type = "notify.sms",
        CreatedAt = Queued,
        NextAttemptAt = Queued,
        ExpiresAt = expiresAt,
        Status = OutboxStatus.Pending,
    };

    /// <summary>The platform's EF model with the Npgsql provider; nothing is sent to a database.</summary>
    private static HospitalDbContext ModelOnlyContext() => new(
        new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options,
        new ModuleCatalog([]));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
