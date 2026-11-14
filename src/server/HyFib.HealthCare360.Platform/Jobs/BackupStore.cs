using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>What a backup folder records about itself (<see cref="BackupStore.MarkerFileName"/>).</summary>
/// <param name="Format">The layout of the folder; a folder of a later format is refused.</param>
/// <param name="Installation">Which hospital's backups the folder holds: the fingerprint of the original field key.</param>
/// <param name="CreatedAt">When this version first wrote to the folder.</param>
public sealed record BackupStoreMarker(int Format, string Installation, DateTimeOffset CreatedAt);

/// <summary>What <see cref="BackupStore.Prune"/> removed, and why it did not look for unused documents, if it did not.</summary>
public sealed record BackupPruneResult(int ArchivesDeleted, int BlobsDeleted, long BytesFreed, int LeftoversDeleted, string? SweepSkipped);

/// <summary>The folder belongs to another installation or a later version, or another backup is using it.</summary>
public sealed class BackupStoreException(string message) : IOException(message);

/// <summary>
/// One backup folder: the primary <c>HealthCare360:Backup:Directory</c>, or the disk mounted at
/// <c>Backup:SecondaryDirectory</c> tonight. Each stored document is kept there once, byte for byte as the file store
/// wrote it (already encrypted with the field keys, which every archive escrows), named by the SHA-256 of those bytes.
/// Each archive lists the documents it needs in a <c>.refs</c> file beside it, sealed with a key derived from the original
/// field key, so documents no kept archive needs can be removed without opening any archive (which needs the recovery key
/// it was made with). See docs/design/incremental-backup.md.
/// <list type="bullet">
/// <item>Everything that writes or removes holds <see cref="Lock"/>, so whatever a crashed run left behind (a <c>.tmp</c>
/// document, a <c>.partial</c> archive, a list without its archive) is known to be garbage without looking at clocks.</item>
/// <item>A run copies the documents, then writes the list, then renames the archive into place: an archive in the folder
/// always has its documents and its list.</item>
/// <item>Unused documents are looked for only when every archive's list is there and verifies; otherwise none is removed.</item>
/// </list>
/// </summary>
internal sealed partial class BackupStore
{
    public const int Format = 1;
    public const string MarkerFileName = "hc360-store.json";
    public const string LockFileName = "hc360-store.lock";
    public const string BlobsFolder = "blobs";
    public const string BlobExtension = ".blob";
    public const string ReferencesExtension = ".refs";

    /// <summary>The purpose the key that seals the lists is derived for, from the original field key (it never changes).</summary>
    public const string ReferencesKeyPurpose = "backup-refs";

    private const string TempExtension = ".tmp";
    private const string PartialExtension = ".partial";
    private const string ReferencesHeader = "HC360REFS 1";

    /// <summary>Every stored copy carries this time, so a disk that leaves the building does not tell when each document was added.</summary>
    public static readonly DateTime BlobTimestamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly TimeProvider _time;
    private BackupStoreLock? _held;

    private BackupStore(string directory, BackupStoreMarker marker, TimeProvider time)
    {
        Directory = directory;
        Marker = marker;
        _time = time;
    }

    public string Directory { get; }

    public BackupStoreMarker Marker { get; }

