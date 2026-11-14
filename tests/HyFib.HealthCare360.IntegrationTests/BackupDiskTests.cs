using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Two USB disks swapped at the same folder (DED-16), over real backups on the run's database: each disk is given an id
/// on its first copy, each backup names the disk it went to in the Backups list, and the swap is in the audit trail.
/// </summary>
public sealed class BackupDiskTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Swapping_the_backup_disk_is_recorded_and_each_backup_names_its_disk()
    {
        var settings = server.Services.GetRequiredService<IOptions<PlatformOptions>>().Value.Backup;
        var secondary = settings.SecondaryDirectory;
        var root = Path.Combine(Path.GetTempPath(), "hc360-tests", "it-disks-" + Guid.NewGuid().ToString("N"));
        var diskA = Path.Combine(root, "disk-a", "HC360-Backups");
        var diskB = Path.Combine(root, "disk-b", "HC360-Backups");
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var since = DateTimeOffset.UtcNow;
        try
        {
            settings.SecondaryDirectory = diskA;
            var first = await BackupAsync();
            settings.SecondaryDirectory = diskB;
            var second = await BackupAsync();

            var listed = await admin.GetAsync<List<BackupRunDto>>("/api/v1/admin/backups");
            var onA = listed.Single(r => r.Id == first.Id).SecondaryCopies.ShouldNotBeNull().ShouldHaveSingleItem().Disk.ShouldNotBeNull();
            var onB = listed.Single(r => r.Id == second.Id).SecondaryCopies.ShouldNotBeNull().ShouldHaveSingleItem().Disk.ShouldNotBeNull();
            onA.ShouldNotBe(onB, "two disks, told apart though at the same folder");
            (await File.ReadAllTextAsync(Path.Combine(diskA, BackupDisk.MarkerFileName), Ct)).ShouldContain(onA);

            // The first backup may be a swap too, from the disk an earlier test of the run copied to.
            await using var scope = server.Services.CreateAsyncScope();
            var swaps = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
                .Where(e => e.Action == BackupDisk.SwappedAction && e.OccurredAt >= since)
                .Select(e => e.Summary)
                .ToListAsync(Ct);
            swaps.ShouldContain($"Backup disk {onB} in place of {onA}: {second.FileName} was copied to it");
            swaps.ShouldNotContain(s => s != null && s.StartsWith($"Backup disk {onA} in place of {onB}", StringComparison.Ordinal));
        }
        finally
        {
            // The server is shared by every test of the run: as it was.
            settings.SecondaryDirectory = secondary;
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A backup as Administration › Backups › Back up now makes it.</summary>
    private async Task<BackupRunDto> BackupAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<BackupService>().RunAsync(BackupKind.Manual, null, Ct);
        run.Status.ShouldBe(BackupStatus.Succeeded, run.Error);
        run.SecondaryCopied.ShouldBeTrue(run.Error);
        return run;
    }
}
