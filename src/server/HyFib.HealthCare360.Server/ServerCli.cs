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

          backup restore --in <backup.hc360bak> --database-out <database.dump> [--data-dir <folder>] [--key <recovery-key>]
              Restores a backup on this Server Node, with the HC360 service stopped (Restore-HC360.ps1 runs this). It installs
              the backup's field encryption key (a different key already here is kept under a new name), puts the stored
              documents back, and writes the database dump for pg_restore. Nothing changes if the backup is damaged.

          backup decrypt --in <backup.hc360bak> --out <database.dump> [--key <recovery-key>]
              Writes only the database dump (all that backups made before archive backups hold).

        The recovery key is shown once in Administration > Backups > Recovery key; keep it offline. Without --key it is
        read from the HC360_RECOVERY_KEY environment variable, from standard input when it is redirected, or from a hidden
        prompt, so it never appears in the process list.
        """;

    public static bool IsCommand(string argument) => argument is "backup" or "help" or "--help" or "-h";

    public static async Task<int> RunAsync(string[] args)
    {
        var options = args.Length > 2 ? ParseOptions(args[2..]) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return args switch
            {
                ["backup", "verify", ..] when options.ContainsKey("in") => await VerifyAsync(options),
                ["backup", "restore", ..] when options.ContainsKey("in") && options.ContainsKey("database-out") => await RestoreAsync(options),
                ["backup", "decrypt", ..] when options.ContainsKey("in") && options.ContainsKey("out") => await DecryptAsync(options),
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

        // A backup from a newer HC360 has a schema this version does not know: install that version first.
        await using (var input = File.OpenRead(options["in"]))
        await using (var payload = await BackupEncryption.OpenAsync(input, key, CancellationToken.None))
        {
            if (payload.Version == BackupEncryption.ArchiveVersion && WarnIfNewer(await BackupArchive.ReadManifestAsync(payload, CancellationToken.None)))
            {
                return 3;
            }
        }

        var result = await BackupRestore.RestoreAsync(options["in"], key, options["database-out"], directories,
            HospitalDataProtection.CreateProvider(directories), TimeProvider.System, CancellationToken.None);
        if (result.Manifest is null)
        {
            Console.WriteLine($"Decrypted the database to {options["database-out"]}. This older backup holds the database only:");
            Console.WriteLine($"copy the old server's keys folder to {directories.Keys} as well, or encrypted identifiers stay unreadable.");
            return 0;
        }

        Describe(result.Manifest);
        Console.WriteLine($"Installed the backup's field encryption key in {directories.Keys}.");
        if (result.PreviousKeyKeptAs is { } keptAs)
        {
            Console.WriteLine($"The different key that was here is kept as {keptAs}.");
        }

        Console.WriteLine($"Documents: {result.DocumentsRestored} restored, {result.DocumentsAlreadyPresent} already present.");
        Console.WriteLine($"Database written to {options["database-out"]}. Restore it with pg_restore.");
        return 0;
    }

    private static async Task<int> DecryptAsync(Dictionary<string, string> options)
    {
        var key = ReadRecoveryKey(options);
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
            CryptographicOperations.ZeroMemory(contents.MasterKey);
            Console.WriteLine("Only the database was written. Use 'backup restore' to restore the encryption key and documents too.");
        }

        Console.WriteLine($"Decrypted to {options["out"]}. Restore it with pg_restore.");
        return 0;
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
