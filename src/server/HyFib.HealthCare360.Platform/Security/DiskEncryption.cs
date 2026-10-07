using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>Whether BitLocker protects a disk (Win32_EncryptableVolume ProtectionStatus).</summary>
public enum VolumeProtection
{
    /// <summary>Not encrypted, still encrypting, or its key is readable on the disk (protection suspended).</summary>
    Off = 0,

    /// <summary>Fully encrypted, and the key is not readable on the disk.</summary>
    On = 1,

    /// <summary>BitLocker cannot tell, for example because the disk is locked.</summary>
    Unknown = 2,
}

/// <summary>How far a disk is encrypted (Win32_EncryptableVolume ConversionStatus).</summary>
public enum VolumeConversion
{
    NotEncrypted = 0,
    Encrypted = 1,
    Encrypting = 2,
    Decrypting = 3,
    EncryptionPaused = 4,
    DecryptionPaused = 5,
    Unknown = -1,
}

/// <summary>One fixed disk of the Server Node, by drive letter.</summary>
public sealed record VolumeEncryption(string Drive, VolumeProtection Protection, VolumeConversion Conversion);

/// <summary>
/// What the last disk encryption check found (<c>config\disk-encryption.json</c>). <paramref name="Available"/> is false
/// when BitLocker is not installed on the machine.
/// </summary>
public sealed record DiskEncryptionReport(DateTimeOffset CheckedAt, bool Available, IReadOnlyList<VolumeEncryption> Volumes);

/// <summary>
/// BitLocker on the Server Node (SRS SEC-05): every fixed disk should be encrypted, because the keys that open HC360's
/// stored data are on the same machine. Reading BitLocker's state needs administrator rights, which the service does not
/// have, so <c>HyFib.HealthCare360.Server.exe disk-encryption check</c> records it: the installer runs it, and the
/// "HC360 disk encryption check" scheduled task runs it as SYSTEM every day and at start-up. System health reads the
/// record and warns about a disk that is not protected, or a check that has not run for a while.
/// </summary>
public static class DiskEncryption
{
    public const string FileName = "disk-encryption.json";
    public const string ScheduledTaskName = "HC360 disk encryption check";

    /// <summary>The scheduled task checks every day; a record older than this means it has stopped.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(3);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathIn(DataDirectories directories) => Path.Combine(directories.Config, FileName);

