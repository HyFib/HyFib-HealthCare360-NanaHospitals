using System.Net;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The outbox through an internet outage and after a restore, against PostgreSQL: messages are taken one at a time and
/// locked, each outcome committed on its own; messages whose time is up end unsent, held or not, and System health counts
/// them; after a restore only the messages queued before it are held, System health counts them without paging IT on-call
/// a second time, and those who manage the server release them, audited; and the alerts that stay inside the hospital are
/// on the local lane, never behind SMS and WhatsApp.
/// </summary>
public sealed class OutboxLaneTests(HospitalServer server) : IAsyncLifetime
{
    private const string Health = "/api/v1/admin/system/health";
    private const string Release = "/api/v1/admin/system/outbox/release";

    // The message types this test queued, of its own: their rows go when it ends, so no other test sees them waiting.
    private readonly List<string> _types = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var types = _types.ToArray();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>()
            .Where(m => types.Contains(m.Type))
            .ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Messages_are_taken_one_at_a_time_each_locked_to_one_taker_and_each_outcome_committed_on_its_own()
    {
        // A type no handler has, due tomorrow: the server's own dispatcher, on today's clock, leaves them alone.
        var type = NewType();
        var due = DateTimeOffset.UtcNow.AddDays(1);
        var first = await AddAsync(type, due);
        var second = await AddAsync(type, due.AddSeconds(1));
        var store = new OutboxStore(server.Services.GetRequiredService<IServiceScopeFactory>());

        (await store.DueTypesAsync(due.AddSeconds(5), null, Ct)).ShouldContain(type);
        await using (var taken = (await store.TakeAsync([type], due.AddSeconds(5), null, Ct)).ShouldNotBeNull())
        {
            taken.Message.Id.ShouldBe(first);

            // Another taker, another server say, skips the locked message for the next one.
            await using (var other = (await store.TakeAsync([type], due.AddSeconds(5), null, Ct)).ShouldNotBeNull())
            {
                other.Message.Id.ShouldBe(second);
                (await store.TakeAsync([type], due.AddSeconds(5), null, Ct)).ShouldBeNull("both are taken");
            }

            taken.Message.Status = OutboxStatus.Succeeded;
            taken.Message.ProcessedAt = due;
            await taken.CommitAsync(Ct);
        }

        // The first is committed as sent; the second, whose taker ended without committing, waits as it was.
        (await MessageAsync(first)).Status.ShouldBe(OutboxStatus.Succeeded);
        (await MessageAsync(second)).ShouldSatisfyAllConditions(m => m.Status.ShouldBe(OutboxStatus.Pending), m => m.Attempts.ShouldBe(0));
    }

    [Fact]
    public async Task What_a_handler_changed_is_undone_when_it_cannot_be_saved_and_the_outcome_is_saved_alone()
    {
        var type = NewType();
        var due = DateTimeOffset.UtcNow.AddDays(1);
        var id = await AddAsync(type, due);
        var store = new OutboxStore(server.Services.GetRequiredService<IServiceScopeFactory>());

        await using (var taken = (await store.TakeAsync([type], due.AddSeconds(5), null, Ct)).ShouldNotBeNull())
        {
            // A handler's statement fails in the transaction, which PostgreSQL then refuses to go on with.
            var db = taken.Services.GetRequiredService<HospitalDbContext>();
            await Should.ThrowAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlRawAsync("SELECT 1 / 0", Ct));

            await taken.UndoHandlerAsync(Ct);
            taken.Message.Attempts = 1;
            taken.Message.LastError = "Delivered, but what it changed could not be saved.";
            taken.Message.NextAttemptAt = due.AddSeconds(15);
            await taken.CommitAsync(Ct);
        }

