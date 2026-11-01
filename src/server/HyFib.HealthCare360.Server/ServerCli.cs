using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace HyFib.HealthCare360.Server;

/// <summary>Offline maintenance commands for administrators on the Server Node.</summary>
internal static class ServerCli
{
    private const string DatabasePasswordVariable = "HC360_DATABASE_PASSWORD";

    private const string CaPasswordVariable = "HC360_CA_PASSWORD";

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

          certificates export-ca --out <file.pfx> [--data-dir <folder>]
              Writes the hospital CA, with its private key, to a new file encrypted with a password of at least 16
              characters (AES-256), for a warm standby Server Node or replacement hardware to import. HC360 Desktop and the
              lab agents trust only this CA, so a server that has it serves them without their being set up again. Whoever
              has the file and its password can make certificates they trust: keep it as safely as the backup recovery key,
              apart from its password. The password is read from the HC360_CA_PASSWORD environment variable, from standard
              input when it is redirected, or asked for twice; never give it on the command line.

          certificates import-ca --in <file.pfx> [--data-dir <folder>]
              Installs an exported hospital CA on this Server Node, protected with DPAPI for this machine, with the HC360
              service stopped (or restart it after). A different CA already here is kept under a new name, and the server
              issues itself a new certificate from the imported CA when it starts. Prints the CA's fingerprint: it is the
              one HC360 Desktop pinned. The password is read as for export-ca.

          certificates prepare-ca [--confirm] [--data-dir <folder>]
              Prepares the next hospital CA in this one's last year, the first of a rollover's two steps: this CA's name, a
              new key, and a certificate this CA signs for the new key. The server keeps serving with this CA and hands both
              out: HC360 Desktop and the lab agents take the next CA by themselves, tablets and browsers are given it, and
              System health names every device that does not hold it yet (docs/deployment/CA-ROLLOVER.md). Not for a key that
              may have been seen by someone it should not have been: use new-ca then. Without --confirm it says what it would
              do, and changes nothing. Run it with the HC360 service stopped, then start the service.

          certificates switch-ca [--confirm] [--data-dir <folder>]
              Switches the server to the prepared next CA, the second step. Refused while any workstation or lab agent seen in
              the last 30 days, and not revoked, does not hold it: after the switch it would no longer trust the server. The
              CA it replaces is kept under a new name, and handed out until it expires. Without --confirm it says what it
              would do, and changes nothing. Run it with the HC360 service stopped, then start the service.

          certificates new-ca [--confirm] [--data-dir <folder>]
              Makes a new hospital CA in place of this server's: for a CA whose key may have been seen by someone it should
              not have been, or one that has already expired (a CA in its last year rolls over with prepare-ca instead). Every
              workstation, tablet, HC360 Desktop and lab agent trusts the CA it replaces, so each must be set up again
              (docs/deployment/CA-ROLLOVER.md): without --confirm it says which CA it would replace, and changes nothing.
              The CA it replaces is kept under a new name, so it can be put back. Run it with the HC360 service stopped,
              then start the service: the server issues itself a certificate from the new CA. Prints the new CA's
              fingerprint, which the devices pin when they are set up again.

          certificates probe <https://address>... [--ca-fingerprint <fingerprint> | --ca-file <ca.crt>] [--event-log]
              Checks each address from this machine as a device does: the certificate served is for that name, was issued
              by the hospital CA, and neither expires within the periods System health warns in. With --ca-fingerprint (the
              one the installer printed and the devices pinned) it reads the CA the server hands out and checks it is that
              one; with --ca-file it trusts that CA certificate; with neither, the roots this machine trusts. --event-log
              writes what needs doing to the Application event log as well. Run it from another machine too: it tells when
              HC360 is down, or cannot send its own alerts. Exit code 0 when all is well, 1 when a certificate needs
              planning, 2 when one needs action now or an address cannot be checked, as monitoring tools read it.

          disk-encryption check [--data-dir <folder>]
              Records whether BitLocker protects each fixed disk of this machine, for System health. It needs administrator
              rights; the "HC360 disk encryption check" scheduled task runs it as SYSTEM every day and at start-up. Exit code
              0 when every disk is protected, 3 when one is not.