    /// <summary>BitLocker's state of every fixed disk with a drive letter. Removable drives are left out.</summary>
    /// <exception cref="UnauthorizedAccessException">Not run with administrator rights.</exception>
    [SupportedOSPlatform("windows")]
    public static DiskEncryptionReport Check(DateTimeOffset now)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2\Security\MicrosoftVolumeEncryption",
                "SELECT DriveLetter, ProtectionStatus, ConversionStatus, VolumeType FROM Win32_EncryptableVolume");
            var volumes = new List<VolumeEncryption>();
            foreach (var item in searcher.Get())
            {
                using var volume = (ManagementObject)item;

                // VolumeType 2 is a removable drive, such as the USB disk backups are copied to; backups are encrypted by
                // HC360 itself.
                if (volume["DriveLetter"] is not string { Length: > 0 } drive || ToInt(volume["VolumeType"]) == 2)
                {
                    continue;
                }

                volumes.Add(new VolumeEncryption(
                    drive.ToUpperInvariant(),
                    ToInt(volume["ProtectionStatus"]) switch { 0 => VolumeProtection.Off, 1 => VolumeProtection.On, _ => VolumeProtection.Unknown },
                    ToInt(volume["ConversionStatus"]) is var status and >= 0 and <= 5 ? (VolumeConversion)status : VolumeConversion.Unknown));
            }

            return new DiskEncryptionReport(now, true, [.. volumes.OrderBy(v => v.Drive, StringComparer.Ordinal)]);
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass or ManagementStatus.ProviderLoadFailure)
        {
            // BitLocker's WMI provider is installed with BitLocker: Windows Home, and Windows Server without the feature,
            // have neither.
            return new DiskEncryptionReport(now, false, []);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            throw new UnauthorizedAccessException("Reading BitLocker's state needs administrator rights.", ex);
        }
    }

    /// <summary>The last check's record, or null when there is none or it cannot be read.</summary>
    public static DiskEncryptionReport? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<DiskEncryptionReport>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string path, DiskEncryptionReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(report, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>True when BitLocker is installed and fully protects every fixed disk.</summary>
    public static bool AllProtected(DiskEncryptionReport report) =>
        report.Available && report.Volumes.Count > 0 && report.Volumes.All(v => v.Protection == VolumeProtection.On);

    /// <summary>One line per disk, for the command's output.</summary>
    public static IEnumerable<string> Describe(DiskEncryptionReport report)
    {
        if (!report.Available)
        {
            yield return "BitLocker is not installed on this machine.";
            yield break;
        }

        foreach (var volume in report.Volumes)
        {
            yield return $"{volume.Drive} {State(volume)}";
        }
    }

    /// <summary>System health warnings about the last check. <paramref name="report"/> is null when it has never run.</summary>
    public static IReadOnlyList<HealthWarning> Warnings(DiskEncryptionReport? report, DateTimeOffset now)
    {
        if (report is null)
        {
            return
            [
                new("disk.encryption_unchecked", "warning",
                    "BitLocker has not been checked on this server. Run Install-HC360Server.ps1 again: it checks the disks and sets up a daily check."),
            ];
        }

        var warnings = new List<HealthWarning>();
        if (!report.Available)
        {
            warnings.Add(new("disk.unencrypted", "critical",
                "BitLocker is not installed on this server, so patient data on its disks is not encrypted. Turn on BitLocker, with the TPM, for every disk that holds HC360 data, backups or the database."));
        }

        var notEncrypted = report.Volumes
            .Where(v => v.Protection == VolumeProtection.Off && v.Conversion is not (VolumeConversion.Encrypted or VolumeConversion.Encrypting or VolumeConversion.EncryptionPaused))
            .Select(v => v.Drive).ToList();
        if (notEncrypted.Count > 0)
        {
            warnings.Add(new("disk.unencrypted", "critical",
                $"BitLocker is off on {Drives(notEncrypted)}, so patient data there is not encrypted. Turn on BitLocker, with the TPM, for every disk that holds HC360 data, backups or the database."));
        }

        var encrypting = report.Volumes
            .Where(v => v.Protection == VolumeProtection.Off && (v.Conversion is VolumeConversion.Encrypting or VolumeConversion.EncryptionPaused))
            .Select(v => v.Drive).ToList();
        if (encrypting.Count > 0)
        {
            warnings.Add(new("disk.encrypting", "warning",
                $"BitLocker has not finished encrypting {Drives(encrypting)}. The data there is protected once encryption is complete."));
        }

        var suspended = report.Volumes
            .Where(v => v.Protection == VolumeProtection.Off && v.Conversion == VolumeConversion.Encrypted)
            .Select(v => v.Drive).ToList();
        if (suspended.Count > 0)
        {
            warnings.Add(new("disk.encryption_suspended", "warning",
                $"BitLocker protection is suspended on {Drives(suspended)}: the disk is encrypted, but its key can be read from the disk until protection resumes. It resumes by itself after most Windows updates; otherwise resume it in BitLocker settings."));
        }

        var unknown = report.Volumes.Where(v => v.Protection == VolumeProtection.Unknown).Select(v => v.Drive).ToList();
        if (unknown.Count > 0)
        {
            warnings.Add(new("disk.encryption_unknown", "warning",
                $"BitLocker could not tell whether {Drives(unknown)} {(unknown.Count == 1 ? "is" : "are")} protected. A locked disk shows like this."));
        }

        if (now - report.CheckedAt > MaxAge)
        {
            warnings.Add(new("disk.encryption_stale", "warning",
                $"BitLocker was last checked on {report.CheckedAt.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}. Check that the \"{ScheduledTaskName}\" scheduled task still runs."));
        }

        return warnings;
    }

    private static string State(VolumeEncryption volume) => (volume.Protection, volume.Conversion) switch
    {
        (VolumeProtection.On, _) => "BitLocker on",
        (VolumeProtection.Unknown, _) => "BitLocker state unknown (the disk may be locked)",
        (_, VolumeConversion.Encrypted) => "BitLocker suspended (encrypted, key readable on the disk)",
        (_, VolumeConversion.Encrypting or VolumeConversion.EncryptionPaused) => "BitLocker still encrypting",
        _ => "BitLocker off (not encrypted)",
    };

    private static string Drives(IReadOnlyList<string> drives) =>
        drives.Count == 1 ? drives[0] : $"{string.Join(", ", drives.Take(drives.Count - 1))} and {drives[^1]}";

    private static int ToInt(object? value) => value is null ? -1 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
}
