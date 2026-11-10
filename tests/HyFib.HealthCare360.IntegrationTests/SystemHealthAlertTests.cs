using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// What the System health alerts (<see cref="SystemHealthAlerts"/>) leave to others, and backups that stay on the server's
/// own disk, which they tell once it lasts. AuditSealTests has more of what they tell: a broken audit chain, once while it
/// lasts and again when it comes back.
/// </summary>
public sealed class SystemHealthAlertTests(HospitalServer server)
{
    private const string Health = "/api/v1/admin/system/health";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The backup is made while the USB disk it is copied to is unplugged (DED-16), as at a hospital that swaps two disks
    /// weekly and forgets to plug the second in. The backup is kept and says it was not copied, and why; the administrators
    /// are told in the app; System health warns, over the API, naming the folder. That is not yet told to IT on-call, but
    /// once no copy has been made for three and a half days it is, as a critical alert; a backup copied again clears it.
    /// Real backups, on the run's database, with the settings a hospital gives with <c>-SecondaryBackupDir</c>.
    /// </summary>
    [Fact]
    public async Task A_backup_not_copied_off_the_server_is_a_warning_and_after_three_nights_an_alert_until_one_is()
    {
        var settings = server.Services.GetRequiredService<IOptions<PlatformOptions>>().Value.Backup;
        var (enabled, secondary) = (settings.Enabled, settings.SecondaryDirectory);
        var root = Path.Combine(Path.GetTempPath(), "hc360-tests", "it-copies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        // A disk that is not there: a path the copy cannot make on Windows or Linux, as a drive letter with no disk behind
        // it is. The run's server takes no nightly backups of its own; this hospital does.
        var unplugged = Path.Combine(root, "unplugged");
        await File.WriteAllTextAsync(unplugged, string.Empty, Ct);
        settings.Enabled = true;
        settings.SecondaryDirectory = Path.Combine(unplugged, "HC360-Backups");
        var lastCopy = new BackupRun
        {
            Kind = BackupKind.Scheduled, Status = BackupStatus.Succeeded, SecondaryCopied = true, FileName = "hc360-copied-scheduled.hc360bak",
        };

        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        using var alerts = ActivatorUtilities.CreateInstance<SystemHealthAlerts>(server.Services);
        try
        {
            var missed = await BackupAsync();
            missed.Status.ShouldBe(BackupStatus.Succeeded, "the backup itself is made");
            missed.SecondaryCopied.ShouldBeFalse();
            missed.Error.ShouldNotBeNull().ShouldContain(BackupService.CopyFailedNote, Case.Sensitive, "the Backups screen says why");
            (await NotificationsAsync(admin, BackupCopies.NotCopiedTitle, settings.SecondaryDirectory)).ShouldHaveSingleItem().Severity.ShouldBe("warning");

            var warning = (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.Single(w => w.Code == BackupCopies.Code);
            warning.Severity.ShouldBe("warning");
            warning.Message.ShouldContain($"was not copied to {settings.SecondaryDirectory}, so it is on the server's own disk only");
            (await alerts.CheckAsync(Ct)).ShouldNotContain(w => w.Code == BackupCopies.Code, "a night without a copy is not yet told to IT on-call");

            // The last copy made was four days before this backup: the disk has been out for three nights.
            lastCopy.StartedAt = missed.StartedAt.AddDays(-4);
            lastCopy.CompletedAt = lastCopy.StartedAt.AddMinutes(10);
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                db.Add(lastCopy);
                await db.SaveChangesAsync(Ct);
            }

            warning = (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.Single(w => w.Code == BackupCopies.Code);
            warning.Severity.ShouldBe("critical");
            warning.Message.ShouldContain($"No backup has been copied to {settings.SecondaryDirectory} since the one of");
            await alerts.CheckAsync(Ct);
            (await NotificationsAsync(admin, SystemHealthAlerts.Title, settings.SecondaryDirectory)).ShouldHaveSingleItem("told in the app, and to IT on-call");
            (await OpenAlertAsync()).ShouldNotBeNull("an alert someone must acknowledge");

            // The disk is plugged in again, and a backup taken: copied, so nothing is left to say.
            settings.SecondaryDirectory = Path.Combine(root, "usb-disk", "HC360-Backups");
            var copied = await BackupAsync();
            copied.SecondaryCopied.ShouldBeTrue(copied.Error);
            File.Exists(Path.Combine(settings.SecondaryDirectory, copied.FileName!)).ShouldBeTrue();
            (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.ShouldNotContain(w => w.Code == BackupCopies.Code);
            await alerts.CheckAsync(Ct);
            (await OpenAlertAsync()).ShouldBeNull("the alert clears with its warning");

            // An update in the day, with the disk out for the swap: the backup taken before it holds no documents, so its
            // missing copy leaves the night's full copy the newest, and says nothing.
            settings.SecondaryDirectory = Path.Combine(unplugged, "HC360-Backups");
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var beforeUpdate = scope.ServiceProvider.GetRequiredService<IPreMigrationBackup>();
                var result = await beforeUpdate.CreatePreMigrationAsync([], Ct);
                result.SecondaryCopied.ShouldBeFalse();
                await beforeUpdate.RecordAsync(result, Ct);
            }

            (await admin.GetAsync<SystemHealthReport>(Health)).Warnings.ShouldNotContain(w => w.Code == BackupCopies.Code);
        }
        finally
        {
            // The server is shared by every test of the run: as it was, with no alert left open.
            (settings.Enabled, settings.SecondaryDirectory) = (enabled, secondary);
            await using (var scope = server.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<BackupRun>().Where(r => r.Id == lastCopy.Id).ExecuteDeleteAsync(Ct);
            }

            await alerts.CheckAsync(Ct);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A backup as Administration › Backups › Back up now makes it.</summary>
    private async Task<BackupRunDto> BackupAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BackupService>().RunAsync(BackupKind.Manual, null, Ct);
    }

    /// <summary>The notifications titled <paramref name="title"/> <paramref name="user"/> has in the app that name <paramref name="folder"/>.</summary>
    private static async Task<IReadOnlyList<UserNotificationDto>> NotificationsAsync(ApiClient user, string title, string folder) =>
        [.. (await user.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=200")).Items
            .Where(n => n.Title == title && n.Body?.Contains(folder, StringComparison.Ordinal) == true)];

    /// <summary>The open alert for a backup not copied, if any.</summary>
    private async Task<AlertIncident?> OpenAlertAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AlertIncident>().AsNoTracking()
            .SingleOrDefaultAsync(i => i.Code == BackupCopies.Code && i.ClearedAt == null, Ct);
    }

    /// <summary>
    /// The WiFi monitor tells a drop, a lost adapter or an insecure network as it happens (DED-06), by its own rules, and
    /// not at all where the hospital turned it off. The System health alerts leave WiFi to it, so that one drop is not told
    /// twice, in the app and to the IT on-call number.
    /// </summary>
    [Fact]
    public async Task A_lost_WiFi_adapter_is_left_to_the_WiFi_monitor_to_tell()
    {
        var wifi = server.Services.GetRequiredService<WifiLinkState>();
        using var alerts = ActivatorUtilities.CreateInstance<SystemHealthAlerts>(server.Services);
        wifi.Update(null, DateTimeOffset.UtcNow, "Server Node lost its WiFi adapter: Windows shows none (disabled or removed).");
        try
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                (await scope.ServiceProvider.GetRequiredService<SystemHealthService>().BuildAsync(Ct)).Warnings
                    .ShouldContain(w => w.Code == "wifi.adapter_missing" && w.Severity == "critical");
            }

            (await alerts.CheckAsync(Ct)).ShouldNotContain(w => w.Code.StartsWith("wifi.", StringComparison.Ordinal));
        }
        finally
        {
            // As the monitor leaves it on a server without WiFi.
            wifi.Update(null, DateTimeOffset.UtcNow);
        }
    }
}
