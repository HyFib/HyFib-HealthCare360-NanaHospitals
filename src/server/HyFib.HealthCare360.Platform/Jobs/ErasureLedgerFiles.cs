using System.Text;
using HyFib.HealthCare360.Platform.Hosting;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// The erasure ledger's files (DPDP Act 2023 s8(7)), which the Privacy module writes (<c>ErasureLedger</c>) and every backup
/// carries (<see cref="BackupArchive.ErasureLedgerEntry"/>): the ledger beside the audit anchors, and its copy beside the
/// backups' second copy. A backup takes every line of both, and a restore adds to this server's ledger the lines it lacks,
/// so the replay at the next start applies again every erasure the restored database does not have, however the server
/// that made it was lost. Platform moves the lines as they are: what each says, and whether the audit seal key sealed it,
/// is the replay's to check.
/// </summary>
public static class ErasureLedgerFiles
{
    public const string FileName = "erasure-ledger.log";

    /// <summary>The ledger beside the audit anchors (in the shared folder when there are several servers).</summary>
    public static string PathIn(DataDirectories directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        return Path.Combine(directories.Shared ?? directories.Root, "privacy", FileName);
    }

    /// <summary>The copy kept with the backups' second copy, or null when no second copy is set.</summary>
    public static string? MirrorPath(BackupOptions backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return backup.SecondaryDirectory is { Length: > 0 } secondary
            ? Path.Combine(Path.GetFullPath(Environment.ExpandEnvironmentVariables(secondary)), FileName)
            : null;
    }

    /// <summary>
    /// The ledger as a backup carries it: every line of the ledger, then each line of its copy the ledger lacks (one the
    /// ledger lost with a disk, say), each ending in a line feed. A line still being appended, without its line end, is left
    /// to the next backup. The copy is read as far as it can be: an unplugged disk leaves the ledger alone.
    /// </summary>
    /// <exception cref="IOException">The ledger itself cannot be read.</exception>
    public static byte[] ReadForBackup(DataDirectories directories, BackupOptions backup)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddLines(lines, seen, PathIn(directories));
        if (MirrorPath(backup) is { } mirror)
        {
            try
            {
                AddLines(lines, seen, mirror);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The ledger has what the copy would add, unless the ledger lost lines too, which the next backup takes.
            }
        }

        return Join(lines);
    }

    /// <summary>The complete lines of a ledger as a backup carries it, or of a ledger file read as it is.</summary>
    public static IReadOnlyList<string> Lines(byte[] ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return CompleteLines(Encoding.UTF8.GetString(ledger));
    }

    /// <summary>The complete lines of the ledger file at <paramref name="path"/>; none when there is no file.</summary>
    public static IReadOnlyList<string> ReadLines(string path) => File.Exists(path) ? CompleteLines(ReadText(path)) : [];

    /// <summary>
    /// Appends to the ledger at <paramref name="path"/> each line of <paramref name="lines"/> it lacks, in order, flushed to
    /// disk, and returns how many. Nothing is rewritten: the ledger is only ever appended to.
    /// </summary>
    public static int Merge(string path, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var existing = File.Exists(path) ? ReadText(path) : string.Empty;
        var present = existing.Split('\n').Select(l => l.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
        var missing = lines.Where(l => l.Length > 0 && present.Add(l)).ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // A line cut short (a power cut while it was appended) stays on its own, and the replay counts it unreadable.
        if (existing.Length > 0 && existing[^1] != '\n')
        {
            writer.WriteLine();
        }

        foreach (var line in missing)
        {
            writer.WriteLine(line);
        }

        writer.Flush();
        file.Flush(flushToDisk: true);
        return missing.Count;
    }

    /// <summary>Appends <paramref name="ledger"/>, as a backup carries it, to the ledger at <paramref name="path"/> (<see cref="Merge(string, IEnumerable{string})"/>).</summary>
    public static int Merge(string path, byte[] ledger) => Merge(path, Lines(ledger));

    private static void AddLines(List<string> lines, HashSet<string> seen, string path)
    {
        foreach (var line in ReadLines(path))
        {
            if (seen.Add(line))
            {
                lines.Add(line);
            }
        }
    }

    /// <summary>The lines that end in a line feed, without it (or a carriage return before it), blank ones left out.</summary>
    private static List<string> CompleteLines(string text)
    {
        var parts = text.Split('\n');
        return [.. parts.Take(parts.Length - 1).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)];
    }

    /// <summary>The file's text, read while the ledger may be appended to.</summary>
    private static string ReadText(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static byte[] Join(List<string> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.Append(line).Append('\n');
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }
}
