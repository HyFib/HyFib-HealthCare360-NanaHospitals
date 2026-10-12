using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.ServiceProcess;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore.Migrations;

namespace HyFib.HealthCare360.Server;

/// <summary>Offline maintenance commands for administrators on the Server Node.</summary>
internal static class ServerCli
{
    private const string DatabasePasswordVariable = "HC360_DATABASE_PASSWORD";

    /// <summary>The Event Log source the installer registers, and the server writes its errors to.</summary>
    private const string EventSource = "HyFib HealthCare360";

    /// <summary>ERROR_SERVICE_DOES_NOT_EXIST.</summary>
    private const int ErrorServiceDoesNotExist = 1060;

    /// <summary>How long the watchdog waits for PostgreSQL to take connections, crash recovery included.</summary>
    private static readonly TimeSpan WatchdogStartTimeout = TimeSpan.FromMinutes(2);

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
              machine uses the backup's key. Restore-HC360.ps1 runs both steps in that order. A machine without a recovery
              key of its own, such as new hardware, goes on backing up with the key the backup was opened with.

        The recovery key that opens a backup is the one that was current when the backup was made. A new key is shown once,
        when it is made in Administration > Backups > Recovery key, as 14 groups of 4 letters and digits; keep each one
        offline for as long as backups made with it are kept. The Backups list, and this tool when it is given another key,
        name the key each backup needs. Backups made before HC360 named their key open with the original recovery key, 44
        characters long; on the Server Node itself, --key original reads that key from the machine's own data key. Without
        --key the key is read from the HC360_RECOVERY_KEY environment variable, from standard input when it is redirected, or
        from a hidden prompt, so it never appears in the process list.

          settings protect [--data-dir <folder>]
              Protects the secrets in config\hc360.settings.json with Windows DPAPI for this machine: every connection string
              (they hold the database password) and every setting whose name ends in Password, Secret or ApiKey. The server
              reads them as before, and a copy of the file is no use on another machine. To change one, write its new plain
              value in the file and run this again. Install-HC360Server.ps1 runs it on every install and upgrade.

          database sign-in show|windows|password [--data-dir <folder>]
              How the server signs in to PostgreSQL, from config\hc360.settings.json. "show" prints the hospital connection
              without its password, and whether it signs in with one. "windows" takes the password out of every connection
              string that signs in to this machine's PostgreSQL as that account: the server then signs in with its Windows
              account (SSPI), which pg_hba.conf and pg_ident.conf must allow. "password" puts back the password given in the
              HC360_DATABASE_PASSWORD environment variable, which must already be the account's password.
              Install-HC360Server.ps1 runs these, and sets PostgreSQL up to match; run them by hand only with the HC360
              service stopped.

          database watchdog --service <name> --pgdata <folder> [--data-dir <folder>]
              Starts the PostgreSQL service again when it is stopped, though set to start automatically, and its data folder
              (--pgdata) says it was not shut down cleanly: it crashed or was killed. A clean shut-down, which is how a person
              or a script stops PostgreSQL, is left alone, as is a service set to start by hand. Windows does not restart
              PostgreSQL itself, because its service reports a clean stop even when the database crashes. It needs
              administrator rights; the "HC360 database watchdog" scheduled task runs it as SYSTEM every minute, and System
              health says when it had to start PostgreSQL. Exit code 0 when PostgreSQL runs or was left stopped on purpose, 2
              when there is no such service or it cannot be read, 3 when it could not be started.

          disk-encryption check [--data-dir <folder>]
              Records whether BitLocker protects each fixed disk of this machine, for System health. It needs administrator
              rights; the "HC360 disk encryption check" scheduled task runs it as SYSTEM every day and at start-up. Exit code
              0 when every disk is protected, 3 when one is not.

          catalogue import --file <file> [--dry-run]
              Imports the hospital's WHO ICD-10 ClaML file (icd102019en.xml, or the icd102019en.xml.zip WHO provides) or the
              CMS ICD-10-PCS order file (icd10pcs_order_YYYY.txt, or its zip) into the local code catalogue, as Medical
              records > ICD catalogue > Import does. --dry-run reads the file and says what would change, and changes nothing.
              Run it on the Server Node from an elevated prompt while the database is running.

