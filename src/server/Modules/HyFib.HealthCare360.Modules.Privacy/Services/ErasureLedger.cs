using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Privacy.Services;

/// <summary>
/// One erasure carried out for a privacy request, as the ledger keeps it: which request, which patient, what was erased
/// and when. No name, number or other personal data: the patient and the request are named by their ids only.
/// <paramref name="Mac"/> is the audit seal key's HMAC of <see cref="ErasureLedger.Canonical"/>, so a line written by
/// anyone without that key (kept outside the database) is not replayed.
/// </summary>
public sealed record ErasureLedgerEntry(Guid Id, Guid RequestId, string RequestNumber, Guid PatientId, IReadOnlyList<string> Items, DateTimeOffset ErasedAt, string? Mac = null);

/// <summary>
/// The erasure ledger (DPDP Act 2023 s8(7) and s12(3)): every erasure is appended, as it is made, to a file outside the
/// database, so that a database restored from a backup made before the erasure can have it applied again
/// (<see cref="ErasureLedgerReplay"/>). Backups are kept grandfather-father-son (<c>HealthCare360:Backup:Retention</c>: 14
/// daily, 8 weekly and 12 monthly by default, so up to about 342 days, <see cref="BackupOptions.RetentionDays"/>) and cannot
/// be edited, so erased data stays in them until they expire; the ledger, which every backup also carries, is what keeps an
/// erasure from coming back with a restore in that time.
/// <list type="bullet">
/// <item>The file is beside the audit anchors, in the folder the servers share when there are several
/// (<c>privacy/erasure-ledger.log</c>), and is copied, line by line as it is written, to the backups' second copy
/// (<c>HealthCare360:Backup:SecondaryDirectory</c>) when one is set, so it outlives the Server Node's own disk.</item>
/// <item>Every backup carries both, inside its encryption, and a restore adds their lines, with those of the copy beside
/// the backup file, to the ledger of the server it restores onto (<see cref="ErasureLedgerFiles"/>), so the replay there
/// applies them however the server that made them was lost: on new hardware, from the second disk or the off-site copy.</item>
/// <item>One JSON object a line, appended and flushed to disk, never rewritten. Reading takes the union of both copies.</item>
/// <item>Each line is sealed with the audit seal key (HMAC-SHA256); a line whose seal does not verify is reported and
/// never replayed, so nobody who can write the folder but not read the key can make the server erase someone's data.</item>
/// </list>
/// </summary>
public sealed class ErasureLedger(DataDirectories directories, IOptions<PlatformOptions> options, AuditSealKey key, ILogger<ErasureLedger> logger)
{
    public const string FileName = ErasureLedgerFiles.FileName;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lock Write = new();

    /// <summary>The ledger beside the audit anchors (in the shared folder when there are several servers), where backups take it from.</summary>
    public string Path => ErasureLedgerFiles.PathIn(directories);

    /// <summary>The copy kept with the backups' second copy, or null when no second copy is set.</summary>
    public string? MirrorPath => ErasureLedgerFiles.MirrorPath(options.Value.Backup);

    /// <summary>What is sealed: the entry's fields in a fixed order, so the seal does not depend on JSON formatting.</summary>
    public static string Canonical(ErasureLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return string.Join('|', "v1", entry.Id.ToString("N"), entry.RequestId.ToString("N"), entry.RequestNumber, entry.PatientId.ToString("N"),
            string.Join(',', entry.Items.Order(StringComparer.Ordinal)), entry.ErasedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>The entry sealed with the audit seal key.</summary>
    public ErasureLedgerEntry Seal(ErasureLedgerEntry entry) => entry with { Mac = key.Seal(Digest(entry)) };

    /// <summary>Whether the entry's seal is the audit seal key's.</summary>
    public bool Verifies(ErasureLedgerEntry entry) => entry.Mac is { Length: > 0 } mac && key.Sealed(Digest(entry), mac);

    /// <summary>
    /// Appends a sealed entry to the ledger and to its copy, flushed to disk. The copy is best effort: when it cannot be
    /// written the ledger still is, and the next replay writes the line there again.
    /// </summary>
    public void Append(ErasureLedgerEntry entry)
    {
        var sealedEntry = entry.Mac is null ? Seal(entry) : entry;
        var line = JsonSerializer.Serialize(sealedEntry, Json);
        lock (Write)
        {
            AppendLine(Path, line);
            if (MirrorPath is { } mirror)
            {
                try
                {
                    AppendLine(mirror, line);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "The erasure ledger's copy at {Path} could not be written; the next replay writes it again", mirror);
                }
            }
        }

        logger.LogInformation("Erasure {Entry} for privacy request {Request} written to the erasure ledger", sealedEntry.Id, sealedEntry.RequestNumber);
    }

    /// <summary>
    /// The entries of both copies (each once), oldest first, with the number of lines that did not read or whose seal did
    /// not verify. Those are never returned.
    /// </summary>
    public (IReadOnlyList<ErasureLedgerEntry> Entries, int Rejected) Read()
    {
        var entries = new Dictionary<Guid, ErasureLedgerEntry>();
        var rejected = 0;
        foreach (var path in new[] { Path, MirrorPath }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                ErasureLedgerEntry? entry = null;
                try
                {
                    entry = JsonSerializer.Deserialize<ErasureLedgerEntry>(line, Json);
                }
                catch (JsonException)
                {
                }

                if (entry is null || entry.Items is null || !Verifies(entry))
                {
                    rejected++;
                    continue;
                }

                entries.TryAdd(entry.Id, entry);
            }
        }

        return ([.. entries.Values.OrderBy(e => e.ErasedAt).ThenBy(e => e.Id)], rejected);
    }

    /// <summary>Writes to the copy the entries it is missing (after it was unreachable, or newly set up).</summary>
    internal int CompleteMirror(IReadOnlyList<ErasureLedgerEntry> entries)
    {
        if (MirrorPath is not { } mirror)
        {
            return 0;
        }

        var present = File.Exists(mirror)
            ? File.ReadLines(mirror).Select(TryRead).OfType<ErasureLedgerEntry>().Select(e => e.Id).ToHashSet()
            : [];
        var missing = entries.Where(e => !present.Contains(e.Id)).ToList();
        lock (Write)
        {
            foreach (var entry in missing)
            {
                AppendLine(mirror, JsonSerializer.Serialize(entry, Json));
            }
        }

        return missing.Count;
    }

    private static ErasureLedgerEntry? TryRead(string line)
    {
        try
        {
            return string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<ErasureLedgerEntry>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Digest(ErasureLedgerEntry entry) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(entry))));

    private static void AppendLine(string path, string line)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file);
        writer.WriteLine(line);
        writer.Flush();
        file.Flush(flushToDisk: true);
    }
}
