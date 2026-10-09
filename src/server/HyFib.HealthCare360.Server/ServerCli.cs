using System.Reflection;
using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HyFib.HealthCare360.Server;

/// <summary>Offline maintenance commands for administrators on the Server Node.</summary>
internal static class ServerCli
{
    private const string Usage = """
        HyFib HealthCare360 server maintenance

          backup verify --in <backup.hc360bak> [--key <recovery-key>]
              Reads a backup in full and checks it: every part decrypts, and the database, the encryption key and the
              documents match its manifest. Use it for the quarterly restore test and for copies on a NAS or USB drive.

          backup decrypt --in <backup.hc360bak> --out <database.dump> [--key <recovery-key>]
              Checks the whole backup and writes only its database dump, for pg_restore. Nothing else changes.

          backup restore --in <backup.hc360bak> [--data-dir <folder>] [--database-out <database.dump>] [--key <recovery-key>]
              Puts back the stored documents and then the backup's field encryption keys (the original key and any added by
              rotations) on this Server Node, with the HC360 service stopped. A different key already here is kept under a
              new name. The whole backup is checked first,
              so nothing changes if it is damaged. Run it only after the backup's database is restored: from then on this
              machine uses the backup's key. Restore-HC360.ps1 runs both steps in that order.

        The recovery key is shown in Administration > Backups > Recovery key (each view is audited); keep it offline. It
        does not change when the data encryption key is rotated. Without --key it is
        read from the HC360_RECOVERY_KEY environment variable, from standard input when it is redirected, or from a hidden
        prompt, so it never appears in the process list.

          settings protect [--data-dir <folder>]
              Protects the secrets in config\hc360.settings.json with Windows DPAPI for this machine: every connection string
              (they hold the database password) and every setting whose name ends in Password, Secret or ApiKey. The server
              reads them as before, and a copy of the file is no use on another machine. To change one, write its new plain
              value in the file and run this again. Install-HC360Server.ps1 runs it on every install and upgrade.

          disk-encryption check [--data-dir <folder>]
              Records whether BitLocker protects each fixed disk of this machine, for System health. It needs administrator
              rights; the "HC360 disk encryption check" scheduled task runs it as SYSTEM every day and at start-up. Exit code
              0 when every disk is protected, 3 when one is not.

          catalogue import --file <file> [--dry-run]
              Imports the hospital's WHO ICD-10 ClaML file (icd102019en.xml, or the icd102019en.xml.zip WHO provides) or the
              CMS ICD-10-PCS order file (icd10pcs_order_YYYY.txt, or its zip) into the local code catalogue, as Medical
              records > ICD catalogue > Import does. --dry-run reads the file and says what would change, and changes nothing.
              Run it on the Server Node from an elevated prompt while the database is running.
        """;

    public static bool IsCommand(string argument) =>
        argument is "backup" or "catalogue" or "settings" or "disk-encryption" or "help" or "--help" or "-h";