    /// <summary>
    /// Opens the folder, marking it as this installation's the first time. Archives already there were made before
    /// documents were kept apart and need none, so each is given an empty list then: from then on an archive without a
    /// list keeps any document from being removed.
    /// </summary>
    /// <param name="installation">The fingerprint of the original field key, the same on every server restored from these backups.</param>
    /// <exception cref="BackupStoreException">The folder is another installation's, or of a later format.</exception>
    public static BackupStore Open(string directory, string installation, ReadOnlySpan<byte> referencesKey, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installation);
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));
        System.IO.Directory.CreateDirectory(full);
        var markerPath = Path.Combine(full, MarkerFileName);
        if (ReadMarker(markerPath) is { } existing)
        {
            return Check(new BackupStore(full, existing, time), installation);
        }

        var created = new BackupStoreMarker(Format, installation, time.GetUtcNow());
        var store = new BackupStore(full, created, time);
        foreach (var archive in store.ArchiveNames().Where(a => !File.Exists(store.ReferencesPath(a))))
        {
            store.WriteReferences(archive, [], referencesKey);
        }

        var temp = markerPath + "." + Guid.NewGuid().ToString("N") + TempExtension;
        WriteDurably(temp, JsonSerializer.SerializeToUtf8Bytes(created, Json));
        try
        {
            File.Move(temp, markerPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(markerPath))
        {
            // Another server marked it first: theirs is the one that counts.
            File.Delete(temp);
            return Check(new BackupStore(full, ReadMarker(markerPath)!, time), installation);
        }

        return store;
    }

    private static BackupStore Check(BackupStore store, string installation)
    {
        if (store.Marker.Format > Format)
        {
            throw new BackupStoreException(
                $"The backup folder {store.Directory} was set up by a later version of HC360 (format {store.Marker.Format}); this version writes format {Format}.");
        }

        if (!string.Equals(store.Marker.Installation, installation, StringComparison.Ordinal))
        {
            throw new BackupStoreException(
                $"The backup folder {store.Directory} holds another installation's backups ({store.Marker.Installation}, not {installation}). " +
                "Give this server a folder of its own: two installations in one folder would remove each other's documents.");
        }

        return store;
    }

    private static BackupStoreMarker? ReadMarker(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BackupStoreMarker>(File.ReadAllBytes(path), Json)
                ?? throw new BackupStoreException($"The backup folder's {MarkerFileName} is empty.");
        }
        catch (JsonException ex)
        {
            throw new BackupStoreException($"The backup folder's {MarkerFileName} cannot be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Takes the folder for one backup: writing documents, lists and archives, and removing them, all need it. On a NAS it is
    /// held through the share, so it also keeps out a server that is not in this hospital's database.
    /// </summary>
    /// <exception cref="BackupStoreException">Another backup holds it.</exception>
    public BackupStoreLock Lock()
    {
        var path = Path.Combine(Directory, LockFileName);
        try
        {
            var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _held = new BackupStoreLock(this, file);
            return _held;
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            throw new BackupStoreException($"Another backup is using the backup folder {Directory}: {ex.Message}");
        }
    }

    internal void Released(BackupStoreLock held)
    {
        if (ReferenceEquals(_held, held))
        {
            _held = null;
        }
    }

    private void RequireLock(BackupStoreLock held)
    {
        ArgumentNullException.ThrowIfNull(held);
        if (!ReferenceEquals(_held, held) || !held.IsHeld)
        {
            throw new InvalidOperationException($"The backup folder {Directory} is changed only while this store's lock is held.");
        }
    }

    /// <summary>The SHA-256 of a file, as a document's name in the folder (64 lowercase hex digits).</summary>
    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }

    public static bool IsBlobId(string? id) => id is not null && BlobIdPattern().IsMatch(id);

    /// <summary>Where the document named <paramref name="id"/> is kept: <c>blobs\ab\cd\abcd….blob</c>.</summary>
    public string PathOf(string id)
    {
        if (!IsBlobId(id))
        {
            throw new ArgumentException($"'{id}' is not a document name in a backup folder.", nameof(id));
        }

        return Path.Combine(Directory, BlobsFolder, id[..2], id[2..4], id + BlobExtension);
    }

    /// <summary>Whether the document is here in full. Its content is checked by verify, not here.</summary>
    public bool Contains(string id, long length)
    {
        var file = new FileInfo(PathOf(id));
        return file.Exists && file.Length == length;
    }

    /// <summary>
    /// Copies a document in unless it is here already. It is written beside its place, flushed to the disk and then renamed,
    /// so a document under its own name is always complete; a copy cut short (a power cut, an unplugged disk) is replaced.
    /// </summary>
    /// <returns>Whether it was copied.</returns>
    /// <exception cref="InvalidDataException">The file's content is not <paramref name="id"/>: it changed after it was hashed.</exception>
    public async Task<bool> AddAsync(BackupStoreLock held, string sourcePath, string id, CancellationToken ct)
    {
        RequireLock(held);
        var target = PathOf(id);
        var length = new FileInfo(sourcePath).Length;
        if (Contains(id, length))
        {
            return false;
        }

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N") + TempExtension;
        try
        {
            string written;
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                output.Flush(flushToDisk: true);
                written = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (!string.Equals(written, id, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{sourcePath} changed while it was being backed up.");
            }

            StampTimes(temp);
            File.Move(temp, target, overwrite: true);
            return true;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static void StampTimes(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetCreationTimeUtc(path, BlobTimestamp);
        }

        File.SetLastWriteTimeUtc(path, BlobTimestamp);
        File.SetLastAccessTimeUtc(path, BlobTimestamp);
    }

    /// <summary>The list beside an archive: <c>hc360-…-scheduled.hc360bak</c> → <c>hc360-…-scheduled.refs</c>.</summary>
    public string ReferencesPath(string archiveName) => Path.Combine(Directory, Path.ChangeExtension(CheckArchiveName(archiveName), ReferencesExtension));

    /// <summary>
    /// Writes the list of the documents an archive needs, sealed (HMAC-SHA256) together with the archive's name and this
    /// installation, so a list cannot be edited, nor moved to another archive, without the seal failing.
    /// </summary>
    public void WriteReferences(BackupStoreLock held, string archiveName, IEnumerable<string> ids, ReadOnlySpan<byte> key)
    {
        RequireLock(held);
        WriteReferences(archiveName, ids, key);
    }

    private void WriteReferences(string archiveName, IEnumerable<string> ids, ReadOnlySpan<byte> key)
    {
        var sorted = ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (sorted.FirstOrDefault(id => !IsBlobId(id)) is { } bad)
        {
            throw new ArgumentException($"'{bad}' is not a document name in a backup folder.", nameof(ids));
        }

        var body = new StringBuilder();
        body.Append(ReferencesHeader).Append('\n');
        body.Append("archive ").Append(CheckArchiveName(archiveName)).Append('\n');
        body.Append("installation ").Append(Marker.Installation).Append('\n');
        foreach (var id in sorted)
        {
            body.Append(id).Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(body.ToString());
        var text = body.Append("mac ").Append(Convert.ToHexStringLower(HMACSHA256.HashData(key, bytes))).Append('\n').ToString();
        var path = ReferencesPath(archiveName);
        var temp = path + "." + Guid.NewGuid().ToString("N") + TempExtension;
        WriteDurably(temp, Encoding.UTF8.GetBytes(text));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The documents an archive needs, from its list, or null when it has none.</summary>
    /// <exception cref="InvalidDataException">The list is damaged, edited, another archive's, or sealed with another key.</exception>
    public IReadOnlyList<string>? ReadReferences(string archiveName, ReadOnlySpan<byte> key)
    {
        var path = ReferencesPath(archiveName);
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path, Encoding.UTF8);
        var macAt = text.LastIndexOf("mac ", StringComparison.Ordinal);
        if (macAt <= 0 || text[macAt - 1] != '\n' || !text.EndsWith('\n'))
        {
            throw new InvalidDataException($"The list {Path.GetFileName(path)} is incomplete.");
        }

        var body = text[..macAt];
        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body));
        byte[] given;
        try
        {
            given = Convert.FromHexString(text[(macAt + 4)..^1]);
        }
        catch (FormatException)
        {
            throw new InvalidDataException($"The seal of the list {Path.GetFileName(path)} cannot be read.");
        }

        if (!CryptographicOperations.FixedTimeEquals(expected, given))
        {
            throw new InvalidDataException($"The list {Path.GetFileName(path)} does not verify: it was changed, or not made by this installation.");
        }

        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 3 || lines[0] != ReferencesHeader
            || lines[1] != "archive " + archiveName || lines[2] != "installation " + Marker.Installation)
        {
            throw new InvalidDataException($"The list {Path.GetFileName(path)} is not the list of {archiveName}.");
        }

        var ids = lines[3..];
        if (ids.FirstOrDefault(id => !IsBlobId(id)) is { } bad)
        {
            throw new InvalidDataException($"The list {Path.GetFileName(path)} names '{bad}', which is not a document name.");
        }

        return ids;
    }

    /// <summary>
    /// Deletes the archives older than <paramref name="retentionDays"/> (by the date in their name, never the file's times),
    /// always keeping the newest; removes what a crashed run left; then removes the documents no remaining archive's list
    /// names, unless an archive has no list or its list does not verify, when none is removed.
    /// </summary>
    public BackupPruneResult Prune(BackupStoreLock held, int retentionDays, ReadOnlySpan<byte> key)
    {
        RequireLock(held);
        var cutoff = _time.GetUtcNow() - TimeSpan.FromDays(Math.Max(retentionDays, 1));

        // Archives older than the cutoff, by their name, except the newest: a disk that was away longer than the backup
        // period keeps its last archive until a newer one is on it.
        var dated = ArchiveNames().Select(name => (Name: name, Date: ArchiveDate(name))).Where(a => a.Date is not null).ToList();
        var newest = dated.Count == 0 ? (DateTimeOffset?)null : dated.Max(a => a.Date);
        var archivesDeleted = 0;
        foreach (var (name, date) in dated.Where(a => a.Date < cutoff && a.Date != newest))
        {
            File.Delete(Path.Combine(Directory, name));
            File.Delete(ReferencesPath(name));
            archivesDeleted++;
        }

        // Left by a run that stopped: every run holds the lock, and this one does now, so none of these is in use.
        var leftovers = 0;
        foreach (var partial in System.IO.Directory.EnumerateFiles(Directory, "*" + BackupService.FileExtension + PartialExtension))
        {
            File.Delete(partial);
            leftovers++;
        }

        foreach (var temp in System.IO.Directory.EnumerateFiles(Directory, "*" + TempExtension))
        {
            File.Delete(temp);
            leftovers++;
        }

        var archives = ArchiveNames().ToHashSet(StringComparer.Ordinal);
        foreach (var list in System.IO.Directory.EnumerateFiles(Directory, "*" + ReferencesExtension))
        {
            if (!archives.Contains(Path.ChangeExtension(Path.GetFileName(list), BackupService.FileExtension)))
            {
                File.Delete(list);
                leftovers++;
            }
        }

        var blobs = Path.Combine(Directory, BlobsFolder);
        if (System.IO.Directory.Exists(blobs))
        {
            foreach (var temp in System.IO.Directory.EnumerateFiles(blobs, "*" + TempExtension, SearchOption.AllDirectories))
            {
                File.Delete(temp);
                leftovers++;
            }
        }

        // Every archive's list, or nothing is removed: a document an archive needs must never go because its list is lost.
        var needed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var archive in archives.Order(StringComparer.Ordinal))
        {
            IReadOnlyList<string>? ids;
            try
            {
                ids = ReadReferences(archive, key);
            }
            catch (InvalidDataException ex)
            {
                return new BackupPruneResult(archivesDeleted, 0, 0, leftovers, ex.Message + " No stored document was removed.");
            }

            if (ids is null)
            {
                return new BackupPruneResult(archivesDeleted, 0, 0, leftovers,
                    $"{archive} has no list of the documents it needs ({Path.GetFileName(ReferencesPath(archive))}), so no stored document was removed. " +
                    "Restore the list from another copy, or delete the archive if it is not needed.");
            }

            needed.UnionWith(ids);
        }

        var blobsDeleted = 0;
        long bytesFreed = 0;
        if (System.IO.Directory.Exists(blobs))
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(blobs, "*" + BlobExtension, SearchOption.AllDirectories))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (IsBlobId(id) && !needed.Contains(id) && string.Equals(path, PathOf(id), StringComparison.Ordinal))
                {
                    bytesFreed += new FileInfo(path).Length;
                    File.Delete(path);
                    blobsDeleted++;
                }
            }
        }

        return new BackupPruneResult(archivesDeleted, blobsDeleted, bytesFreed, leftovers, null);
    }

    /// <summary>
    /// Whether a disk's file system is unsuited to a backup folder: FAT and exFAT have no journal (a power cut can damage
    /// the folder), and exFAT's large clusters waste tens of kilobytes on every stored document. Format the disk NTFS.
    /// </summary>
    public static bool UnsuitableFileSystem(string? driveFormat) =>
        driveFormat is not null && (driveFormat.Equals("exFAT", StringComparison.OrdinalIgnoreCase)
            || driveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase)
            || driveFormat.Equals("vfat", StringComparison.OrdinalIgnoreCase));

    /// <summary>The archive file names in the folder (not partial ones).</summary>
    public IEnumerable<string> ArchiveNames() =>
        System.IO.Directory.EnumerateFiles(Directory, "*" + BackupService.FileExtension).Select(Path.GetFileName).OfType<string>();

    /// <summary>When an archive was made, from its name (<c>hc360-yyyyMMdd-HHmmss-kind.hc360bak</c>, UTC); null for another name.</summary>
    public static DateTimeOffset? ArchiveDate(string archiveName)
    {
        var match = ArchiveNamePattern().Match(archiveName);
        return match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? new DateTimeOffset(date, TimeSpan.Zero)
            : null;
    }

    private static string CheckArchiveName(string archiveName)
    {
        // The same rule on every system: '\' and ':' are separators on Windows even where this runs on Linux.
        if (string.IsNullOrEmpty(archiveName) || Path.GetFileName(archiveName) != archiveName
            || archiveName.IndexOfAny(['/', '\\', ':']) >= 0 || archiveName.StartsWith('.')
            || !archiveName.EndsWith(BackupService.FileExtension, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{archiveName}' is not the name of an archive in a backup folder.", nameof(archiveName));
        }

        return archiveName;
    }

    private static void WriteDurably(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex BlobIdPattern();

    [GeneratedRegex(@"^hc360-(\d{8}-\d{6})-[a-z]+\.hc360bak$")]
    private static partial Regex ArchiveNamePattern();
}

/// <summary>The hold on a backup folder (<see cref="BackupStore.Lock"/>); released when disposed.</summary>
internal sealed class BackupStoreLock : IDisposable
{
    private readonly BackupStore _store;
    private FileStream? _file;

    internal BackupStoreLock(BackupStore store, FileStream file)
    {
        _store = store;
        _file = file;
    }

    public bool IsHeld => _file is not null;

    public void Dispose()
    {
        if (_file is null)
        {
            return;
        }

        _file.Dispose();
        _file = null;
        _store.Released(this);
    }
}