        (await MessageAsync(id)).ShouldSatisfyAllConditions(
            m => m.Status.ShouldBe(OutboxStatus.Pending),
            m => m.Attempts.ShouldBe(1),
            m => m.LastError.ShouldBe("Delivered, but what it changed could not be saved."));
    }

    [Fact]
    public async Task Messages_whose_time_is_up_end_unsent_whether_due_or_not_and_System_health_counts_those_that_expired()
    {
        // Not due (held, or their provider cooling down), and their time is up: one never tried, one tried before.
        var type = NewType();
        var now = DateTimeOffset.UtcNow;
        var neverTried = await AddAsync(type, now.AddHours(1), expiresAt: now.AddMinutes(-1));
        var triedBefore = await AddAsync(type, now.AddHours(1), expiresAt: now.AddMinutes(-1), attempts: 2);
        var store = new OutboxStore(server.Services.GetRequiredService<IServiceScopeFactory>());

        // The server's own dispatcher ends such messages once a minute too: either way, they end so.
        await store.ExpireAsync(DateTimeOffset.UtcNow, Ct);

        (await MessageAsync(neverTried)).Status.ShouldBe(OutboxStatus.Expired);
        (await MessageAsync(triedBefore)).Status.ShouldBe(OutboxStatus.Failed);

        await using var scope = server.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var outbox = await SystemHealthOutbox.ReadAsync(
            services.GetRequiredService<HospitalDbContext>(), services.GetRequiredService<ISettingsService>(), services.GetServices<IOutboxHandler>(),
            DateTimeOffset.UtcNow, Ct);
        outbox.ExpiredLast24Hours.ShouldBeGreaterThanOrEqualTo(1);
        outbox.FailedLast24Hours.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task While_held_only_the_messages_queued_before_the_hold_began_wait()
    {
        // One queued before the hold began, one after it: due tomorrow, so the server's own dispatcher leaves them alone.
        var type = NewType();
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var due = DateTimeOffset.UtcNow.AddDays(1);
        var before = await AddAsync(type, due, queuedAt: since.AddMinutes(-5));
        var after = await AddAsync(type, due.AddSeconds(1), queuedAt: since.AddSeconds(1));
        var store = new OutboxStore(server.Services.GetRequiredService<IServiceScopeFactory>());

        (await store.DueTypesAsync(due.AddSeconds(5), since, Ct)).ShouldContain(type);
        await using (var taken = (await store.TakeAsync([type], due.AddSeconds(5), since, Ct)).ShouldNotBeNull())
        {
            taken.Message.Id.ShouldBe(after, "the one queued before the hold began is held, though it fell due first");
        }

        (await MessageAsync(before)).Status.ShouldBe(OutboxStatus.Pending);
    }

    [Fact]
    public async Task Held_messages_are_counted_on_System_health_until_one_who_manages_the_server_releases_them()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        using (var notHeld = await admin.SendRawAsync(HttpMethod.Post, Release))
        {
            notHeld.StatusCode.ShouldBe(HttpStatusCode.Conflict, "nothing is held");
            (await notHeld.Content.ReadAsStringAsync(Ct)).ShouldContain("outbox.not_held");
        }

        // A message waiting from before the hold began, of a type no handler has (so on the internet lane), due tomorrow.
        var since = DateTimeOffset.UtcNow.AddMinutes(-2);
        await AddAsync(NewType(), DateTimeOffset.UtcNow.AddDays(1), queuedAt: since.AddMinutes(-1));

        // As the restore sets it: it holds only what was queued before it, so the test server's other tests go on.
        var restore = new OutboxHold(OutboxHold.RestoreReason, since, since.AddHours(-6));
        await SetHoldAsync(restore);
        try
        {
            var report = await admin.GetAsync<SystemHealthReport>(Health);
            report.Outbox.Hold.ShouldBe(restore);
            report.Outbox.HeldMessages.ShouldBeGreaterThanOrEqualTo(1);
            var warning = report.Warnings.Single(w => w.Code == SystemHealthOutbox.HeldCode);
            warning.Severity.ShouldBe("warning", "the restore's own critical warning tells IT on-call, once");
            warning.Message.ShouldBe(restore.Warning(report.Outbox.HeldMessages));

            // Held for another reason, nothing else tells of it: critical.
            await SetHoldAsync(restore with { Reason = "The SMS gateway is being changed" });
            (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.Single(w => w.Code == SystemHealthOutbox.HeldCode).Severity.ShouldBe("critical");

            using (var refused = await hospitalAdmin.SendRawAsync(HttpMethod.Post, Release))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "only those who manage the server release held messages");
            }

            var released = await admin.PostAsync<OutboxReleased>(Release);
            released.Held.ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(OutboxHold.SettingKey, null, Ct);
        }

        // Released: no warning, no hold, and the release is in the audit trail with what the hold said.
        var afterRelease = await admin.GetAsync<SystemHealthReport>(Health);
        afterRelease.Outbox.Hold.ShouldBeNull();
        afterRelease.Warnings.ShouldNotContain(w => w.Code == SystemHealthOutbox.HeldCode);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var audit = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
                .Where(e => e.Action == OutboxHolds.ReleasedAction && e.OccurredAt >= since)
                .OrderByDescending(e => e.Id)
                .FirstAsync(Ct);
            audit.Summary.ShouldNotBeNull().ShouldContain("The SMS gateway is being changed");
            audit.UserName.ShouldBe(HospitalServer.Users.Admin);
        }
    }

    [Fact]
    public void The_alerts_that_stay_inside_the_hospital_are_on_the_local_lane_and_the_rest_go_to_the_internet()
    {
        using var scope = server.Services.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().ToDictionary(h => h.Type);

        // In the app, realtime, the database, or a system on the LAN: never behind SMS and WhatsApp, nor held after a restore.
        string[] local =
        [
            "supplychain.cold-chain-alert", "diagnostics.realtime", "diagnostics.staff-alert", "diagnostics.user-alert",
            "pharmacy.queue.changed", "pharmacy.ndps-alert", "clinical.idsp-alert", "clinical.ams-alert", "lab.analyser.order",
            "billing.einvoice.reminder", "billing.appointment.prepayment", "billing.tally.post", "billing.tally.check",
        ];
        handlers.Where(h => h.Value.Lane == OutboxLane.Local).Select(h => h.Key).ShouldBe(local, ignoreOrder: true);

        // The internet, each provider's messages together.
        (string Type, string Provider)[] internet =
        [
            ("notify.sms", OutboxProviders.Sms), ("notify.whatsapp", OutboxProviders.WhatsApp), ("whatsapp.reply_confirmation", OutboxProviders.WhatsApp),
            ("notify.email", OutboxProviders.Email), ("analytics.report.email", OutboxProviders.Email),
            ("abdm.transfer", "abdm"), ("abdm.link_record", "abdm"),
        ];
        internet.ShouldAllBe(i => handlers[i.Type].Lane == OutboxLane.Internet && handlers[i.Type].Provider == i.Provider);
        handlers["billing.tally.post"].Provider.ShouldBe(handlers["billing.tally.check"].Provider);
    }

    /// <summary>A message type of this test's own, which no handler has: the server's dispatcher fails such a message at once
    /// when it is due, so the tests queue theirs due tomorrow.</summary>
    private string NewType()
    {
        var type = "it.outbox." + Guid.NewGuid().ToString("N")[..8];
        _types.Add(type);
        return type;
    }

    private async Task SetHoldAsync(OutboxHold hold)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(OutboxHold.SettingKey, hold, cancellationToken: Ct);
    }

    private async Task<Guid> AddAsync(string type, DateTimeOffset due, DateTimeOffset? expiresAt = null, int attempts = 0, DateTimeOffset? queuedAt = null)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var message = new OutboxMessage
        {
            Type = type,
            CreatedAt = queuedAt ?? DateTimeOffset.UtcNow,
            NextAttemptAt = due,
            ExpiresAt = expiresAt ?? due.AddDays(1),
            Attempts = attempts,
            LastError = attempts > 0 ? "The gateway could not be reached." : null,
            Status = OutboxStatus.Pending,
        };
        db.Add(message);
        await db.SaveChangesAsync(Ct);
        return message.Id;
    }

    private async Task<OutboxMessage> MessageAsync(Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == id, Ct);
    }
}