          audit verify-export --in <hc360-audit-....json> [--anchors <anchors.log>]
              Checks an audit export (Administration > Audit log > Export for auditors) on any machine, without the
              database: every record's hash is that of its content and links to the one before, and each anchor in the
              export, and in --anchors (a copy of the anchor file kept elsewhere), matches the record at its position. The
              keyed seals need the server's key, so HC360's own verification checks them. Exit code 0 when it is intact, 3
              when it is not.

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

    /// <summary>The options that take no value.</summary>
    private static readonly string[] Switches = ["--dry-run", "--confirm", "--event-log"];

    public static bool IsCommand(string argument) =>
        argument is "backup" or "audit" or "catalogue" or "settings" or "database" or "certificates" or "disk-encryption" or "loadtest" or "desktop" or "help"
            or "--help" or "-h";

    public static async Task<int> RunAsync(string[] args)
    {
        // Switches that take no value, taken out before the "--name value" pairs are read.
        var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
        var confirmed = args.Contains("--confirm", StringComparer.OrdinalIgnoreCase);
        var eventLog = args.Contains("--event-log", StringComparer.OrdinalIgnoreCase);
        args = [.. args.Where(a => !Switches.Contains(a, StringComparer.OrdinalIgnoreCase))];
        var options = args.Length > 2 ? ParseOptions(args[2..]) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return args switch
            {
                ["backup", "verify", ..] when options.ContainsKey("in") => await VerifyAsync(options),
                ["backup", "restore", ..] when options.ContainsKey("in") => await RestoreAsync(options),
                ["backup", "decrypt", ..] when options.ContainsKey("in") && options.ContainsKey("out") => await DecryptAsync(options),
                ["audit", "verify-export", ..] when options.ContainsKey("in") => AuditCli.VerifyExport(options["in"], options.GetValueOrDefault("anchors")),
                ["catalogue", "import", ..] when options.ContainsKey("file") => await CatalogueCli.ImportAsync(options["file"], dryRun),
                ["loadtest", "patients", ..] when options.ContainsKey("count") => await LoadTestCli.AddPatientsAsync(options["count"]),
                ["loadtest", "accounts", ..] when options.ContainsKey("count") => await LoadTestCli.AddAccountsAsync(options["count"]),
                ["settings", "protect", ..] => ProtectSettings(options),
                ["database", "sign-in", "show", ..] => ShowDatabaseSignIn(options),
                ["database", "sign-in", "windows", ..] => ChangeDatabaseSignIn(options, windows: true),
                ["database", "sign-in", "password", ..] => ChangeDatabaseSignIn(options, windows: false),
                ["database", "watchdog", ..] when options.ContainsKey("service") && options.ContainsKey("pgdata") => WatchDatabase(options),
                ["disk-encryption", "check", ..] => CheckDiskEncryption(options),
                ["certificates", "export-ca", ..] when options.ContainsKey("out") => ExportCa(options),
                ["certificates", "import-ca", ..] when options.ContainsKey("in") => ImportCa(options),
                ["certificates", "new-ca", ..] => NewCa(options, confirmed),
                ["certificates", "prepare-ca", ..] => PrepareCa(options, confirmed),
                ["certificates", "switch-ca", ..] => await SwitchCaAsync(options, confirmed, ReadDeviceReadinessAsync),
                ["certificates", "probe", ..] when Positional(args[2..]).Length > 0 => await ProbeAsync(Positional(args[2..]), options, eventLog),
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
        var directories = SiteDirectories(options);
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
        Console.WriteLine(result.AuditSealKeyInstalled
            ? "Installed the backup's audit seal key: the restored audit trail's seals verify on this server."
            : "This backup has no audit seal key (it is older): audit records it sealed with a key will not verify here.");
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

    /// <summary>
    /// Writes the hospital CA with its private key to a new password-protected file, for a warm standby or replacement
    /// hardware (SRS DED-20). Needs administrator rights, to open the CA with this machine's DPAPI.
    /// </summary>
    private static int ExportCa(Dictionary<string, string> options)
    {
        var output = Path.GetFullPath(options["out"]);
        if (File.Exists(output))
        {
            Console.Error.WriteLine($"{output} already exists: name a new file.");
            return 1;
        }

        if (ReadCaPassword(confirm: true) is not { } password)
        {
            return 1;
        }

        var directories = SiteDirectories(options);
        ExportedHospitalCa exported;
        try
        {
            exported = LanCertificateManager.ExportCa(directories, HospitalDataProtection.CreateNodeProvider(directories), password);
        }
        catch (Exception ex) when (ex is FileNotFoundException or CryptographicException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"The hospital CA could not be read: {ex.Message} Run it on the Server Node from an elevated prompt.");
            return 2;
        }

        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(exported.Pkcs12);
        }

        Console.WriteLine($"Wrote the hospital CA, with its private key, to {output}, encrypted with the password.");
        Console.WriteLine($"Fingerprint: {GroupFingerprint(exported.Fingerprint)} (valid until {exported.ExpiresAt:dd MMM yyyy}).");
        if (exported.PreviousCaExpiresAt is { } previousEnds)
        {
            Console.WriteLine($"It carries the CA this one replaced too, which the standby hands out until {previousEnds:dd MMM yyyy}, as this server does.");
        }

        if (exported.NextCaLeftHere)
        {
            Console.WriteLine("The next CA prepared here is not in it: the standby serves with this CA, which the devices hold. Export again after the switch.");
        }

        Console.WriteLine("Import it on the standby with 'certificates import-ca'. Keep the file as safely as the backup recovery key, apart from");
        Console.WriteLine("its password, and delete copies you no longer need: with both, anyone can make certificates the workstations trust.");
        return 0;
    }

