using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Alerts that reach someone (audit P2), over the real API, database and alerts. A drill's test warning is told as any
/// critical warning is, and keeps when each step happened; drills started at once start one. The heartbeat's address is
/// kept secret, the heartbeat fetches it, and System health says when that fails. A live server says when a critical
/// warning would not reach IT on-call, and when nothing outside the hospital would notice it stop.
/// </summary>
public sealed class AlertingTests(HospitalServer server)
{
    private const string Drills = "/api/v1/admin/system/alert-drills";
    private const string Health = "/api/v1/admin/system/health";
    private const string Settings = "/api/v1/admin/settings";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_drill_is_told_as_a_critical_warning_and_keeps_when_each_step_happened()
    {
        const string onCallPhone = "+919800000143";
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await using var onCall = await SettingAsync("alerts.oncallPhone", onCallPhone);
        using (var refused = await hospitalAdmin.SendRawAsync(HttpMethod.Post, Drills))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "only those who manage the server start a drill");
        }

        // Started by several at once, as a double click or two people do: one drill starts, the others are refused.
        var starts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => admin.SendRawAsync(HttpMethod.Post, Drills)));
        try
        {
            starts.Count(r => r.IsSuccessStatusCode).ShouldBe(1, "one drill at a time, however many start one at once");
            starts.Where(r => !r.IsSuccessStatusCode).ShouldAllBe(r => r.StatusCode == HttpStatusCode.Conflict);
            var drill = await ApiClient.ReadAsync<AlertDrill>(starts.Single(r => r.IsSuccessStatusCode), HttpMethod.Post, Drills);
            drill.State.ShouldBe("open");
            using (var second = await admin.SendRawAsync(HttpMethod.Post, Drills))
            {
                second.StatusCode.ShouldBe(HttpStatusCode.Conflict, "one drill at a time");
            }

            // System health has its test warning, critical, until someone acknowledges it.
            var warning = (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.Single(w => w.Code == AlertDrills.WarningPrefix + drill.Code);
            warning.Severity.ShouldBe("critical");
            warning.Message.ShouldContain("Nothing is wrong");

            // The alerts tell it as they tell any critical warning: in the app, and to IT on-call by SMS.
            using var alerts = ActivatorUtilities.CreateInstance<SystemHealthAlerts>(server.Services);
            (await alerts.CheckAsync(Ct)).ShouldContain(w => w.Code == warning.Code);

            var told = (await admin.GetAsync<List<AlertDrill>>(Drills)).Single(d => d.Id == drill.Id);
            told.ToldAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(drill.StartedAt);
            told.ToldTo.ShouldBeGreaterThan(0, "everyone who may see System health has it in the app");
            // By SMS to IT on-call, who has no WhatsApp alerts here. The server's own alerts, running every five minutes, may
            // tell it too.
            var masked = Phone.Mask(Phone.Normalize(onCallPhone));
            told.Messages.ShouldNotBeEmpty();
            told.Messages.ShouldAllBe(m => m.Channel == NotificationChannel.Sms && m.Recipient == masked && m.QueuedAt >= drill.StartedAt);

            // Whoever may see System health acknowledges it, and the warning goes.
            var acknowledged = await hospitalAdmin.PostAsync<AlertDrill>($"{Drills}/{drill.Id}/acknowledge");
            (acknowledged.State, acknowledged.ToldAt).ShouldBe(("acknowledged", told.ToldAt));
            acknowledged.AcknowledgedAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(told.ToldAt.Value);
            acknowledged.AcknowledgedBy.ShouldNotBeNullOrWhiteSpace();
            (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.ShouldNotContain(w => w.Code.StartsWith(AlertDrills.WarningPrefix, StringComparison.Ordinal));
            using var again = await hospitalAdmin.SendRawAsync(HttpMethod.Post, $"{Drills}/{drill.Id}/acknowledge");
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var start in starts)
            {
                start.Dispose();
            }

            // The server is shared by every test of the run: no drill is left open.
            foreach (var open in (await admin.GetAsync<List<AlertDrill>>(Drills)).Where(d => d.State == "open"))
            {
                using var _ = await admin.SendRawAsync(HttpMethod.Post, $"{Drills}/{open.Id}/acknowledge");
            }
        }
    }

    [Fact]
    public async Task The_heartbeat_fetches_its_outside_address_and_system_health_says_when_it_fails()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var heartbeat = server.Services.GetRequiredService<AlertHeartbeat>();
        var token = Guid.NewGuid().ToString("N");
        await using var monitor = Monitor.Start();

        // Set as an administrator sets it, without asking for a secret. The address holds the monitor's token: it is kept
        // encrypted, never shown again, and left out of the audit trail.
        await admin.PutAsync($"{Settings}/{AlertHeartbeat.UrlSetting}", new SaveSettingRequest(JsonValue.Create($"{monitor.Address}ping/{token}"), null, false, null));
        try
        {
            var saved = (await admin.GetAsync<List<SettingDto>>($"{Settings}?prefix={AlertHeartbeat.UrlSetting}")).Single(s => s.Key == AlertHeartbeat.UrlSetting);
            (saved.IsSecret, saved.HasValue, saved.Value is null).ShouldBe((true, true, true), "the address is a secret, however it was saved");
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                (await db.Set<SystemSetting>().AsNoTracking().SingleAsync(s => s.Key == AlertHeartbeat.UrlSetting && s.FacilityId == null, Ct)).Value
                    .ShouldNotContain(token, Case.Sensitive, "kept encrypted, in the database and every backup");
                (await db.Set<AuditEvent>().AsNoTracking().Where(e => e.Action == "setting.changed" && e.EntityId == AlertHeartbeat.UrlSetting)
                    .OrderByDescending(e => e.OccurredAt).FirstAsync(Ct)).Details.ShouldNotBeNull().ShouldNotContain(token, Case.Sensitive);
            }

            var ok = await heartbeat.BeatAsync(force: true, Ct);
            (ok.Host, ok.Failures, ok.LastError).ShouldBe(("127.0.0.1", 0, (string?)null));
            ok.LastSuccessAt.ShouldNotBeNull();
            monitor.Requests.ShouldContain($"GET /ping/{token}");

            // The monitor gone: three failures in a row, and System health says so, naming the host but not the token.
            await monitor.DisposeAsync();
            for (var i = 0; i < AlertHeartbeat.FailuresToWarn; i++)
            {
                await heartbeat.BeatAsync(force: true, Ct);
            }

            var warning = (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.Single(w => w.Code == AlertHeartbeat.FailingCode);
            warning.Severity.ShouldBe("warning");
            warning.Message.ShouldContain("127.0.0.1");
            warning.Message.ShouldNotContain(token);
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(AlertHeartbeat.UrlSetting, null, CancellationToken.None);
        }

        // With the address taken away, the heartbeat is off and says nothing.
        (await heartbeat.BeatAsync(force: true, Ct)).Host.ShouldBeNull();
        (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.ShouldNotContain(w => w.Code == AlertHeartbeat.FailingCode);
    }

    [Fact]
    public async Task A_live_server_says_when_a_critical_warning_would_not_reach_IT_on_call()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        await using var onCall = await SettingAsync("alerts.oncallPhone", "+919800000144");
        await using var sms = await SettingAsync("integrations.sms", new { enabled = true, provider = "msg91" });
        await using var dlt = await AlertTemplateDltIdAsync(null);

        // A training server may leave alerts unset.
        var training = (await admin.GetAsync<SystemHealthReport>(Health)).Warnings;
        training.ShouldNotContain(w => w.Code == AlertReadiness.Code);
        training.ShouldNotContain(w => w.Code == AlertHeartbeat.OffCode);

        using (server.AsLiveServer())
        {
            var live = (await admin.GetAsync<SystemHealthReport>(Health)).Warnings;
            var notReady = live.Single(w => w.Code == AlertReadiness.Code);
            notReady.Severity.ShouldBe("critical", "MSG91 refuses every alert SMS whose template has no DLT template id");
            notReady.Message.ShouldContain("MSG91 requires");
            live.Single(w => w.Code == AlertHeartbeat.OffCode).Severity.ShouldBe("warning", "nothing outside the hospital notices if the server stops");

            await using (await AlertTemplateDltIdAsync("1107161234567890123"))
            {
                (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.ShouldNotContain(w => w.Code == AlertReadiness.Code);
            }
        }
    }

    /// <summary>
    /// A setting changed for the rest of the test, then put back as it was: the server is shared by every test of the run.
    /// </summary>
    private async Task<IAsyncDisposable> SettingAsync<T>(string key, T value)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var before = await settings.GetAsync<JsonElement?>(key, cancellationToken: Ct);
        await settings.SetAsync(key, value, cancellationToken: Ct);
        return new Restore(async () =>
        {
            await using var restore = server.Services.CreateAsyncScope();
            var saved = restore.ServiceProvider.GetRequiredService<ISettingsService>();
            if (before is { } previous)
            {
                await saved.SetAsync(key, previous, cancellationToken: CancellationToken.None);
            }
            else
            {
                await saved.RemoveAsync(key, null, CancellationToken.None);
            }
        });
    }

    /// <summary>The hospital-wide system.alert SMS templates' DLT template id, for the rest of the test.</summary>
    private async Task<IAsyncDisposable> AlertTemplateDltIdAsync(string? dltTemplateId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var templates = await db.Set<NotificationTemplate>()
            .Where(t => t.Key == AlertReadiness.AlertTemplate && t.Channel == NotificationChannel.Sms && t.FacilityId == null && t.IsActive)
            .ToListAsync(Ct);
        templates.ShouldNotBeEmpty("the hospital-wide system.alert SMS template is seeded");
        var before = templates.ToDictionary(t => t.Id, t => t.DltTemplateId);
        await SetDltIdAsync(templates.ToDictionary(t => t.Id, _ => dltTemplateId), Ct);
        return new Restore(() => SetDltIdAsync(before, CancellationToken.None));
    }

    private async Task SetDltIdAsync(Dictionary<Guid, string?> ids, CancellationToken ct)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        foreach (var (id, dltTemplateId) in ids)
        {
            await db.Set<NotificationTemplate>().Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.DltTemplateId, dltTemplateId), ct);
        }
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }

    /// <summary>A heartbeat monitor on this machine: it answers every fetch with 200 and keeps what was fetched.</summary>
    private sealed class Monitor : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private Task? _answering;

        private Monitor(Uri address) => Address = address;

        public Uri Address { get; }

        public ConcurrentQueue<string> Requests { get; } = new();

        public static Monitor Start()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var monitor = new Monitor(new Uri($"http://127.0.0.1:{port}/"));
            monitor._listener.Prefixes.Add(monitor.Address.AbsoluteUri);
            monitor._listener.Start();
            monitor._answering = Task.Run(monitor.AnswerAsync);
            return monitor;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_listener.IsListening)
            {
                return;
            }

            _listener.Stop();
            _listener.Close();
            await (_answering ?? Task.CompletedTask);
        }

        private async Task AnswerAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                Requests.Enqueue($"{context.Request.HttpMethod} {context.Request.Url!.AbsolutePath}");
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        }
    }
}
