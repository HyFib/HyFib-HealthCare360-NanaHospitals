using System.Text.Json;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Settings;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The hold on outgoing messages after a restore, as the restore writes it and System health shows it, and the messages
/// that end unsent, which System health counts whether they failed or expired before any try.
/// </summary>
public sealed class OutboxHoldTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_hold_is_the_object_the_restore_writes_in_camel_case()
    {
        // As the restore writes it and the settings service stores and reads values (web defaults).
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string written = """{"reason":"restore","since":"2026-11-14T04:30:00Z","backupTakenAt":"2026-11-13T20:30:00Z"}""";

        var hold = JsonSerializer.Deserialize<OutboxHold>(written, web).ShouldNotBeNull();

        hold.ShouldBe(new OutboxHold(OutboxHold.RestoreReason, new DateTimeOffset(2026, 11, 14, 4, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 13, 20, 30, 0, TimeSpan.Zero)));
        hold.SetByRestore().ShouldBeTrue();
        JsonSerializer.Serialize(hold, web).ShouldBe(written.Replace(":00Z", ":00+00:00", StringComparison.Ordinal));
        OutboxHold.SettingKey.ShouldBe("messaging.outbox.hold");
    }

    [Fact]
    public async Task A_hold_that_cannot_be_read_holds_the_messages_all_the_same()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<OutboxHold>(OutboxHold.SettingKey, null, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<OutboxHold?>(new JsonException("The JSON value could not be converted to OutboxHold.")));

        var hold = (await OutboxHolds.ReadAsync(settings, Ct)).ShouldNotBeNull();

        hold.Since.ShouldBe(default);
        hold.SetByRestore().ShouldBeFalse();

        // It holds every message on the internet lane, and is critical: nothing else tells of it.
        var warning = SystemHealthOutbox.Warnings(new OutboxInfo(5, 0, null, Hold: hold, HeldMessages: 5), Now).Single(w => w.Code == SystemHealthOutbox.HeldCode);
        warning.Severity.ShouldBe("critical");
        warning.Message.ShouldBe("Outgoing messages are held, and the hold cannot be read: none goes to the internet. Check them, then release");
    }

    [Fact]
    public async Task No_hold_set_holds_nothing()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<OutboxHold>(OutboxHold.SettingKey, null, Arg.Any<CancellationToken>()).Returns(Task.FromResult<OutboxHold?>(null));

        (await OutboxHolds.ReadAsync(settings, Ct)).ShouldBeNull();
    }

    [Fact]
    public void After_a_restore_the_held_messages_are_counted_in_a_warning_that_pages_nobody_a_second_time()
    {
        // The restore's own critical warning (system.restored) tells IT on-call; this one says how many are held.
        var hold = new OutboxHold(OutboxHold.RestoreReason, new DateTimeOffset(2026, 11, 14, 4, 30, 0, TimeSpan.Zero), Now.AddHours(-10));

        var warnings = SystemHealthOutbox.Warnings(new OutboxInfo(14, 0, Now.AddHours(-5), Hold: hold, HeldMessages: 12), Now);

        var held = warnings.Single(w => w.Code == SystemHealthOutbox.HeldCode);
        held.Severity.ShouldBe("warning");
        held.Message.ShouldBe(
            "12 outgoing messages queued before the restore at 14 Nov 2026 10:00 are held: check them, then release. Messages queued since go as usual");

        // The backlog is put down to the hold, not to the internet.
        warnings.Single(w => w.Code == "outbox.backlog").Message
            .ShouldBe("Outbound messages are queued for more than 30 minutes: those queued before the hold began are held.");
    }

    [Fact]
    public void A_hold_set_for_another_reason_is_critical_and_says_why()
    {
        var hold = new OutboxHold("The SMS gateway is being changed", new DateTimeOffset(2026, 11, 14, 4, 30, 0, TimeSpan.Zero), null);

        var held = SystemHealthOutbox.Warnings(new OutboxInfo(1, 0, Now.AddMinutes(-5), Hold: hold, HeldMessages: 1), Now).ShouldHaveSingleItem();

        held.Severity.ShouldBe("critical");
        held.Message.ShouldBe(
            "1 outgoing message queued before 14 Nov 2026 10:00 is held (The SMS gateway is being changed): check it, then release. Messages queued since go as usual");
    }

    [Fact]
    public void A_hold_with_nothing_left_to_hold_asks_to_be_released()
    {
        var hold = new OutboxHold(OutboxHold.RestoreReason, new DateTimeOffset(2026, 11, 14, 4, 30, 0, TimeSpan.Zero), null);

        SystemHealthOutbox.Warnings(new OutboxInfo(0, 0, null, Hold: hold), Now).ShouldHaveSingleItem().Message
            .ShouldBe("No outgoing message queued before the restore at 14 Nov 2026 10:00 is held any more: release the hold to end it");
    }

    [Fact]
    public void Messages_that_expired_unsent_are_counted_with_those_that_failed()
    {
        SystemHealthOutbox.Warnings(new OutboxInfo(0, 0, null, ExpiredLast24Hours: 4), Now).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            w => w.Code.ShouldBe("outbox.failed"),
            w => w.Severity.ShouldBe("warning"),
            w => w.Message.ShouldBe("4 outbound message(s) expired unsent in the last 24 hours: their time was up before they could be tried."));

        SystemHealthOutbox.Warnings(new OutboxInfo(0, 2, null, ExpiredLast24Hours: 4), Now).ShouldHaveSingleItem().Message
            .ShouldBe("2 outbound message(s) failed and 4 expired unsent in the last 24 hours.");
        SystemHealthOutbox.Warnings(new OutboxInfo(0, 2, null), Now).ShouldHaveSingleItem().Message
            .ShouldBe("2 outbound message(s) failed in the last 24 hours.");
    }

    [Fact]
    public void Nothing_waiting_long_nothing_unsent_and_no_hold_is_no_warning() =>
        SystemHealthOutbox.Warnings(new OutboxInfo(3, 0, Now.AddMinutes(-10)), Now).ShouldBeEmpty();
}