    /// <summary>Installs an exported hospital CA on this Server Node, protected for this machine (SRS DED-20).</summary>
    private static int ImportCa(Dictionary<string, string> options)
    {
        var input = options["in"];
        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"There is no file at {input}.");
            return 1;
        }

        if (ReadCaPassword(confirm: false) is not { } password)
        {
            return 1;
        }

        var directories = SiteDirectories(options);
        ImportedHospitalCa imported;
        try
        {
            imported = LanCertificateManager.ImportCa(directories, HospitalDataProtection.CreateNodeProvider(directories), File.ReadAllBytes(input), password, DateTimeOffset.UtcNow);
        }
        catch (CryptographicException)
        {
            Console.Error.WriteLine("The password does not open this file, or the file is damaged.");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"{ex.Message} Run it from an elevated prompt.");
            return 2;
        }

        Console.WriteLine(imported.Changed
            ? $"Installed the hospital CA in {directories.Certificates}, protected for this machine."
            : "This server already has this hospital CA: nothing changed.");
        Console.WriteLine($"Fingerprint: {GroupFingerprint(imported.Fingerprint)} (valid until {imported.ExpiresAt:dd MMM yyyy}). HC360 Desktop shows the one it pinned.");
        if (imported.PreviousCaExpiresAt is { } previousEnds)
        {
            Console.WriteLine($"With the CA it replaced, which this server hands out until {previousEnds:dd MMM yyyy}, as the one it came from does.");
        }

        if (imported.PreviousKeptAs is { } keptAs)
        {
            Console.WriteLine($"The different CA that was here is kept as {keptAs}.");
        }

        if (imported.Changed)
        {
            Console.WriteLine("HC360 issues itself a new server certificate from this CA when it starts: restart the HC360Server service if it runs.");
        }

        return 0;
    }

    /// <summary>
    /// Makes a new hospital CA in place of this server's, once the administrator has confirmed it: every device that trusts
    /// the CA it replaces must be set up again (docs/deployment/CA-ROLLOVER.md). Needs administrator rights.
    /// </summary>
    private static int NewCa(Dictionary<string, string> options, bool confirmed)
    {
        var directories = SiteDirectories(options);
        if (!confirmed)
        {
            using var current = LanCertificateManager.PublicCa(directories);
            Console.WriteLine(current is null
                ? "This server has no hospital CA yet: HC360 makes one when it first starts."
                : $"This server's hospital CA is {GroupFingerprint(CertificateProbe.Fingerprint(current))}, valid until {current.NotAfter.ToUniversalTime():dd MMM yyyy}.");
            Console.WriteLine("'certificates new-ca' makes a new CA in its place. Every workstation, tablet, HC360 Desktop and lab agent trusts this one,");
            Console.WriteLine("so each must be set up again before it connects (docs/deployment/CA-ROLLOVER.md). Nothing has changed.");
            Console.WriteLine("Run it again with --confirm, with the HC360 service stopped, to make the new CA.");
            return 1;
        }

        var facilityName = SiteSettings(options)[$"{PlatformOptions.SectionName}:{nameof(PlatformOptions.DefaultFacilityName)}"] ?? new PlatformOptions().DefaultFacilityName;
        NewHospitalCa made;
        try
        {
            made = LanCertificateManager.NewCa(directories, HospitalDataProtection.CreateNodeProvider(directories), facilityName, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or CryptographicException)
        {
            Console.Error.WriteLine($"The new hospital CA could not be made: {ex.Message} Run it on the Server Node from an elevated prompt; nothing has changed.");
            return 2;
        }

        Console.WriteLine($"Made a new hospital CA in {directories.Certificates}, protected for this machine.");
        Console.WriteLine($"Fingerprint: {GroupFingerprint(made.Fingerprint)} (valid until {made.ExpiresAt:dd MMM yyyy}). The devices pin it when they are set up again.");
        if (made.PreviousKeptAs is { } keptAs)
        {
            Console.WriteLine(made.PreviousFingerprint is { } previous
                ? $"The CA it replaces, {GroupFingerprint(previous)} (valid until {made.PreviousExpiresAt:dd MMM yyyy}), is kept as {keptAs}; CA-ROLLOVER.md says how to put it back."
                : $"The CA it replaces is kept as {keptAs}; CA-ROLLOVER.md says how to put it back.");
        }

        Console.WriteLine("Next, as CA-ROLLOVER.md says:");
        Console.WriteLine("  1. Start the HC360Server service (or restart it): it issues itself a certificate from the new CA.");
        Console.WriteLine("  2. On a warm standby and every other server node, import the new CA: 'certificates export-ca' here, 'certificates import-ca' there.");
        Console.WriteLine("  3. Set every device up again: HC360 Desktop (a new enrolment code, and the new fingerprint wherever IT set -CaFingerprint),");
        Console.WriteLine("     the lab agents, and each tablet and browser (install the CA from https://<server>:7360/api/v1/system/ca-certificate).");
        Console.WriteLine("  4. Give the certificate probe the new fingerprint.");
        return 0;
    }

    /// <summary>
    /// Prepares the next hospital CA, once the administrator has confirmed it: the first of a rollover's two steps
    /// (docs/deployment/CA-ROLLOVER.md). Needs administrator rights.
    /// </summary>
    private static int PrepareCa(Dictionary<string, string> options, bool confirmed)
    {
        var directories = SiteDirectories(options);
        if (!confirmed)
        {
            using var current = LanCertificateManager.PublicCa(directories);
            Console.WriteLine(current is null
                ? "This server has no hospital CA yet: HC360 makes one when it first starts."
                : $"This server's hospital CA is {GroupFingerprint(CertificateProbe.Fingerprint(current))}, valid until {current.NotAfter.ToUniversalTime():dd MMM yyyy}.");
            Console.WriteLine("'certificates prepare-ca' makes the next CA: this CA's name, a new key, valid for ten years, and a certificate this CA signs");
            Console.WriteLine("for it. The server keeps serving with this CA and hands both out: HC360 Desktop and the lab agents take the next CA by");
            Console.WriteLine("themselves, and tablets and browsers are given it. Once every device holds it, 'certificates switch-ca' switches the server");
            Console.WriteLine("to it. Not for a key that may have been seen by someone else: use 'certificates new-ca' then.");
            Console.WriteLine("Nothing has changed. Run it again with --confirm, with the HC360 service stopped, to prepare the next CA.");
            return 1;
        }

        PreparedHospitalCa prepared;
        try
        {
            prepared = LanCertificateManager.PrepareCa(directories, HospitalDataProtection.CreateNodeProvider(directories), DateTimeOffset.UtcNow);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"{ex.Message} Nothing has changed.");
            return 1;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException or CryptographicException)
        {
            Console.Error.WriteLine($"The next hospital CA could not be prepared: {ex.Message} Run it on the Server Node from an elevated prompt; nothing has changed.");
            return 2;
        }

        Console.WriteLine($"Prepared the next hospital CA in {directories.Certificates}, protected for this machine.");
        Console.WriteLine($"Next CA: {GroupFingerprint(prepared.Fingerprint)} (valid until {prepared.ExpiresAt:dd MMM yyyy}).");
        Console.WriteLine($"The server keeps serving with {GroupFingerprint(prepared.CurrentFingerprint)}, which vouches for it, until the switch.");
        Console.WriteLine("Next, as CA-ROLLOVER.md says:");
        Console.WriteLine("  1. Start the HC360Server service (or restart it): it hands the next CA out. HC360 Desktop and the lab agents take it within");
        Console.WriteLine("     six hours of reaching the server.");
        Console.WriteLine("  2. Give the next CA to tablets and browsers (MDM), the certificate probe and other systems that call HC360, beside the current one.");
        Console.WriteLine($"  3. Once System health names no device without it, and before {prepared.CurrentExpiresAt:dd MMM yyyy}: 'certificates switch-ca --confirm',");
        Console.WriteLine("     with the service stopped.");
        return 0;
    }

    /// <summary>
    /// Switches the server to the prepared next CA, once the administrator has confirmed it and every device seen holds it: the
    /// second of a rollover's two steps (docs/deployment/CA-ROLLOVER.md). <paramref name="readDevices"/> reads which devices do
    /// not hold the next CA, from the database. Needs administrator rights.
    /// </summary>
    internal static async Task<int> SwitchCaAsync(
        Dictionary<string, string> options, bool confirmed, Func<string, DateTimeOffset, Task<CaRolloverReadiness>> readDevices)
    {
        var directories = SiteDirectories(options);
        var keys = HospitalDataProtection.CreateNodeProvider(directories);
        var now = DateTimeOffset.UtcNow;
        string? next;
        try
        {
            next = LanCertificateManager.PreparedNextCaFingerprint(directories, keys, now);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or CryptographicException)
        {
            Console.Error.WriteLine($"The hospital CA could not be read: {ex.Message} Run it on the Server Node from an elevated prompt; nothing has changed.");
            return 2;
        }

        if (next is null)
        {
            Console.Error.WriteLine("No next CA is prepared: prepare one with 'certificates prepare-ca', and switch once every device holds it. Nothing has changed.");
            return 1;
        }

        // After the switch a device without the next CA no longer trusts the server: every one seen lately must hold it.
        CaRolloverReadiness readiness;
        try
        {
            readiness = await readDevices(next, now);
        }
        catch (Exception ex) when (ex is NpgsqlException or System.TimeoutException or InvalidOperationException)
        {
            Console.Error.WriteLine($"The HC360 database could not be reached to check which devices hold the next CA: {ex.Message} Switch once it can be; nothing has changed.");
            return 3;
        }

        if (readiness.NotReady > 0)
        {
            Console.Error.WriteLine($"{readiness.NotReady} of the {readiness.Seen} devices seen in the last 30 days do not hold the next CA, and would no longer trust the server:");
            foreach (var name in readiness.WithoutNextCa)
            {
                Console.Error.WriteLine($"  {name}: holds other CAs");
            }

            foreach (var name in readiness.NotReporting)
            {
                Console.Error.WriteLine($"  {name}: does not say (an HC360 Desktop or lab agent older than this server)");
            }

            Console.Error.WriteLine("Each takes it within six hours of reaching the server. Update or look into these, or revoke one that is gone");
            Console.Error.WriteLine("(Administration › Devices), then switch. Nothing has changed.");
            return 1;
        }

        if (!confirmed)
        {
            Console.WriteLine($"Every one of the {readiness.Seen} devices seen in the last 30 days holds the next CA, {GroupFingerprint(next)}.");
            Console.WriteLine("'certificates switch-ca' makes it this server's CA. Tablets, browsers and other systems that call HC360 cannot say which");
            Console.WriteLine("CAs they hold: give it to them first (MDM), or they no longer trust the server after the switch.");
            Console.WriteLine("Nothing has changed. Run it again with --confirm, with the HC360 service stopped, to switch.");
            return 1;
        }

        SwitchedHospitalCa switched;
        try
        {
            switched = LanCertificateManager.SwitchCa(directories, keys, now);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"{ex.Message} Nothing has changed.");
            return 1;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException or CryptographicException)
        {
            Console.Error.WriteLine($"The server could not be switched to the next CA: {ex.Message} Run it on the Server Node from an elevated prompt; nothing has changed.");
            return 2;
        }

        Console.WriteLine($"Switched the hospital CA in {directories.Certificates}: it is now {GroupFingerprint(switched.Fingerprint)} (valid until {switched.ExpiresAt:dd MMM yyyy}).");
        Console.WriteLine($"The CA it replaced, {GroupFingerprint(switched.PreviousFingerprint)}, is handed out until it expires on {switched.PreviousExpiresAt:dd MMM yyyy}"
            + $"{(switched.PreviousKeptAs is { } keptAs ? $", and kept as {keptAs}" : "")}.");
        Console.WriteLine("Next, as CA-ROLLOVER.md says:");
        Console.WriteLine("  1. Start the HC360Server service: it issues itself a certificate from the new CA.");
        Console.WriteLine("  2. On a warm standby and every other server node, import the CA again: 'certificates export-ca' here, 'certificates import-ca' there.");
        Console.WriteLine("  3. Give the certificate probe's tasks the new fingerprint.");
        return 0;
    }

    /// <summary>The devices that do not hold <paramref name="nextCaFingerprint"/>, from the database the server uses.</summary>
    private static async Task<CaRolloverReadiness> ReadDeviceReadinessAsync(string nextCaFingerprint, DateTimeOffset now)
    {
        await using var app = CatalogueCli.BuildApplication();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await CaRolloverDevices.ReadAsync(db, nextCaFingerprint, now, CancellationToken.None);
    }

    /// <summary>
    /// Checks HC360 addresses as a device does, for a scheduled task or a monitoring tool (SRS DED-04): the exit code is the
    /// worst outcome, numbered as monitoring tools read it.
    /// </summary>
    private static async Task<int> ProbeAsync(string[] addresses, Dictionary<string, string> options, bool eventLog)
    {
        var pinned = options.GetValueOrDefault("ca-fingerprint");
        if (pinned is not null && CertificateProbe.Normalise(pinned).Length != 64)
        {
            Console.Error.WriteLine("A CA fingerprint is 64 hexadecimal characters, as the installer printed it and HC360 Desktop shows it.");
            return (int)CertificateProbeOutcome.Critical;
        }

        X509Certificate2? ca = null;
        if (options.GetValueOrDefault("ca-file") is { } caFile)
        {
            if (pinned is not null)
            {
                Console.Error.WriteLine("Give the CA's fingerprint or its file, not both.");
                return (int)CertificateProbeOutcome.Critical;
            }

            try
            {
                ca = CertificateProbe.LoadCaFile(caFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                Console.Error.WriteLine($"The CA certificate could not be read from {caFile}: {ex.Message}");
                return (int)CertificateProbeOutcome.Critical;
            }
        }

        if (eventLog && !OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The event log exists only on Windows: the findings are written here alone.");
        }

        using (ca)
        {
            var worst = CertificateProbeOutcome.Ok;
            foreach (var given in addresses)
            {
                var result = Uri.TryCreate(given, UriKind.Absolute, out var address)
                    ? await CertificateProbe.ProbeAsync(address, pinned, ca, DateTimeOffset.UtcNow, CancellationToken.None)
                    : new CertificateProbeResult(new Uri("https://invalid/"), CertificateProbeOutcome.Critical, [$"{given} is not an https address."], null, null, null);
                var label = result.Outcome switch
                {
                    CertificateProbeOutcome.Ok => "OK",
                    CertificateProbeOutcome.Warning => "WARNING",
                    _ => "CRITICAL",
                };
                Console.WriteLine($"{label,-8} {given}");
                foreach (var finding in result.Findings)
                {
                    Console.WriteLine($"         {finding}");
                }

                worst = result.Outcome > worst ? result.Outcome : worst;
                if (eventLog && result.Outcome != CertificateProbeOutcome.Ok && OperatingSystem.IsWindows())
                {
                    WriteEvent($"HC360 certificate probe, {given}: {string.Join(" ", result.Findings)}",
                        result.Outcome == CertificateProbeOutcome.Critical ? EventLogEntryType.Error : EventLogEntryType.Warning);
                }
            }

            return (int)worst;
        }
    }

    /// <summary>
    /// The password of an exported CA: from <see cref="CaPasswordVariable"/>, from standard input when it is redirected, or
    /// from a hidden prompt, asked twice to <paramref name="confirm"/> it. Null, after saying why, when it is not usable.
    /// </summary>
    private static string? ReadCaPassword(bool confirm)
    {
        var password = Environment.GetEnvironmentVariable(CaPasswordVariable);
        if (string.IsNullOrEmpty(password))
        {
            password = ReadSecret("Password for the CA file: ");
            if (confirm && !Console.IsInputRedirected && password != ReadSecret("The same password again: "))
            {
                Console.Error.WriteLine("The two passwords differ.");
                return null;
            }
        }

        if (string.IsNullOrEmpty(password) || password.Length < LanCertificateManager.MinimumExportPasswordLength)
        {
            Console.Error.WriteLine($"Use a password of at least {LanCertificateManager.MinimumExportPasswordLength} characters.");
            return null;
        }

        return password;
    }

    /// <summary>"AB12CD34…" as "AB12 CD34 …", as the installer prints it and HC360 Desktop shows it.</summary>
    private static string GroupFingerprint(string fingerprint) =>
        string.Join(' ', Enumerable.Range(0, fingerprint.Length / 4).Select(i => fingerprint.Substring(i * 4, 4)));

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
        var directories = SiteDirectories(options);
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

    /// <summary>The arguments that are neither an option ("--name") nor its value: the addresses 'certificates probe' checks.</summary>
    private static string[] Positional(string[] args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            result.Add(args[i]);
        }

        return [.. result];
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

    /// <summary>
    /// The server's folders as the service sees them: the data directory (<c>--data-dir</c>, or the default), with the keys
    /// and documents in the folder several nodes share when the site settings name one (SCAL-01), so a command wraps and
    /// unwraps keys with the same Data Protection key ring as the service.
    /// </summary>
    internal static DataDirectories SiteDirectories(Dictionary<string, string> options)
    {
        var cluster = new ClusterOptions();
        SiteSettings(options).GetSection($"{PlatformOptions.SectionName}:Cluster").Bind(cluster);
        return new DataDirectories(new PlatformOptions { DataDirectory = options.GetValueOrDefault("data-dir"), Cluster = cluster });
    }

    /// <summary>The site settings as the service reads them: config\hc360.settings.json, then the HC360_ environment variables.</summary>
    private static IConfiguration SiteSettings(Dictionary<string, string> options)
    {
        var local = new DataDirectories(new PlatformOptions { DataDirectory = options.GetValueOrDefault("data-dir") });
        return new ConfigurationBuilder()
            .AddProtectedJsonFile(ProtectedSettings.PathIn(local))
            .AddEnvironmentVariables("HC360_")
            .Build();
    }

    private sealed class RecoveryKeyException(string message) : Exception(message);
}
