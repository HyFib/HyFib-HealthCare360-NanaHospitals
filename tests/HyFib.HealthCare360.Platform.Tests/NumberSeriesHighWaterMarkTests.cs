using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The number series' high-water mark on disk, and what a restore leaves for the next start: the mark is written when a
/// counter changed and only then, a copy on a disk that is away does not stop it, the newest copy sealed with this
/// installation's key is read, and Restore-HC360.ps1's marker is read and renamed once acted on. System health then names
/// the backup, the series raised and the held messages.
/// </summary>
public sealed class NumberSeriesHighWaterMarkTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 4, 30, 0, TimeSpan.Zero);
    private static readonly Guid Database = Guid.Parse("0193a0c1-0000-7000-8000-0000000000aa");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-high-water-" + Guid.NewGuid().ToString("N"));
    private readonly AuditSealKey _key;

    public NumberSeriesHighWaterMarkTests()
    {
        var directories = new DataDirectories(Options(Path.Combine(_root, "data"), null));
        directories.EnsureCreated();
        _key = new AuditSealKey(directories, new EphemeralDataProtectionProvider());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_mark_is_written_when_a_counter_changed_and_only_then()
    {
        var mark = Mark();

        mark.Record(Database, [Uhid(10)], T0).ShouldBeTrue();
        mark.Record(Database, [Uhid(10)], T0.AddMinutes(1)).ShouldBeFalse("nothing was issued in the minute");
        Mark().ReadNewest(Database).ShouldNotBeNull().WrittenAt.ShouldBe(T0);

        mark.Record(Database, [Uhid(11)], T0.AddMinutes(2)).ShouldBeTrue();
        var read = Mark().ReadNewest(Database).ShouldNotBeNull("a server started later reads what this one wrote");
        read.Series.ShouldBe([Uhid(11)]);
        read.WrittenAt.ShouldBe(T0.AddMinutes(2));
        read.Database.ShouldBe(Database);
        File.Exists(mark.Path + ".writing").ShouldBeFalse("each write replaces the file whole");
    }

    [Fact]
    public void A_copy_that_cannot_be_written_does_not_stop_the_mark_and_is_brought_level_once_it_can()
    {
        // The backup disk is away: a file stands where its folder should be.
        var secondary = Path.Combine(_root, "usb");
        File.WriteAllText(secondary, "not a folder");
        var logger = new Recorded<NumberSeriesHighWaterMark>();
        var mark = Mark(secondary, logger);

        Should.NotThrow(() => mark.Record(Database, [Uhid(10)], T0)).ShouldBeTrue("the mark in the data directory is written");
        File.Exists(mark.Path).ShouldBeTrue();
        logger.Warnings.ShouldHaveSingleItem().ShouldContain(mark.MirrorPath!);
        mark.Record(Database, [Uhid(10)], T0.AddMinutes(1)).ShouldBeFalse();
        logger.Warnings.Count.ShouldBe(1, "told once, not every minute");

        // The disk is back: the copy is written although nothing changed.
        File.Delete(secondary);
        Directory.CreateDirectory(secondary);
        mark.Record(Database, [Uhid(10)], T0.AddMinutes(2)).ShouldBeFalse("the mark itself did not change");
        File.Delete(mark.Path);
        Mark(secondary).ReadNewest(Database).ShouldNotBeNull("the copy is what outlives the server's own disk").Series.ShouldBe([Uhid(10)]);
    }

    [Fact]
    public void The_newest_copy_is_read_and_one_not_sealed_with_this_installation_s_key_is_not()
    {
        // The file here was last written at T0; the copy a minute later, when the file here could not be (its disk was full).
        var secondary = Path.Combine(_root, "nas");
        Mark().Record(Database, [Uhid(10)], T0).ShouldBeTrue();
        var copy = Mark(secondary, dataDirectory: Path.Combine(_root, "elsewhere"));
        copy.Record(Database, [Uhid(20)], T0.AddMinutes(1));
        Mark(secondary).ReadNewest(Database).ShouldNotBeNull().Series.ShouldBe([Uhid(20)], "the copy is the newer of the two");

        // Someone who can write the backup share but not read the key changes the copy: it is not used.
        var text = File.ReadAllText(copy.MirrorPath!);
        text.ShouldContain("\"value\": 20");
        File.WriteAllText(copy.MirrorPath!, text.Replace("\"value\": 20", "\"value\": 900000", StringComparison.Ordinal));
        Mark(secondary).ReadNewest(Database).ShouldNotBeNull().Series.ShouldBe([Uhid(10)], "the copy's seal no longer verifies");

        // A training server sharing the backup share writes its own mark there, with a key of its own.
        var training = new DataDirectories(Options(Path.Combine(_root, "training"), null));
        training.EnsureCreated();
        new NumberSeriesHighWaterMark(training, Microsoft.Extensions.Options.Options.Create(Options(Path.Combine(_root, "training"), secondary)),
                new AuditSealKey(training, new EphemeralDataProtectionProvider()), NullLogger<NumberSeriesHighWaterMark>.Instance)
            .Record(Database, [Uhid(777)], T0.AddMinutes(9)).ShouldBeTrue();
        Mark(secondary).ReadNewest(Database).ShouldNotBeNull().Series.ShouldBe([Uhid(10)], "its seal is not this installation's");
    }

    [Fact]
    public void A_mark_taken_from_another_database_is_not_used_and_is_replaced()
    {
        var mark = Mark();
        mark.Record(Database, [Uhid(300)], T0);
        var live = Guid.Parse("0193a0c1-0000-7000-8000-0000000000bb");

        // The hospital went live on a database of its own, with the data directory it trained with.
        Mark().ReadNewest(live).ShouldBeNull("the training database's numbers are not the live one's");
        var fresh = Mark();
        fresh.Record(live, [Uhid(1)], T0.AddMinutes(1)).ShouldBeTrue();
        fresh.ReadNewest(live).ShouldNotBeNull().Series.ShouldBe([Uhid(1)], "the training mark's series are not carried over");
    }

    [Fact]
    public void A_database_restored_while_the_server_runs_does_not_lower_the_mark()
    {
        var mark = Mark();
        mark.Record(Database, [Uhid(1300)], T0);

        mark.Record(Database, [Uhid(1200)], T0.AddMinutes(1)).ShouldBeFalse();
        Mark().ReadNewest(Database).ShouldNotBeNull().Series.ShouldBe([Uhid(1300)], "start-up raises the series past it when it next starts");
    }

    [Fact]
    public void The_marker_Restore_HC360_writes_is_read_and_renamed_once_acted_on()
    {
        var directories = new DataDirectories(Options(Path.Combine(_root, "data"), null));
        var marker = new RestoreMarker(directories, new FixedTime(T0), NullLogger<RestoreMarker>.Instance);
        marker.Read().ShouldBeNull("no restore");

        // As Windows PowerShell 5.1's ConvertTo-Json writes it.
        File.WriteAllText(marker.Path, """
            {
                "restoredAt":  "2026-11-14T09:10:00Z",
                "backupFile":  "D:\\HC360-Backups\\hc360-20261113-200000-scheduled.hc360bak",
                "backupTakenAt":  "2026-11-13T20:00:00Z"
            }
            """);
        marker.Read().ShouldBe(new RestoreMarkerContent(
            new DateTimeOffset(2026, 11, 14, 9, 10, 0, TimeSpan.Zero), @"D:\HC360-Backups\hc360-20261113-200000-scheduled.hc360bak",
            new DateTimeOffset(2026, 11, 13, 20, 0, 0, TimeSpan.Zero)));

        marker.Consume();
        marker.Read().ShouldBeNull("it is acted on once");
        File.Exists(Path.Combine(directories.Root, "restored-20261114-043000.json")).ShouldBeTrue("kept beside it, renamed");
    }

    [Fact]
    public void A_marker_that_does_not_read_is_a_restore_all_the_same()
    {
        var directories = new DataDirectories(Options(Path.Combine(_root, "data"), null));
        var marker = new RestoreMarker(directories, new FixedTime(T0), NullLogger<RestoreMarker>.Instance);
        File.WriteAllText(marker.Path, "{ \"backupFile\": null, \"backupTakenAt\": null }");
        File.SetLastWriteTimeUtc(marker.Path, new DateTime(2026, 11, 14, 9, 10, 0, DateTimeKind.Utc));

        var read = marker.Read().ShouldNotBeNull();
        read.RestoredAt.ShouldBe(new DateTimeOffset(2026, 11, 14, 9, 10, 0, TimeSpan.Zero), "when the file was written");
        read.BackupTakenAt.ShouldBeNull();

        File.WriteAllText(marker.Path, "restored");
        marker.Read().ShouldNotBeNull().BackupFile.ShouldBeNull();
    }

    [Fact]
    public void System_health_names_the_backup_the_series_raised_and_the_held_messages()
    {
        var restore = new ServerRestore(
            new DateTimeOffset(2026, 11, 14, 9, 10, 0, TimeSpan.Zero),
            @"D:\HC360-Backups\hc360-20261113-200000-scheduled.hc360bak",
            new DateTimeOffset(2026, 11, 13, 20, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 14, 9, 12, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 14, 8, 59, 0, TimeSpan.Zero),
            [
                new RaisedNumberSeries("uhid", "UHID (patient number)", null, "NH26001335", "NH26001201"),
                new RaisedNumberSeries("billing.bill.op", "OP bill", "NH", "NHOB26000581", null),
            ]);

        var warning = RestoreHealthWarnings.Warning(restore, restore.StartedAt, restore.BackupTakenAt, @"C:\ProgramData\x\numbering\mark.json", null);

        warning.Code.ShouldBe("system.restored");
        warning.Severity.ShouldBe("critical");
        warning.Server.ShouldBeNull("it is the hospital's, not one machine's");
        warning.Message.ShouldContain("restored from the backup of 2026-11-13 20:00 UTC (hc360-20261113-200000-scheduled.hc360bak) on 2026-11-14 09:10 UTC");
        warning.Message.ShouldContain("moved 2 number series 100 past the last numbers issued before it (high-water mark of 2026-11-14 08:59 UTC)");
        warning.Message.ShouldContain("UHID (patient number), next NH26001335 instead of NH26001201");
        warning.Message.ShouldContain("OP bill at NH, next NHOB26000581");
        warning.Message.ShouldContain("Outgoing messages are held since 2026-11-14 09:12 UTC");
        warning.Message.ShouldContain("release them");
    }

    [Fact]
    public void Without_a_high_water_mark_System_health_says_where_it_looked_and_what_to_check()
    {
        var restore = new ServerRestore(T0, null, null, T0, null, []);
        var message = RestoreHealthWarnings.Warning(restore, T0, null, @"C:\data\numbering\mark.json", @"E:\HC360-Backups\mark.json").Message;

        message.ShouldStartWith("HC360 was restored from a backup on 2026-11-14 04:30 UTC.");
        message.ShouldContain(@"No high-water mark of the number series was found (C:\data\numbering\mark.json or E:\HC360-Backups\mark.json)");
        message.ShouldContain("Hospital setup › Number formats");

        var many = restore with { MarkWrittenAt = T0, Raised = [.. Enumerable.Range(1, 11).Select(i => new RaisedNumberSeries($"s{i}", $"Series {i}", null, $"{i}01", null))] };
        var listed = RestoreHealthWarnings.Warning(many, T0, null, "mark.json", null).Message;
        listed.ShouldContain("Series 8, next 801; and 3 more, each in the audit trail (numbering.raised)");
        listed.ShouldNotContain("Series 9,");
        RestoreHealthWarnings.Warning(restore with { MarkWrittenAt = T0 }, T0, null, "mark.json", null).Message
            .ShouldContain("Every number series was already past the last numbers issued before it");
    }

    [Fact]
    public void The_hold_is_the_outbox_s_setting_as_start_up_writes_it()
    {
        // As the settings service stores the value start-up gives it (camelCase JSON).
        var since = new DateTimeOffset(2026, 11, 14, 9, 12, 0, TimeSpan.Zero);
        var takenAt = new DateTimeOffset(2026, 11, 13, 20, 0, 0, TimeSpan.Zero);
        JsonElement Stored(object value) => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        RestoreHealthWarnings.RestoreHold(Stored(new { reason = NumberSeriesStartupCheck.RestoreHoldReason, since, backupTakenAt = (DateTimeOffset?)takenAt }))
            .ShouldBe(((DateTimeOffset?)since, (DateTimeOffset?)takenAt));
        RestoreHealthWarnings.RestoreHold(Stored(new { reason = "restore", since, backupTakenAt = (DateTimeOffset?)null }))
            .ShouldBe(((DateTimeOffset?)since, (DateTimeOffset?)null));
        RestoreHealthWarnings.RestoreHold(Stored(new { reason = "maintenance", since })).ShouldBeNull("held for another reason, which says so itself");
        RestoreHealthWarnings.RestoreHold(null).ShouldBeNull("released");
        NumberSeriesStartupCheck.OutboxHoldSetting.ShouldBe("messaging.outbox.hold");
    }

    [Fact]
    public async Task A_record_of_the_restore_changed_by_hand_into_something_else_is_read_as_none()
    {
        // Administration › Settings lists it: one changed there must neither stop start-up nor System health.
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<ServerRestore>(NumberSeriesStartupCheck.RestoreSetting, null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new JsonException("The JSON value could not be converted to ServerRestore."));

        (await NumberSeriesStartupCheck.RecordedAsync(settings, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    private NumberSeriesHighWaterMark Mark(string? secondary = null, ILogger<NumberSeriesHighWaterMark>? logger = null, string? dataDirectory = null)
    {
        var options = Options(dataDirectory ?? Path.Combine(_root, "data"), secondary);
        return new NumberSeriesHighWaterMark(new DataDirectories(options), Microsoft.Extensions.Options.Options.Create(options), _key,
            logger ?? NullLogger<NumberSeriesHighWaterMark>.Instance);
    }

    private static PlatformOptions Options(string dataDirectory, string? secondary) =>
        new() { DataDirectory = dataDirectory, Backup = new BackupOptions { SecondaryDirectory = secondary } };

    private static NumberSeriesMark Uhid(long value) => new(null, "uhid", null, value, NumberResetPolicy.Never, "{FAC}{YY}{SEQ:6}");

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The warnings logged, as they read.</summary>
    private sealed class Recorded<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
