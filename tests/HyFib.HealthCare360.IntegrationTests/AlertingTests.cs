using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Messaging;
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
/// warning would not reach IT on-call, and when nothing outside the hospital would notice it stop. A server of several that
/// stopped reporting and reports again is told back to everyone its alert reached, never before the alert itself, and
/// never when it is gone rather than back.
/// </summary>
public sealed class AlertingTests(HospitalServer server)
{
    private const string Drills = "/api/v1/admin/system/alert-drills";
    private const string Alerts = "/api/v1/admin/system/alerts";
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
            // The server's own alerts, every five minutes, may be the ones to tell it: either way, it is told once.
            using var alerts = ActivatorUtilities.CreateInstance<SystemHealthAlerts>(server.Services);
            await alerts.CheckAsync(Ct);

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
            (await IncidentAsync(admin, AlertDrills.WarningPrefix + drill.Code)).AcknowledgedAt.ShouldNotBeNull(
                "its alert with it, so the report counts it acknowledged, not one that cleared unseen");
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
    public async Task An_alert_nobody_acknowledges_reaches_the_second_contact_then_HyFib_support_and_the_report_says_how_far_it_went()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await using var onCall = await SettingAsync("alerts.oncallPhone", "+919800000146");
        await using var second = await SettingAsync(AlertEscalation.SecondContactSetting, "+919800000147");
        await using var support = await SettingAsync(AlertEscalation.SupportSetting, "+919800000148");
        await using var escalateAt = await SettingAsync(AlertEscalation.EscalateMinutesSetting, 0);
        await using var supportAt = await SettingAsync(AlertEscalation.SupportMinutesSetting, 0);
        var drill = await admin.PostAsync<AlertDrill>(Drills);
        try
        {
            // Nobody acknowledges it: IT on-call is told at once, and with no wait set, each check pages the next.
            var code = AlertDrills.WarningPrefix + drill.Code;
            using var alerts = ActivatorUtilities.CreateInstance<SystemHealthAlerts>(server.Services);
            for (var check = 0; check < 3; check++)
            {
                await alerts.CheckAsync(Ct);
            }

            var paged = await IncidentAsync(admin, code);
            paged.Level.ShouldBe(AlertLevel.Support);
            var told = paged.ToldAt.ShouldNotBeNull();
            paged.SecondContactAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(told);
            paged.SupportAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(paged.SecondContactAt.Value);
            paged.Messages.Where(m => m.Level == AlertLevel.OnCall).ShouldAllBe(m => m.Recipient == Masked("+919800000146"));
            paged.Messages.Where(m => m.Level == AlertLevel.SecondContact).ShouldHaveSingleItem().Recipient.ShouldBe(Masked("+919800000147"));
            paged.Messages.Where(m => m.Level == AlertLevel.Support).ShouldHaveSingleItem().Recipient.ShouldBe(Masked("+919800000148"));
            (await EscalationsAsync(paged.Id)).ShouldBe(2, "each escalation is in the audit trail");

            // Acknowledged at last: by whom, and the drill with it; the warning goes, and its alert is closed.
            var acknowledged = await hospitalAdmin.PostAsync<AlertIncidentDto>($"{Alerts}/{paged.Id}/acknowledge");
            acknowledged.AcknowledgedBy.ShouldNotBeNullOrWhiteSpace();
            acknowledged.MinutesToAcknowledge.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(0);
            (await admin.GetAsync<List<AlertDrill>>(Drills)).Single(d => d.Id == drill.Id).State.ShouldBe("acknowledged");
            using (var again = await hospitalAdmin.SendRawAsync(HttpMethod.Post, $"{Alerts}/{paged.Id}/acknowledge"))
            {
                again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            }

            await alerts.CheckAsync(Ct);
            var report = await admin.GetAsync<AlertReport>($"{Alerts}?days=1");
            var closed = report.Incidents.Single(i => i.Code == code);
            closed.ClearedAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(acknowledged.AcknowledgedAt.ShouldNotBeNull());
            closed.Messages.Count.ShouldBe(paged.Messages.Count, "nobody is paged once it is acknowledged");
            report.ReachedSecondContact.ShouldBeGreaterThanOrEqualTo(1);
            report.ReachedSupport.ShouldBeGreaterThanOrEqualTo(1);
            report.Acknowledged.ShouldBeGreaterThanOrEqualTo(1);
            report.MedianMinutesToAcknowledge.ShouldNotBeNull();
        }
        finally
        {
            await AcknowledgeOpenDrillsAsync(admin);
        }
    }

    [Fact]
    public async Task An_alert_acknowledged_in_time_pages_nobody_else_and_a_restart_does_not_tell_it_again()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await using var onCall = await SettingAsync("alerts.oncallPhone", "+919800000149");
        await using var second = await SettingAsync(AlertEscalation.SecondContactSetting, "+919800000150");

        // SMS that goes (to the simulator): one that fails would page the second contact at once, as it should.
        await using var sms = await SettingAsync("integrations.sms", new { enabled = true, provider = "simulator" });
        var drill = await admin.PostAsync<AlertDrill>(Drills);
        try
        {
            var code = AlertDrills.WarningPrefix + drill.Code;
            using (var before = ActivatorUtilities.CreateInstance<SystemHealthAlerts>(server.Services))
            {
                await before.CheckAsync(Ct);
            }

            var told = await IncidentAsync(admin, code);
            told.ToldAt.ShouldNotBeNull();

            // A restart: alerts that remember nothing in memory tell it no more, for it is kept in the database.
            using var alerts = ActivatorUtilities.CreateInstance<SystemHealthAlerts>(server.Services);
            await alerts.CheckAsync(Ct);
            var after = await IncidentAsync(admin, code);
            (after.Id, after.ToldAt, after.Messages.Count).ShouldBe((told.Id, told.ToldAt, told.Messages.Count));

            // Acknowledged before the wait is up: even with no wait left, nobody else is paged, and the drill is acknowledged.
            await hospitalAdmin.PostAsync<AlertIncidentDto>($"{Alerts}/{told.Id}/acknowledge");
            await using var escalateAt = await SettingAsync(AlertEscalation.EscalateMinutesSetting, 0);
            await alerts.CheckAsync(Ct);
            var acknowledged = await IncidentAsync(admin, code);
            (acknowledged.Level, acknowledged.SecondContactAt).ShouldBe((AlertLevel.OnCall, null));
            acknowledged.Messages.ShouldNotContain(m => m.Level != AlertLevel.OnCall);
            (await admin.GetAsync<List<AlertDrill>>(Drills)).Single(d => d.Id == drill.Id).State.ShouldBe("acknowledged");
        }
        finally
        {
            await AcknowledgeOpenDrillsAsync(admin);
        }
    }

    [Fact]
    public async Task An_alert_that_reached_nobody_at_IT_on_call_goes_to_the_second_contact_without_waiting()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        await using var onCall = await SettingAsync("alerts.oncallPhone", "");
        await using var second = await SettingAsync(AlertEscalation.SecondContactSetting, "+919800000151");
        var drill = await admin.PostAsync<AlertDrill>(Drills);
        try
        {
            using var alerts = ActivatorUtilities.CreateInstance<SystemHealthAlerts>(server.Services);
            await alerts.CheckAsync(Ct);
            await alerts.CheckAsync(Ct);

            // No on-call number: told in the app only, so the next check pages the second contact, well within the quarter hour.
            var paged = await IncidentAsync(admin, AlertDrills.WarningPrefix + drill.Code);
            paged.Messages.ShouldNotContain(m => m.Level == AlertLevel.OnCall);
            paged.Level.ShouldBe(AlertLevel.SecondContact);
            paged.SecondContactAt.ShouldNotBeNull().ShouldBeLessThan(paged.ToldAt.ShouldNotBeNull() + AlertEscalation.DefaultEscalateAfter);
            paged.Messages.ShouldHaveSingleItem().Recipient.ShouldBe(Masked("+919800000151"));
        }
        finally
        {
            await AcknowledgeOpenDrillsAsync(admin);
        }
    }

    [Fact]
    public async Task A_warning_that_clears_and_comes_back_is_a_new_alert_told_again()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        await using var onCall = await SettingAsync("alerts.oncallPhone", "+919800000152");
        var recurring = new HealthWarning("test.recurring:" + Guid.NewGuid().ToString("N")[..8], "critical", "A test warning that clears and comes back.");

        await CheckWithAsync(recurring);
        var first = await IncidentAsync(admin, recurring.Code);
        first.ToldAt.ShouldNotBeNull();
        first.Messages.ShouldNotBeEmpty();

        await CheckWithAsync();
        (await IncidentAsync(admin, recurring.Code)).ClearedAt.ShouldNotBeNull();
        using (var cleared = await admin.SendRawAsync(HttpMethod.Post, $"{Alerts}/{first.Id}/acknowledge"))
        {
            cleared.StatusCode.ShouldBe(HttpStatusCode.Conflict, "it cleared before anyone acknowledged it");
        }

        // Back again, however soon: a new alert, told at once, as a broken audit chain that comes back is a second break.
        await CheckWithAsync(recurring);
        var back = await IncidentAsync(admin, recurring.Code);
        back.Id.ShouldNotBe(first.Id);
        (back.ToldAt, back.ClearedAt).ShouldBe((back.RaisedAt, null));
        back.Messages.ShouldNotBeEmpty();
        (await admin.GetAsync<AlertReport>($"{Alerts}?days=1")).Incidents.Count(i => i.Code == recurring.Code).ShouldBe(2);

        await CheckWithAsync();
    }

    [Fact]
    public async Task A_warning_whose_code_is_longer_than_its_column_stays_one_alert_told_once()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        await using var onCall = await SettingAsync("alerts.oncallPhone", "+919800000153");
        var lengthy = new HealthWarning($"test.lengthy:{Guid.NewGuid().ToString("N")[..8]}:" + new string('x', AlertIncident.CodeLength), "critical",
            "A test warning whose code is longer than the alerts keep.");
        var kept = lengthy.Code[..AlertIncident.CodeLength];

        await CheckWithAsync(lengthy);
        var first = await IncidentAsync(admin, kept);
        first.Messages.ShouldNotBeEmpty();

        // The next check finds the same warning: the same alert, still open, and nobody told it again.
        await CheckWithAsync(lengthy);
        var next = await IncidentAsync(admin, kept);
        next.Id.ShouldBe(first.Id);
        next.ClearedAt.ShouldBeNull();
        next.Messages.Count.ShouldBe(first.Messages.Count);
        (await admin.GetAsync<AlertReport>($"{Alerts}?days=1")).Incidents.Count(i => i.Code == kept).ShouldBe(1);

        await CheckWithAsync();
    }

    /// <summary>
    /// A server of several that stopped reporting reports again (SCAL-01): IT on-call is told it is over, in the app too, with
    /// when it was not reporting and when it was again. The alert of its silence, still waiting in the outbox after a failed
    /// try, is withdrawn, so the return never reaches the phone before it.
    /// </summary>
    [Fact]
    public async Task A_server_that_reports_again_is_told_back_and_its_alert_not_sent_yet_is_withdrawn()
    {
        await using var onCall = await SettingAsync("alerts.oncallPhone", "+919800000154");
        var name = ServerName();
        var toldAt = Now();
        await CheckWithAsync(toldAt, [], Silent(name));
        var told = await SilentIncidentAsync(name);
        told.ToldAt.ShouldNotBeNull();
        var loss = told.OnCallMessageIds.ShouldHaveSingleItem("by SMS to IT on-call, who has no WhatsApp alerts here");
        await FailedAndWaitingAsync(loss);

        var backAt = toldAt.AddMinutes(12);
        await CheckWithAsync(backAt, [name]);

        var cleared = await SilentIncidentAsync(name);
        (cleared.Id, cleared.ClearedAt).ShouldBe((told.Id, backAt));
        (await StatusAsync(loss)).ShouldBe((NotificationStatus.Suppressed, AlertIncidents.WithdrawnBecauseBack));
        var back = await QueuedAsync(cleared.OnCallMessageIds.Except([loss]).ShouldHaveSingleItem());
        (back.TemplateKey, back.Channel, back.To, back.Values["server"]).ShouldBe(
            (AlertReadiness.AlertTemplate, NotificationChannel.Sms, Phone.Normalize("+919800000154"), name));
        back.Values["message"].ShouldBe(AlertIncidents.BackMessage(name, cleared.ToldAt.ShouldNotBeNull(), backAt));
        back.Values["message"].ShouldStartWith($"{name} is reporting again: ");
        (await InAppAsync(SystemHealthAlerts.BackTitle, name)).ShouldNotBeEmpty("everyone who may see System health saw it critical in the app");

        // Told once: the next check finds its alert cleared, and sends nothing more.
        await CheckWithAsync(backAt.AddMinutes(5), [name]);
        (await SilentIncidentAsync(name)).OnCallMessageIds.Count.ShouldBe(2);
    }

    /// <summary>
    /// Nobody acknowledged it, and it reached the second contact and then HyFib support: each of them is told it is over,
    /// as they were paged, HyFib support with the hospital's name.
    /// </summary>
    [Fact]
    public async Task A_server_back_after_its_alert_was_escalated_is_told_back_to_everyone_it_reached()
    {
        await using var onCall = await SettingAsync("alerts.oncallPhone", "+919800000155");
        await using var second = await SettingAsync(AlertEscalation.SecondContactSetting, "+919800000156");
        await using var support = await SettingAsync(AlertEscalation.SupportSetting, "+919800000157");
        await using var escalateAt = await SettingAsync(AlertEscalation.EscalateMinutesSetting, 15);
        await using var supportAt = await SettingAsync(AlertEscalation.SupportMinutesSetting, 30);
        var name = ServerName();
        var toldAt = Now();
        await CheckWithAsync(toldAt, [], Silent(name));
        await CheckWithAsync(toldAt.AddMinutes(15), [], Silent(name));
        await CheckWithAsync(toldAt.AddMinutes(45), [], Silent(name));
        var paged = await SilentIncidentAsync(name);
        paged.Level.ShouldBe(AlertLevel.Support);

        var backAt = toldAt.AddMinutes(50);
        await CheckWithAsync(backAt, [name]);

        var cleared = await SilentIncidentAsync(name);
        var message = AlertIncidents.BackMessage(name, cleared.ToldAt.ShouldNotBeNull(), backAt);
        var toOnCall = await QueuedAsync(cleared.OnCallMessageIds.Except(paged.OnCallMessageIds).ShouldHaveSingleItem());
        var toSecond = await QueuedAsync(cleared.SecondContactMessageIds.Except(paged.SecondContactMessageIds).ShouldHaveSingleItem());
        var toSupport = await QueuedAsync(cleared.SupportMessageIds.Except(paged.SupportMessageIds).ShouldHaveSingleItem());
        (toOnCall.To, toOnCall.Values["server"], toOnCall.Values["message"]).ShouldBe((Phone.Normalize("+919800000155"), name, message));
        (toSecond.To, toSecond.Values["server"], toSecond.Values["message"]).ShouldBe((Phone.Normalize("+919800000156"), name, message));
        (toSupport.To, toSupport.Values["message"]).ShouldBe((Phone.Normalize("+919800000157"), message));
        toSupport.Values["server"].ShouldEndWith($", {name}", customMessage: "HyFib support is told whose hospital it is, as when it was paged");
    }

    /// <summary>
    /// Back, then silent again before the alert of its return could go (the SMS gateway out of reach): the return is
    /// withdrawn, so the phone never ends on "reporting again" while the server is not; the new alert is told at once.
    /// </summary>
    [Fact]
    public async Task A_server_silent_again_before_its_return_went_withdraws_the_return()
    {
        await using var onCall = await SettingAsync("alerts.oncallPhone", "+919800000158");
        var name = ServerName();
        var toldAt = Now();
        await CheckWithAsync(toldAt, [], Silent(name));
        await CheckWithAsync(toldAt.AddMinutes(10), [name]);
        var first = await SilentIncidentAsync(name);
        first.OnCallMessageIds.Count.ShouldBe(2, "the alert of its silence, then of its return");
        var back = first.OnCallMessageIds[^1];
        await FailedAndWaitingAsync(back);

        await CheckWithAsync(toldAt.AddMinutes(15), [], Silent(name));

        var again = await SilentIncidentAsync(name);
        again.Id.ShouldNotBe(first.Id);
        (again.ToldAt, again.ClearedAt).ShouldBe((again.RaisedAt, null));
        again.OnCallMessageIds.ShouldHaveSingleItem();
        (await StatusAsync(back)).ShouldBe((NotificationStatus.Suppressed, AlertIncidents.WithdrawnBecauseSilentAgain));

        await CheckWithAsync(toldAt.AddMinutes(20), null);
    }

    /// <summary>
    /// A server stopped on purpose removes itself from the servers that report, and one silent for a day is dropped from
    /// them: its alert clears all the same, but it is not back, and nobody is told it is. The alert of its silence stands.
    /// </summary>
    [Fact]
    public async Task A_server_gone_rather_than_back_is_not_told_back()
    {
        await using var onCall = await SettingAsync("alerts.oncallPhone", "+919800000159");
        var name = ServerName();
        var toldAt = Now();
        await CheckWithAsync(toldAt, [], Silent(name));
        var loss = (await SilentIncidentAsync(name)).OnCallMessageIds.ShouldHaveSingleItem();
        await FailedAndWaitingAsync(loss);

        await CheckWithAsync(toldAt.AddMinutes(10), ["HC360-API2"]);

        var cleared = await SilentIncidentAsync(name);
        cleared.ClearedAt.ShouldNotBeNull();
        cleared.OnCallMessageIds.ShouldBe([loss], "no return is queued");
        (await StatusAsync(loss)).Status.ShouldBe(NotificationStatus.Failed, "the alert of its silence is not withdrawn: it is still true");
        (await InAppAsync(SystemHealthAlerts.BackTitle, name)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_report_counts_every_alert_of_the_period_though_it_lists_only_the_latest_that_cleared()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var code = "test.report:" + Guid.NewGuid().ToString("N")[..8];
        var raised = DateTimeOffset.UtcNow.AddDays(-200);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.AddRange(Enumerable.Range(0, AlertIncidents.Listed + 1).Select(n => new AlertIncident
            {
                Code = code,
                Server = $"report-{n}",
                Message = "A test alert of the report.",
                RaisedAt = raised.AddMinutes(n),
                ToldAt = raised.AddMinutes(n),
                AcknowledgedAt = raised.AddMinutes(n + 7),
                ClearedAt = raised.AddMinutes(n + 60),
            }));
            await db.SaveChangesAsync(Ct);
        }

        // These alone are more than a year's report lists: the oldest is left out of the list, but not of the figures.
        var report = await admin.GetAsync<AlertReport>($"{Alerts}?days=366");
        var open = report.Incidents.Count(i => i.ClearedAt is null);
        report.Incidents.Count(i => i.ClearedAt is not null).ShouldBe(AlertIncidents.Listed, "the latest that cleared are listed");
        report.Incidents.ShouldNotContain(i => i.Code == code && i.Server == "report-0");
        report.Total.ShouldBeGreaterThanOrEqualTo(AlertIncidents.Listed + 1 + open, "every alert of the period is counted, listed or not");
        report.Acknowledged.ShouldBeGreaterThanOrEqualTo(AlertIncidents.Listed + 1);
        report.MedianMinutesToAcknowledge.ShouldBe(7, "over every alert acknowledged, most of them these");
    }

    [Fact]
    public async Task The_heartbeat_fetches_its_outside_address_and_system_health_says_when_it_fails()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var heartbeat = server.Services.GetRequiredService<AlertHeartbeat>();
        var token = Guid.NewGuid().ToString("N");
        await using var monitor = Monitor.Start();

        // Set as an administrator sets it, without asking for a secret, and approved by a second (where alerts go waits for
        // one). The address holds the monitor's token: it is kept encrypted, never shown again, and left out of the audit trail.
        await server.SaveSettingAsync(admin, AlertHeartbeat.UrlSetting, new SaveSettingRequest(JsonValue.Create($"{monitor.Address}ping/{token}"), null, false, null));
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
        training.ShouldNotContain(w => w.Code == AlertEscalation.NotSetCode);

        using (server.AsLiveServer())
        {
            var live = (await admin.GetAsync<SystemHealthReport>(Health)).Warnings;
            var notReady = live.Single(w => w.Code == AlertReadiness.Code);
            notReady.Severity.ShouldBe("critical", "MSG91 refuses every alert SMS whose template has no DLT template id");
            notReady.Message.ShouldContain("MSG91 requires");
            live.Single(w => w.Code == AlertHeartbeat.OffCode).Severity.ShouldBe("warning", "nothing outside the hospital notices if the server stops");
            live.Single(w => w.Code == AlertEscalation.NotSetCode).Severity.ShouldBe("warning", "an alert nobody acknowledges would stop at IT on-call");
            await using (await SettingAsync(AlertEscalation.SupportSetting, "+919800000154"))
            {
                (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.ShouldNotContain(w => w.Code == AlertEscalation.NotSetCode);
            }

            await using (await AlertTemplateDltIdAsync("1107161234567890123"))
            {
                (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.ShouldNotContain(w => w.Code == AlertReadiness.Code);
            }
        }
    }

    /// <summary>The alert of <paramref name="code"/> raised last, as the alerts' report has it.</summary>
    private static async Task<AlertIncidentDto> IncidentAsync(ApiClient admin, string code) =>
        (await admin.GetAsync<AlertReport>($"{Alerts}?days=1")).Incidents.Where(i => i.Code == code).MaxBy(i => i.RaisedAt).ShouldNotBeNull();

    /// <summary>One check of the alerts with System health's critical warnings now and <paramref name="extra"/>.</summary>
    private Task CheckWithAsync(params HealthWarning[] extra) => CheckWithAsync(DateTimeOffset.UtcNow, null, extra);

    /// <summary>
    /// One check of the alerts at <paramref name="now"/>, with System health's critical warnings and <paramref name="extra"/>,
    /// as a server of several that runs them finds <paramref name="reporting"/> reporting.
    /// </summary>
    private async Task CheckWithAsync(DateTimeOffset now, IReadOnlyCollection<string>? reporting, params HealthWarning[] extra)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var critical = (await scope.ServiceProvider.GetRequiredService<SystemHealthService>().BuildAsync(Ct)).Warnings
            .Where(w => w.Severity == "critical" && !w.Code.StartsWith("wifi.", StringComparison.Ordinal));
        await scope.ServiceProvider.GetRequiredService<AlertIncidents>().CheckAsync([.. critical, .. extra], now, reporting, Ct);
    }

    /// <summary>Now to the second, which the database keeps as it is: it keeps microseconds, not .NET's ticks.</summary>
    private static DateTimeOffset Now()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }

    /// <summary>A server name of this test alone, as a machine is named.</summary>
    private static string ServerName() => "TEST-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    /// <summary>System health's warning that <paramref name="name"/>, a server that takes requests, has stopped reporting.</summary>
    private static HealthWarning Silent(string name) => new(SystemHealthService.NotReportingCode, "critical",
        $"{name} has not reported for more than three minutes: it may be off, its HC360Server service stopped, its network cable out, "
        + "or the database out of its reach. The reverse proxy sends requests to HC360-API2 meanwhile.",
        name);

    /// <summary>The alert raised last for <paramref name="name"/> not reporting, as the database keeps it.</summary>
    private async Task<AlertIncident> SilentIncidentAsync(string name)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AlertIncident>().AsNoTracking()
            .Where(i => i.Code == SystemHealthService.NotReportingCode && i.Server == name)
            .OrderByDescending(i => i.RaisedAt)
            .FirstAsync(Ct);
    }

    /// <summary>What a message waiting in the outbox says, and to whom: its payload is JSON, found by its log's id.</summary>
    private async Task<NotificationDelivery> QueuedAsync(Guid logId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>()
            .FromSqlInterpolated($"SELECT * FROM platform.outbox_messages WHERE payload->>'logId' = {logId.ToString()}")
            .AsNoTracking()
            .SingleAsync(Ct)).Read<NotificationDelivery>();
    }

    /// <summary>
    /// A message as when its first try failed (the SMS gateway out of reach) and the outbox waits to try it again: not sent,
    /// and not tried again during the test. The outbox holds a message's row while it tries it, so whatever it was doing
    /// with this one is done first.
    /// </summary>
    private async Task FailedAndWaitingAsync(Guid logId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE platform.outbox_messages SET status = 'Pending', next_attempt_at = {DateTimeOffset.UtcNow.AddHours(1)} WHERE payload->>'logId' = {logId.ToString()}",
            Ct);
        await db.Set<NotificationLog>().Where(l => l.Id == logId).ExecuteUpdateAsync(s => s
            .SetProperty(l => l.Status, NotificationStatus.Failed)
            .SetProperty(l => l.Error, "The SMS gateway could not be reached."), Ct);
    }

    private async Task<(NotificationStatus Status, string? Error)> StatusAsync(Guid logId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var log = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NotificationLog>().AsNoTracking()
            .SingleAsync(l => l.Id == logId, Ct);
        return (log.Status, log.Error);
    }

    /// <summary>The in-app notifications titled <paramref name="title"/> about <paramref name="name"/>.</summary>
    private async Task<List<UserNotification>> InAppAsync(string title, string name)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserNotification>().AsNoTracking()
            .Where(n => n.Title == title && n.Body!.Contains(name))
            .ToListAsync(Ct);
    }

    private async Task<int> EscalationsAsync(Guid incidentId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var entityId = incidentId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>()
            .CountAsync(e => e.Action == AlertIncidents.EscalatedAction && e.EntityId == entityId, Ct);
    }

    /// <summary>The server is shared by every test of the run: no drill is left open.</summary>
    private static async Task AcknowledgeOpenDrillsAsync(ApiClient admin)
    {
        foreach (var open in (await admin.GetAsync<List<AlertDrill>>(Drills)).Where(d => d.State == "open"))
        {
            using var _ = await admin.SendRawAsync(HttpMethod.Post, $"{Drills}/{open.Id}/acknowledge");
        }
    }

    private static string Masked(string phone) => Phone.Mask(Phone.Normalize(phone));

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
