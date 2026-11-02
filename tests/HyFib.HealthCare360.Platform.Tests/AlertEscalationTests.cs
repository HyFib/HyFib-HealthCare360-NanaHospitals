using System.Text.Json;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Who an alert nobody acknowledges is paged to, and when (audit P2): IT on-call at once, the second contact after the
/// wait or as soon as nothing reached IT on-call, HyFib support after its wait; nobody once someone acknowledges it; and
/// the alert told again a day later while it lasts. Each step follows from what the incident records alone.
/// </summary>
public sealed class AlertEscalationTests
{
    private static readonly DateTimeOffset Told = new(2026, 11, 2, 2, 0, 0, TimeSpan.Zero);

    private static readonly AlertEscalation Both = new("+919800000144", "+919800000145", TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30));

    [Fact]
    public void A_new_alert_is_told_at_once_and_one_that_cleared_is_left_alone()
    {
        Both.Next(new AlertIncident { RaisedAt = Told }, Told, levelUndelivered: false).ShouldBe(AlertStep.Tell);

        var cleared = Incident();
        cleared.ClearedAt = Told.AddMinutes(1);
        Both.Next(cleared, Told.AddHours(3), levelUndelivered: true).ShouldBe(AlertStep.None);
    }

    [Fact]
    public void Nobody_acknowledging_it_reaches_the_second_contact_after_the_wait_and_HyFib_support_after_its_own()
    {
        var incident = Incident();
        Both.Next(incident, Told.AddMinutes(14), levelUndelivered: false).ShouldBe(AlertStep.None, "IT on-call has a quarter of an hour");
        Both.Next(incident, Told.AddMinutes(15), levelUndelivered: false).ShouldBe(AlertStep.EscalateToSecondContact);

        incident.Level = AlertLevel.SecondContact;
        incident.SecondContactAt = Told.AddMinutes(15);
        Both.Next(incident, Told.AddMinutes(44), levelUndelivered: false).ShouldBe(AlertStep.None, "the second contact has half an hour of their own");
        Both.Next(incident, Told.AddMinutes(45), levelUndelivered: false).ShouldBe(AlertStep.EscalateToSupport);

        incident.Level = AlertLevel.Support;
        incident.SupportAt = Told.AddMinutes(45);
        Both.Next(incident, Told.AddHours(23), levelUndelivered: true).ShouldBe(AlertStep.None, "nobody is after HyFib support");
    }

    [Fact]
    public void Nothing_that_reached_a_level_escalates_at_once()
    {
        var incident = Incident();
        Both.Next(incident, Told.AddMinutes(5), levelUndelivered: true).ShouldBe(AlertStep.EscalateToSecondContact,
            "no on-call number, or every message to it failed: waiting helps nobody");

        incident.Level = AlertLevel.SecondContact;
        incident.SecondContactAt = Told.AddMinutes(5);
        Both.Next(incident, Told.AddMinutes(10), levelUndelivered: true).ShouldBe(AlertStep.EscalateToSupport);
    }

    [Fact]
    public void A_level_nobody_is_set_for_is_skipped()
    {
        var supportOnly = Both with { SecondContactPhone = null };
        supportOnly.Next(Incident(), Told.AddMinutes(15), levelUndelivered: false).ShouldBe(AlertStep.EscalateToSupport);

        var secondOnly = Both with { SupportPhone = " " };
        var incident = Incident();
        incident.Level = AlertLevel.SecondContact;
        incident.SecondContactAt = Told.AddMinutes(15);
        secondOnly.Next(incident, Told.AddHours(2), levelUndelivered: true).ShouldBe(AlertStep.None);

        var nobody = new AlertEscalation(null, null, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30));
        nobody.Next(Incident(), Told.AddHours(2), levelUndelivered: true).ShouldBe(AlertStep.None);
        nobody.Next(Incident(), Told.AddDays(1), levelUndelivered: true).ShouldBe(AlertStep.Remind, "it is still told again while it lasts");
    }

    [Fact]
    public void Once_acknowledged_nobody_further_is_paged_but_it_is_still_told_again_a_day_later()
    {
        var incident = Incident();
        incident.AcknowledgedAt = Told.AddMinutes(3);
        Both.Next(incident, Told.AddMinutes(16), levelUndelivered: true).ShouldBe(AlertStep.None);
        Both.Next(incident, Told.AddHours(23).AddMinutes(59), levelUndelivered: false).ShouldBe(AlertStep.None);
        Both.Next(incident, Told.AddDays(1), levelUndelivered: false).ShouldBe(AlertStep.Remind);

        incident.RemindedAt = Told.AddDays(1);
        Both.Next(incident, Told.AddDays(1).AddHours(1), levelUndelivered: false).ShouldBe(AlertStep.None, "a day from the last time it was told");
        Both.Next(incident, Told.AddDays(2), levelUndelivered: false).ShouldBe(AlertStep.Remind);
    }

    [Fact]
    public async Task The_contacts_and_waits_are_read_from_the_settings_as_entered()
    {
        var entered = await AlertEscalation.ReadAsync(new JsonSettings(new Dictionary<string, string?>
        {
            [AlertEscalation.SecondContactSetting] = "9800000144",
            [AlertEscalation.SupportSetting] = "\" +91 98000 00145 \"",
            [AlertEscalation.EscalateMinutesSetting] = "0",
            [AlertEscalation.SupportMinutesSetting] = "\"45\"",
        }), TestContext.Current.CancellationToken);
        entered.ShouldBe(new AlertEscalation("9800000144", "+91 98000 00145", TimeSpan.Zero, TimeSpan.FromMinutes(45)),
            "a number typed without quotes is the phone number all the same; 0 escalates at the next check");

        var unset = await AlertEscalation.ReadAsync(new JsonSettings(new Dictionary<string, string?>
        {
            [AlertEscalation.SecondContactSetting] = "\"  \"",
            [AlertEscalation.EscalateMinutesSetting] = "-5",
            [AlertEscalation.SupportMinutesSetting] = "\"two days\"",
        }), TestContext.Current.CancellationToken);
        unset.ShouldBe(new AlertEscalation(null, null, AlertEscalation.DefaultEscalateAfter, AlertEscalation.DefaultSupportAfter));

        var tooLong = await AlertEscalation.ReadAsync(new JsonSettings(new Dictionary<string, string?>
        {
            [AlertEscalation.EscalateMinutesSetting] = "1441",
        }), TestContext.Current.CancellationToken);
        tooLong.EscalateAfter.ShouldBe(AlertEscalation.DefaultEscalateAfter, "no longer than a day, when the alert is told again anyway");
    }

    [Fact]
    public async Task A_live_server_warns_when_an_unacknowledged_alert_could_go_no_further_than_IT_on_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var live = new Hosting.PlatformOptions { SeedDemoData = false };
        var nobody = new JsonSettings(new Dictionary<string, string?>());
        var warning = (await AlertEscalation.WarningAsync(nobody, live, ct)).ShouldNotBeNull();
        (warning.Code, warning.Severity).ShouldBe((AlertEscalation.NotSetCode, "warning"));
        warning.Message.ShouldContain("alerts.escalationPhone");

        (await AlertEscalation.WarningAsync(new JsonSettings(new Dictionary<string, string?>
        {
            [AlertEscalation.SupportSetting] = "\"+919800000145\"",
        }), live, ct)).ShouldBeNull("HyFib support alone is somewhere to go");
        (await AlertEscalation.WarningAsync(nobody, new Hosting.PlatformOptions { SeedDemoData = true }, ct)).ShouldBeNull("a training server");
    }

    [Fact]
    public void A_message_that_failed_or_was_never_sent_has_reached_nobody()
    {
        AlertIncidents.Reaches(NotificationStatus.Queued).ShouldBeTrue("it is still on its way");
        AlertIncidents.Reaches(NotificationStatus.Sent).ShouldBeTrue();
        AlertIncidents.Reaches(NotificationStatus.Delivered).ShouldBeTrue();
        AlertIncidents.Reaches(NotificationStatus.Failed).ShouldBeFalse("a critical alert does not wait on the retries");
        AlertIncidents.Reaches(NotificationStatus.Skipped).ShouldBeFalse();
        AlertIncidents.Reaches(NotificationStatus.Suppressed).ShouldBeFalse("the on-call number opted out of WhatsApp");
    }

    [Fact]
    public void Time_to_acknowledge_is_reported_by_nearest_rank()
    {
        AlertIncidents.Percentile([], 0.5).ShouldBeNull();
        AlertIncidents.Percentile([7], 0.9).ShouldBe(7);
        double[] minutes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 40];
        AlertIncidents.Percentile(minutes, 0.5).ShouldBe(5);
        AlertIncidents.Percentile(minutes, 0.9).ShouldBe(9);
        AlertIncidents.Percentile(minutes, 1).ShouldBe(40);
    }

    private static AlertIncident Incident() => new() { Code = "backup.stale", RaisedAt = Told, ToldAt = Told };

    /// <summary>Settings as the server keeps them: each a JSON value, read into the type asked for.</summary>
    private sealed class JsonSettings(IReadOnlyDictionary<string, string?> values) : ISettingsService
    {
        public Task<T?> GetAsync<T>(string key, Guid? facilityId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(values.GetValueOrDefault(key) is { } json ? JsonSerializer.Deserialize<T>(json) : default);

        public Task SetAsync<T>(string key, T value, Guid? facilityId = null, bool secret = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetAsync<T>(string key, T value, Guid? facilityId, bool secret, uint? expectedVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RemoveAsync(string key, Guid? facilityId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