          loadtest patients --count <N>
              Adds N synthetic patients, after any added before, for a load test of patient search over a large register
              (SRS PERF-02: a million patients). Their UHIDs start with LT and nothing is sent to them. Only on a test
              server set up with demonstration data (HealthCare360:SeedDemoData); see docs/testing/LOAD-TESTS.md.

          loadtest accounts --count <N>
              Makes the staff accounts loadtest01 to loadtestNN (Front Office), with the password in the
              HC360_LOADTEST_PASSWORD environment variable, and prints their names for the load test's --accounts. An
              account made before takes the new password. Only on a test server with demonstration data.
        """;

    public static bool IsCommand(string argument) =>
        argument is "backup" or "catalogue" or "settings" or "database" or "disk-encryption" or "loadtest" or "desktop" or "help" or "--help" or "-h";

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
                ["loadtest", "patients", ..] when options.ContainsKey("count") => await LoadTestCli.AddPatientsAsync(options["count"]),
                ["loadtest", "accounts", ..] when options.ContainsKey("count") => await LoadTestCli.AddAccountsAsync(options["count"]),
                ["settings", "protect", ..] => ProtectSettings(options),
                ["database", "sign-in", "show", ..] => ShowDatabaseSignIn(options),
                ["database", "sign-in", "windows", ..] => ChangeDatabaseSignIn(options, windows: true),
                ["database", "sign-in", "password", ..] => ChangeDatabaseSignIn(options, windows: false),
                ["database", "watchdog", ..] when options.ContainsKey("service") && options.ContainsKey("pgdata") => WatchDatabase(options),
                ["disk-encryption", "check", ..] => CheckDiskEncryption(options),
                ["desktop", "publish", ..] when options.ContainsKey("package") => DesktopCli.Publish(options["package"], options.GetValueOrDefault("data-dir")),
                ["desktop", "verify", ..] when options.ContainsKey("package") => DesktopCli.Verify(options["package"]),
                ["desktop", "show", ..] => await DesktopCli.ShowAsync(options.GetValueOrDefault("data-dir")),
                _ => PrintUsage(args),
            };
        }
        catch (BackupKeyMismatchException ex)
        {
            Console.Error.WriteLine($"This backup opens with recovery key {ex.BackupKeyId}, not with the key entered ({ex.GivenKeyId}).");
            Console.Error.WriteLine("Each key's id was shown with it when it was made, and Administration > Backups lists the key of every backup.");
            return 1;
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
        Console.WriteLine();
        Console.WriteLine(DesktopCli.Usage);
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
        Console.WriteLine(payload.KeyId is { } keyId
            ? $"The backup decrypts in full and matches its manifest. It opens with recovery key {keyId}."
            : "The backup decrypts in full and matches its manifest. It opens with the original recovery key.");
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

        if (result.RecoveryKeyKept)
        {
            Console.WriteLine($"Backups made here from now on also open with the recovery key you entered ({BackupRecoveryKey.Id(key)}).");
        }
        else if (result.RecoveryKeyProblem is { } problem)
        {
            Console.WriteLine($"The recovery key you entered could not be kept for new backups ({problem}).");
            Console.WriteLine("Once HC360 runs, make a new one in Administration > Backups > Recovery key.");
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

    /// <summary>
    /// Prints the hospital database connection without its password, as name=value lines Install-HC360Server.ps1 reads
    /// (ConvertFrom-StringData).
    /// </summary>
    private static int ShowDatabaseSignIn(Dictionary<string, string> options)
    {
        if (SettingsFile(options) is not { } path)
        {
            return 1;
        }

        DatabaseSignInState state;
        try
        {
            state = DatabaseSignIn.Read(path, ProtectedSettings.ForThisMachine());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Text.Json.JsonException or IOException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        Console.WriteLine($"host={state.Host}");
        Console.WriteLine($"port={state.Port}");
        Console.WriteLine($"database={state.Database}");
        Console.WriteLine($"username={state.Username}");
        Console.WriteLine($"sign-in={(state.WithPassword ? "password" : "windows")}");
        Console.WriteLine($"on-this-machine={(state.OnThisMachine ? "yes" : "no")}");
        return 0;
    }

    /// <summary>
    /// Switches the server's connection strings to the Windows sign-in, or back to the password in
    /// <see cref="DatabasePasswordVariable"/>, which must already be the account's in PostgreSQL. The file stays
    /// protected with DPAPI.
    /// </summary>
    private static int ChangeDatabaseSignIn(Dictionary<string, string> options, bool windows)
    {
        var password = windows ? null : Environment.GetEnvironmentVariable(DatabasePasswordVariable);
        if (!windows && string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("Give the password in the HC360_DATABASE_PASSWORD environment variable, never on the command line.");
            return 1;
        }

        if (SettingsFile(options) is not { } path)
        {
            return 1;
        }

        if (ProtectedSettings.ForThisMachine() is not { } protector)
        {
            Console.Error.WriteLine("The database sign-in can be changed only on Windows, where the settings file is protected with DPAPI.");
            return 1;
        }

        IReadOnlyList<string> changed;
        try
        {
            changed = windows ? DatabaseSignIn.UseWindows(path, protector) : DatabaseSignIn.UsePassword(path, protector, password!);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or CryptographicException or System.Text.Json.JsonException
                                       or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var how = windows ? "with its Windows account" : "with its password";
        Console.WriteLine(changed.Count == 0
            ? $"The server already signs in to the database {how}."
            : $"The server now signs in to the database {how}: changed {string.Join(", ", changed)}.");
        return 0;
    }

    private static string? SettingsFile(Dictionary<string, string> options)
    {
        var path = ProtectedSettings.PathIn(new DataDirectories(new PlatformOptions { DataDirectory = options.GetValueOrDefault("data-dir") }));
        if (File.Exists(path))
        {
            return path;
        }

        Console.Error.WriteLine($"There is no settings file at {path}.");
        return null;
    }

    /// <summary>
    /// Starts PostgreSQL again when it stopped without being shut down (SRS DED-01, <see cref="DatabaseWatchdog"/>), and
    /// records what it found for System health. Needs administrator rights.
    /// </summary>
    private static int WatchDatabase(Dictionary<string, string> options)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The database watchdog watches a Windows service, so it runs only on Windows.");
            return 1;
        }

        var name = options["service"];
        var path = DatabaseWatchdog.PathIn(new DataDirectories(new PlatformOptions { DataDirectory = options.GetValueOrDefault("data-dir") }));
        var restarts = DatabaseWatchdog.Load(path) is { } last && string.Equals(last.Service, name, StringComparison.OrdinalIgnoreCase)
            ? last.Restarts
            : [];
        var cluster = DatabaseWatchdog.ReadClusterState(options["pgdata"]);
        var runsWithoutService = DatabaseWatchdog.ReadPostmasterId(options["pgdata"]) is { } postmaster && IsPostgres(postmaster);
        var now = DateTimeOffset.UtcNow;

        using var service = new ServiceController(name);
        DatabaseServiceState state;
        bool automatic;
        try
        {
            (state, automatic) = ReadService(service);
        }
        catch (InvalidOperationException ex)
        {
            // Nothing is recorded, so System health says the watchdog has stopped once its record is old.
            Console.Error.WriteLine($"The service {name} could not be read: {ex.InnerException?.Message ?? ex.Message} Run it from an elevated prompt.");
            return 2;
        }

        var decision = DatabaseWatchdog.Decide(name, state, automatic, cluster, runsWithoutService, restarts, now);
        Console.WriteLine(decision.Reason);
        var exitCode = state == DatabaseServiceState.Missing ? 2 : 0;
        if (decision.Start)
        {
            var restart = StartService(service, now);
            restarts = DatabaseWatchdog.WithRestart(restarts, restart);
            state = ReadService(service).State;
            var message = restart.Succeeded
                ? $"PostgreSQL ({name}) had stopped without being shut down, and the HC360 database watchdog started it again. PostgreSQL's log folder says why it stopped."
                : $"PostgreSQL ({name}) had stopped without being shut down, and the HC360 database watchdog could not start it again: {restart.Problem}";
            Console.WriteLine(message);
            WriteEvent(message, restart.Succeeded ? EventLogEntryType.Warning : EventLogEntryType.Error);
            exitCode = restart.Succeeded ? 0 : 3;
        }

        DatabaseWatchdog.Save(path, new DatabaseWatchdogReport(now, name, state, cluster, restarts));
        return exitCode;
    }

    /// <summary>The service's state, and whether it starts automatically (delayed or not).</summary>
    /// <exception cref="InvalidOperationException">The service exists but cannot be read, as without administrator rights.</exception>
    [SupportedOSPlatform("windows")]
    private static (DatabaseServiceState State, bool Automatic) ReadService(ServiceController service)
    {
        try
        {
            service.Refresh();
            var state = service.Status switch
            {
                ServiceControllerStatus.Running => DatabaseServiceState.Running,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => DatabaseServiceState.Starting,
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => DatabaseServiceState.Stopping,
                ServiceControllerStatus.Paused => DatabaseServiceState.Paused,
                _ => DatabaseServiceState.Stopped,
            };
            return (state, service.StartType == ServiceStartMode.Automatic);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: ErrorServiceDoesNotExist })
        {
            return (DatabaseServiceState.Missing, false);
        }
    }

    /// <summary>Starts the service and waits until it runs, which pg_ctl reports once PostgreSQL takes connections.</summary>
    [SupportedOSPlatform("windows")]
    private static DatabaseRestart StartService(ServiceController service, DateTimeOffset at)
    {
        try
        {
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, WatchdogStartTimeout);
            return new(at, Succeeded: true);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            service.Refresh();
            return new(at, false, service.Status == ServiceControllerStatus.Stopped
                ? "it stopped again as it started."
                : $"it was still starting {WatchdogStartTimeout.TotalMinutes:0} minutes later.");
        }
        catch (InvalidOperationException ex)
        {
            return new(at, false, (ex.InnerException?.Message ?? ex.Message).Trim());
        }
    }

    /// <summary>Whether process <paramref name="id"/> is a PostgreSQL server process; a crash leaves an old id behind.</summary>
    private static bool IsPostgres(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return string.Equals(process.ProcessName, "postgres", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteEvent(string message, EventLogEntryType type)
    {
        try
        {
            EventLog.WriteEntry(EventSource, message, type);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Win32Exception or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"The Application event log could not be written to: {ex.Message}");
        }
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
                // A version 3 backup names its key, which every chunk authenticates; a version 2 one used the original key.
                if (payload.Version == BackupEncryption.ArchiveVersion)
                {
                    BackupRestore.CheckKey(contents.MasterKey, key);
                }
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
        return payload.Version != BackupEncryption.LegacyVersion && WarnIfNewer(await BackupArchive.ReadManifestAsync(payload, CancellationToken.None));
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

        if (string.Equals(key.Trim(), "original", StringComparison.OrdinalIgnoreCase))
        {
            return OriginalRecoveryKey(options);
        }

        return BackupRecoveryKey.TryParse(key, out var bytes, out var problem) ? bytes : throw new RecoveryKeyException(problem);
    }

    /// <summary>
    /// The original recovery key, from this machine's own original data key: older backups open with it. Only the Server
    /// Node they were made on has that key, unless a restore installed it on another.
    /// </summary>
    private static byte[] OriginalRecoveryKey(Dictionary<string, string> options)
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = options.GetValueOrDefault("data-dir") });
        var path = FieldMasterKey.PathIn(directories);
        byte[]? masterKey;
        try
        {
            masterKey = FieldMasterKey.Load(path, FieldMasterKey.CreateProtector(HospitalDataProtection.CreateProvider(directories)));
        }
        catch (CryptographicException ex)
        {
            throw new RecoveryKeyException($"This machine's data key could not be read ({ex.Message}). Enter the recovery key instead.");
        }

        if (masterKey is null)
        {
            throw new RecoveryKeyException($"There is no data key at {path}, so 'original' cannot be used on this machine. Enter the recovery key instead.");
        }

        try
        {
            return BackupRecoveryKey.Original(masterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
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

    // "--name value" pairs wherever they start: after a command of two words (settings protect) or three (database sign-in show).
    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                result[args[i][2..]] = args[i + 1];
                i++;
            }
        }

        return result;
    }

    private sealed class RecoveryKeyException(string message) : Exception(message);
}