    public static async Task<int> RunAsync(string[] args)
    {
        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        args = [.. args.Where(a => !string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase))];
        var options = args.Length > 2 ? ParseOptions(args[2..]) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return args switch
            {
                ["backup", "verify", ..] when options.ContainsKey("in") => await VerifyAsync(options),
                ["backup", "restore", ..] when options.ContainsKey("in") => await RestoreAsync(options),
                ["backup", "decrypt", ..] when options.ContainsKey("in") && options.ContainsKey("out") => await DecryptAsync(options),
                ["catalogue", "import", ..] when options.ContainsKey("file") => await CatalogueCli.ImportAsync(options["file"], dryRun),
                ["settings", "protect", ..] => ProtectSettings(options),
                ["disk-encryption", "check", ..] => CheckDiskEncryption(options),
                _ => PrintUsage(args),
            };
        }
        catch (AuthenticationTagMismatchException)
        {
            Console.Error.WriteLine("The recovery key does not open this backup, or the file is damaged.");
            return 1;
        }
        catch (CryptographicException ex)
        {
            Console.Error.WriteLine($"The backup could not be decrypted: {ex.Message}");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"The backup is not usable: {ex.Message}");
            return 1;
        }
        catch (RecoveryKeyException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int PrintUsage(string[] args)
    {
        Console.WriteLine(Usage);
        return args.Length == 0 || args[0] is "help" or "--help" or "-h" ? 0 : 2;
    }

    private static async Task<int> VerifyAsync(Dictionary<string, string> options)
    {
        var key = ReadRecoveryKey(options);
        await using var input = File.OpenRead(options["in"]);
        await using var payload = await BackupEncryption.OpenAsync(input, key, CancellationToken.None);
        if (payload.Version == BackupEncryption.LegacyVersion)
        {
            await payload.CopyToAsync(Stream.Null);
            Console.WriteLine("The backup decrypts in full. It is an older backup that holds the database only: restoring it on new");
            Console.WriteLine("hardware also needs the old server's keys folder.");
            return 0;
        }

        var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(), CancellationToken.None);
        CryptographicOperations.ZeroMemory(contents.MasterKey);
        Describe(contents.Manifest);
        Console.WriteLine("The backup decrypts in full and matches its manifest.");
        return WarnIfNewer(contents.Manifest) ? 3 : 0;
    }

    private static async Task<int> RestoreAsync(Dictionary<string, string> options)
    {
        var key = ReadRecoveryKey(options);
        var platform = new PlatformOptions { DataDirectory = options.GetValueOrDefault("data-dir") };
        var directories = new DataDirectories(platform);
        directories.EnsureCreated();

        if (await IsFromNewerVersionAsync(options["in"], key))
        {
            return 3;
        }

        var databaseOut = options.GetValueOrDefault("database-out");
        var result = await BackupRestore.RestoreAsync(options["in"], key, databaseOut, directories,
            HospitalDataProtection.CreateProvider(directories), TimeProvider.System, CancellationToken.None);
        if (result.Manifest is null)
        {
            Console.WriteLine(databaseOut is null
                ? "This older backup holds the database only: there was nothing else to put back."
                : $"Decrypted the database to {databaseOut}. This older backup holds the database only:");
            Console.WriteLine($"copy the old server's keys folder to {directories.Keys} as well, or encrypted identifiers stay unreadable.");
            return 0;
        }

        Describe(result.Manifest);
        Console.WriteLine($"Documents: {result.DocumentsRestored} restored, {result.DocumentsAlreadyPresent} already present.");
        Console.WriteLine(result.RingKeysInstalled == 0
            ? $"Installed the backup's field encryption key in {directories.Keys}."
            : $"Installed the backup's field encryption key and the {result.RingKeysInstalled} key(s) its rotations added in {directories.Keys}.");
        if (result.PreviousKeyKeptAs is { } keptAs)
        {
            Console.WriteLine($"The different key that was here is kept as {keptAs}.");
        }

        if (databaseOut is not null)
        {
            Console.WriteLine($"Database written to {databaseOut}. Restore it with pg_restore.");
        }

        return 0;
    }

    /// <summary>Protects the settings file's plain-text secrets with DPAPI for this machine (SRS SEC-07).</summary>
    private static int ProtectSettings(Dictionary<string, string> options)
    {
        var path = ProtectedSettings.PathIn(new DataDirectories(new PlatformOptions { DataDirectory = options.GetValueOrDefault("data-dir") }));
        if (ProtectedSettings.ForThisMachine() is not { } protector)
        {
            Console.Error.WriteLine("Settings can be protected only on Windows, with DPAPI.");
            return 1;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"There is no settings file at {path}.");
            return 1;
        }

        IReadOnlyList<string> keys;
        try
        {
            keys = ProtectedSettings.ProtectFile(path, protector);
        }
        catch (Exception ex) when (ex is CryptographicException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The settings file could not be protected: {ex.Message}");
            return 2;
        }

        Console.WriteLine(keys.Count == 0
            ? "Every secret in the settings file is already protected."
            : $"Protected with DPAPI for this machine: {string.Join(", ", keys)}.");
        return 0;
    }

    /// <summary>Records BitLocker's state of every fixed disk for System health (SRS SEC-05). Needs administrator rights.</summary>
    private static int CheckDiskEncryption(Dictionary<string, string> options)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("BitLocker exists only on Windows.");
            return 1;
        }

        DiskEncryptionReport report;
        try
        {
            report = DiskEncryption.Check(DateTimeOffset.UtcNow);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Nothing is recorded, so System health keeps the last check that worked.
            Console.Error.WriteLine($"{ex.Message} Run it from an elevated prompt.");
            return 2;
        }
        catch (System.Management.ManagementException ex)
        {
            Console.Error.WriteLine($"BitLocker's state could not be read: {ex.Message}");
            return 2;
        }

        var directories = new DataDirectories(new PlatformOptions { DataDirectory = options.GetValueOrDefault("data-dir") });
        DiskEncryption.Save(DiskEncryption.PathIn(directories), report);
        foreach (var line in DiskEncryption.Describe(report))
        {
            Console.WriteLine(line);
        }

        return DiskEncryption.AllProtected(report) ? 0 : 3;
    }

    /// <summary>
    /// The first step of a restore: checks everything the later steps need (the archive, its manifest, the key, and that
    /// this version knows the backup's database), then writes the database dump. Nothing else changes.
    /// </summary>
    private static async Task<int> DecryptAsync(Dictionary<string, string> options)
    {
        var key = ReadRecoveryKey(options);
        if (await IsFromNewerVersionAsync(options["in"], key))
        {
            return 3;
        }

        await using var input = File.OpenRead(options["in"]);
        await using var payload = await BackupEncryption.OpenAsync(input, key, CancellationToken.None);
        if (payload.Version == BackupEncryption.LegacyVersion)
        {
            await using var output = File.Create(options["out"]);
            await payload.CopyToAsync(output);
        }
        else
        {
            var contents = await BackupArchive.ReadAsync(payload, new BackupReadTargets(DatabaseDumpPath: options["out"]), CancellationToken.None);
            try
            {
                BackupRestore.CheckKey(contents.MasterKey, key);
            }
            catch
            {
                File.Delete(options["out"]);
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(contents.MasterKey);
            }

            Console.WriteLine("Only the database was written. After it is restored, 'backup restore' puts back the encryption key and documents.");
        }

        Console.WriteLine($"Decrypted to {options["out"]}. Restore it with pg_restore.");
        return 0;
    }

    /// <summary>
    /// True (after saying so) when the backup's database has migrations this build does not know: its schema would not match
    /// this version, so that version must be installed first.
    /// </summary>
    private static async Task<bool> IsFromNewerVersionAsync(string backupFile, byte[] key)
    {
        await using var input = File.OpenRead(backupFile);
        await using var payload = await BackupEncryption.OpenAsync(input, key, CancellationToken.None);
        return payload.Version == BackupEncryption.ArchiveVersion && WarnIfNewer(await BackupArchive.ReadManifestAsync(payload, CancellationToken.None));
    }

    private static void Describe(BackupManifest manifest) =>
        Console.WriteLine(
            $"{manifest.Kind} backup of '{manifest.Database}' taken {manifest.CreatedAt:yyyy-MM-dd HH:mm} UTC by HC360 {manifest.ServerVersion}: " +
            (manifest.IncludesDocuments ? $"database, encryption key and {manifest.DocumentCount} documents." : "database and encryption key (no documents)."));

    /// <summary>True (after saying so) when the backup's database has migrations this build does not know.</summary>
    private static bool WarnIfNewer(BackupManifest manifest)
    {
        var known = typeof(ServerCli).Assembly.GetTypes()
            .Select(t => t.GetCustomAttribute<MigrationAttribute>()?.Id)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var unknown = manifest.Migrations.Where(m => !known.Contains(m)).ToList();
        if (unknown.Count == 0)
        {
            return false;
        }

        Console.Error.WriteLine($"This backup was made by a newer version of HC360 (database changes {string.Join(", ", unknown)}).");
        Console.Error.WriteLine($"Install HC360 {manifest.ServerVersion} or later before restoring it.");
        return true;
    }

    private static byte[] ReadRecoveryKey(Dictionary<string, string> options)
    {
        var key = options.GetValueOrDefault("key") ?? Environment.GetEnvironmentVariable("HC360_RECOVERY_KEY") ?? ReadSecret("Recovery key: ");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new RecoveryKeyException("A recovery key is required.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(key.Trim());
        }
        catch (FormatException)
        {
            throw new RecoveryKeyException("The recovery key is not valid base64.");
        }

        return bytes.Length == FieldMasterKey.Length ? bytes : throw new RecoveryKeyException("That is not an HC360 recovery key (it should be 44 characters).");
    }

    private static string? ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine()?.Trim();
        }

        Console.Write(prompt);
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString().Trim();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                result[args[i][2..]] = args[i + 1];
            }
        }

        return result;
    }

    private sealed class RecoveryKeyException(string message) : Exception(message);
}
