using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Privacy.Tests;

/// <summary>
/// The erasure ledger travels with the backups (DPDP Act 2023 s8(7)): the ledger is the file every backup carries, and on
/// the machine a backup is restored onto, the lines the restore put back are the ledger's own, applied as HC360 starts.
/// </summary>
public sealed class ErasureLedgerTests : IDisposable
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
    public void The_ledger_is_the_file_every_backup_carries_and_its_lines_put_back_on_new_hardware_are_read_there()
    {
        var oldOptions = new PlatformOptions { DataDirectory = Path.Combine(_root, "old") };
        oldOptions.Backup.SecondaryDirectory = Path.Combine(_root, "nas");
        var oldServer = Machine(oldOptions);
        var key = new AuditSealKey(oldServer, new EphemeralDataProtectionProvider());
        var ledger = Ledger(oldServer, oldOptions, key);
        (ledger.Path, ledger.MirrorPath).ShouldBe((ErasureLedgerFiles.PathIn(oldServer), ErasureLedgerFiles.MirrorPath(oldOptions.Backup)),
            "a backup takes the ledger, and its copy, from where the ledger writes them");
        var erased = Entry("NHDPR2600007");
        ledger.Append(erased);
        var carried = ErasureLedgerFiles.ReadForBackup(oldServer, oldOptions.Backup);

        // New hardware, with the backup's audit seal key installed by the restore, and nothing of the old disk.
        var newOptions = new PlatformOptions { DataDirectory = Path.Combine(_root, "new") };
        var newServer = Machine(newOptions);
        var restored = Ledger(newServer, newOptions, key);
        restored.Read().Entries.ShouldBeEmpty();
        ErasureLedgerFiles.Merge(ErasureLedgerFiles.PathIn(newServer), carried).ShouldBe(1);

        var (entries, rejected) = restored.Read();
        entries.ShouldHaveSingleItem().Id.ShouldBe(erased.Id);
        rejected.ShouldBe(0, "its seal is the backup's audit seal key's");
    }

    [Fact]
    public void A_line_put_back_that_this_servers_audit_seal_key_did_not_seal_is_never_read_as_an_erasure()
    {
        var options = new PlatformOptions { DataDirectory = Path.Combine(_root, "new") };
        var server = Machine(options);
        var ledger = Ledger(server, options, new AuditSealKey(server, new EphemeralDataProtectionProvider()));
        var elsewhere = Machine(new PlatformOptions { DataDirectory = Path.Combine(_root, "elsewhere") });
        var forged = Ledger(elsewhere, options, new AuditSealKey(elsewhere, new EphemeralDataProtectionProvider())).Seal(Entry("FORGED"));

        ErasureLedgerFiles.Merge(ledger.Path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(forged, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n"))
            .ShouldBe(1);

        var (entries, rejected) = ledger.Read();
        entries.ShouldBeEmpty();
        rejected.ShouldBe(1);
    }

    private static ErasureLedgerEntry Entry(string requestNumber) =>
        new(Guid.NewGuid(), Guid.NewGuid(), requestNumber, Guid.NewGuid(), [ErasureService.NomineeItem], DateTimeOffset.UtcNow);

    private static DataDirectories Machine(PlatformOptions options)
    {
        var directories = new DataDirectories(options);
        directories.EnsureCreated();
        return directories;
    }

    private static ErasureLedger Ledger(DataDirectories directories, PlatformOptions options, AuditSealKey key) =>
        new(directories, Options.Create(options), key, NullLogger<ErasureLedger>.Instance);
}
