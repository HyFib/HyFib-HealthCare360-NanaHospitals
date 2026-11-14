using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The erasure ledger's files as backups carry them (DPDP Act 2023 s8(7)): the Privacy module writes the ledger and a copy
/// beside the backups' second copy; a backup takes every line of both, and a restore adds to this server's ledger the
/// lines it lacks, so an erasure is applied again however the server that made it was lost.
/// </summary>
public sealed class ErasureLedgerFilesTests : IDisposable
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
    public void The_ledger_is_beside_the_audit_anchors_and_its_copy_beside_the_backups_second_copy()
    {
        var single = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        ErasureLedgerFiles.PathIn(new DataDirectories(single)).ShouldBe(Path.Combine(_root, "data", "privacy", "erasure-ledger.log"));

        var several = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        several.Cluster.SharedDirectory = Path.Combine(_root, "shared");
        ErasureLedgerFiles.PathIn(new DataDirectories(several)).ShouldBe(Path.Combine(_root, "shared", "privacy", "erasure-ledger.log"), "every server writes the one ledger");

        ErasureLedgerFiles.MirrorPath(new BackupOptions()).ShouldBeNull();
        ErasureLedgerFiles.MirrorPath(new BackupOptions { SecondaryDirectory = Path.Combine(_root, "nas") }).ShouldBe(Path.Combine(_root, "nas", "erasure-ledger.log"));
    }

    [Fact]
    public void A_backup_takes_every_line_of_the_ledger_and_its_copy_once_and_leaves_out_a_line_still_being_written()
    {
        var options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        options.Backup.SecondaryDirectory = Path.Combine(_root, "nas");
        var directories = new DataDirectories(options);
        Write(ErasureLedgerFiles.PathIn(directories), "{\"id\":1}\r\n{\"id\":2}\n{\"id\":3");
        Write(ErasureLedgerFiles.MirrorPath(options.Backup)!, "{\"id\":2}\n{\"id\":0}\n");

        var carried = Encoding.UTF8.GetString(ErasureLedgerFiles.ReadForBackup(directories, options.Backup));

        carried.ShouldBe("{\"id\":1}\n{\"id\":2}\n{\"id\":0}\n", "the copy's line the ledger lost is carried too, and the line being appended is left to the next backup");
    }

    [Fact]
    public void A_server_that_never_erased_anything_carries_an_empty_ledger()
    {
        var options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        options.Backup.SecondaryDirectory = Path.Combine(_root, "unplugged");

        ErasureLedgerFiles.ReadForBackup(new DataDirectories(options), options.Backup).ShouldBeEmpty();
    }

    [Fact]
    public void A_restore_adds_to_the_ledger_only_the_lines_it_lacks()
    {
        var ledger = Path.Combine(_root, "data", "privacy", "erasure-ledger.log");
        Write(ledger, "{\"id\":1}" + Environment.NewLine + "{\"id\":2}" + Environment.NewLine);

        ErasureLedgerFiles.Merge(ledger, Encoding.UTF8.GetBytes("{\"id\":2}\n{\"id\":3}\n\n{\"id\":4}\n")).ShouldBe(2);
        File.ReadAllLines(ledger).ShouldBe(["{\"id\":1}", "{\"id\":2}", "{\"id\":3}", "{\"id\":4}"]);
        ErasureLedgerFiles.Merge(ledger, Encoding.UTF8.GetBytes("{\"id\":1}\n{\"id\":4}\n")).ShouldBe(0, "a second restore of the same backup adds nothing");

        var newHardware = Path.Combine(_root, "new", "privacy", "erasure-ledger.log");
        ErasureLedgerFiles.Merge(newHardware, Encoding.UTF8.GetBytes("{\"id\":1}\n")).ShouldBe(1, "new hardware has no ledger of its own yet");
        File.ReadAllLines(newHardware).ShouldBe(["{\"id\":1}"]);
    }

    [Fact]
    public void A_ledger_written_without_its_last_line_end_gets_one_before_the_lines_a_restore_adds()
    {
        var ledger = Path.Combine(_root, "data", "privacy", "erasure-ledger.log");
        Write(ledger, "{\"id\":1}" + Environment.NewLine + "{\"id\":2");

        ErasureLedgerFiles.Merge(ledger, Encoding.UTF8.GetBytes("{\"id\":3}\n")).ShouldBe(1);

        File.ReadAllLines(ledger).ShouldBe(["{\"id\":1}", "{\"id\":2", "{\"id\":3}"], "the cut line stays on its own, and is reported as unreadable by the replay");
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
