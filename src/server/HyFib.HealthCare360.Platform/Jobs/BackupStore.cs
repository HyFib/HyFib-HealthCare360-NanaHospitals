using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Hosting;

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
/// <item>Archives go by grandfather-father-son retention (<see cref="BackupRetention"/>), and a document goes only once no
/// archive kept in the folder lists it (<see cref="Prune"/>).</item>
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

    /// <summary>
    /// Present in a folder HC360 may add files to but not delete or rename (an append-only or WORM share): its documents are
    /// kept in generations, never removed by HC360 (docs/design/incremental-backup.md, section 5.6). A file, not a field of
    /// the marker, because such a folder lets a file be added but not the marker rewritten.
    /// </summary>
    public const string AppendOnlyFlagFile = "hc360-append-only";

    /// <summary>How long a generation of an append-only folder is used before the next starts with every document again.</summary>
    public const int GenerationDays = 7;

    public const string GenerationPrefix = "gen-";

    /// <summary>The purpose the key that seals the lists is derived for, from the original field key (it never changes).</summary>
    public const string ReferencesKeyPurpose = "backup-refs";

    private const string TempExtension = ".tmp";
    private const string BadExtension = ".bad";
    private const string PartialExtension = ".partial";
    private const string ReferencesHeader = "HC360REFS 1";

    /// <summary>Every stored copy carries this time, so a disk that leaves the building does not tell when each document was added.</summary>
    public static readonly DateTime BlobTimestamp = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly TimeProvider _time;
    private BackupStoreLock? _held;

    private BackupStore(string directory, BackupStoreMarker marker, TimeProvider time, bool appendOnly)
    {
        Directory = directory;
        Marker = marker;
        _time = time;
        AppendOnly = appendOnly;
        Generation = appendOnly ? CurrentGeneration(directory, time.GetUtcNow()) : null;
    }

    public string Directory { get; }

    public BackupStoreMarker Marker { get; }

    /// <summary>Whether HC360 may only add files here (<see cref="AppendOnlyFlagFile"/>): documents go into <see cref="Generation"/>, and nothing is removed.</summary>
    public bool AppendOnly { get; }

    /// <summary>The generation tonight's documents go into in an append-only folder (<c>gen-yyyyMMdd</c>); null otherwise.</summary>
    public string? Generation { get; }

    /// <summary>Where the documents are kept: the folder itself, or tonight's generation in an append-only folder.</summary>
    private string BlobRoot => Generation is null ? Directory : Path.Combine(Directory, Generation);

    /// <summary>
    /// Opens the folder, marking it as this installation's the first time. Archives already there were made before
    /// documents were kept apart and need none, so each is given an empty list then: from then on an archive without a
    /// list keeps any document from being removed.
    /// </summary>
    /// <param name="installation">The fingerprint of the original field key, the same on every server restored from these backups.</param>
    /// <param name="appendOnly">The folder is known to be append-only (<c>HealthCare360:Backup:SecondaryAppendOnly</c>).</param>
    /// <exception cref="BackupStoreException">The folder is another installation's, or of a later format.</exception>
    public static BackupStore Open(string directory, string installation, ReadOnlySpan<byte> referencesKey, TimeProvider time, bool appendOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installation);
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));
        System.IO.Directory.CreateDirectory(full);
        var markerPath = Path.Combine(full, MarkerFileName);
        if (ReadMarker(markerPath) is { } existing)
        {
            return Check(new BackupStore(full, existing, time, MarkAppendOnly(full, appendOnly)), installation);
        }

        // The first use of a folder finds out whether it lets HC360 rename and delete; one that does not is append-only.
        appendOnly = MarkAppendOnly(full, appendOnly || !CanRenameAndDelete(full));
        var created = new BackupStoreMarker(Format, installation, time.GetUtcNow());
        var store = new BackupStore(full, created, time, appendOnly);
        foreach (var archive in store.ArchiveNames().Where(a => !File.Exists(store.ReferencesPath(a))))
        {
            store.WriteReferences(archive, [], referencesKey);
        }

        try
        {
            WriteDurably(markerPath, JsonSerializer.SerializeToUtf8Bytes(created, Json));
        }
        catch (IOException) when (File.Exists(markerPath))
        {
            // Another server marked it first: theirs is the one that counts.
            return Check(new BackupStore(full, ReadMarker(markerPath)!, time, appendOnly), installation);
        }

        return store;
    }

    /// <summary>
    /// Whether the folder is append-only, recording it when it is newly known to be: the setting says so, a probe found it,
    /// or a delete was refused (<see cref="Prune"/>). Once recorded it stays so.
    /// </summary>
    private static bool MarkAppendOnly(string directory, bool appendOnly)
    {
        var flag = Path.Combine(directory, AppendOnlyFlagFile);
        if (File.Exists(flag))
        {
            return true;
        }

        if (appendOnly)
        {
            WriteDurably(flag, "HC360 may add files to this folder but not delete or rename them: its backups keep their documents in weekly generations.\n"u8.ToArray());
        }

        return appendOnly;
    }

    private static bool CanRenameAndDelete(string directory)
    {
        var probe = Path.Combine(directory, $"hc360-probe-{Guid.NewGuid():N}{TempExtension}");
        try
        {
            WriteDurably(probe, [0]);
            File.Move(probe, probe + "2");
            File.Delete(probe + "2");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The generation to keep tonight's documents in: the newest one started less than <see cref="GenerationDays"/> days
    /// ago, or a new one named for today. A generation is never taken up again after a longer gap (F25).
    /// </summary>
    /// <param name="suffix">For generations recorded as files (the off-site ledger): the end of their names after the date.</param>
    internal static string CurrentGeneration(string directory, DateTimeOffset now, string? suffix = null)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var names = !System.IO.Directory.Exists(directory) ? []
            : suffix is null
                ? System.IO.Directory.EnumerateDirectories(directory, GenerationPrefix + "*").Select(Path.GetFileName)
                : System.IO.Directory.EnumerateFiles(directory, GenerationPrefix + "*" + suffix).Select(f => Path.GetFileName(f)[..^suffix.Length]);
        var newest = names.Select(GenerationDate).Where(d => d is not null && d <= today).Max();
        return newest is { } started && today.DayNumber - started.DayNumber < GenerationDays
            ? GenerationPrefix + started.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : GenerationPrefix + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    internal static DateOnly? GenerationDate(string? name) =>
        name is not null && name.StartsWith(GenerationPrefix, StringComparison.Ordinal)
        && DateOnly.TryParseExact(name[GenerationPrefix.Length..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

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
            FileStream file;
            try
            {
                file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (UnauthorizedAccessException) when (File.Exists(path))
            {
                // An append-only share lets HC360 create the lock file once, then only read it; reading holds it all the same.
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }

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

        return Path.Combine(BlobRoot, BlobsFolder, id[..2], id[2..4], id + BlobExtension);
    }

    /// <summary>Whether the document is here in full (in tonight's generation, in an append-only folder). Its content is checked by verify, not here.</summary>
    public bool Contains(string id, long length) => FindCopy(id, length) is not null;

    /// <summary>
    /// A copy of the document here of the right length: <c>&lt;id&gt;.blob</c>, or in an append-only folder one written beside
    /// a copy cut short (<c>&lt;id&gt;.1.blob</c>, …) and not marked bad.
    /// </summary>
    public string? FindCopy(string id, long length)
    {
        var first = PathOf(id);
        if (Good(first, length))
        {
            return first;
        }

        if (!AppendOnly || !System.IO.Directory.Exists(Path.GetDirectoryName(first)))
        {
            return null;
        }

        return System.IO.Directory.EnumerateFiles(Path.GetDirectoryName(first)!, id + ".*" + BlobExtension)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault(path => Good(path, length));

        static bool Good(string path, long length)
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length == length && !File.Exists(path + BadExtension);
        }
    }

    /// <summary>
    /// Whether the folder has a copy of the document not marked bad, of whatever length: under <c>blobs\</c>, or in an
    /// append-only folder in any generation. Its content is checked by reading it back (<see cref="CopiesInShare"/>).
    /// </summary>
    public bool HasCopy(string id) =>
        AppendOnly ? BackupBlobs.Candidates(Directory, id).Any(path => !File.Exists(path + BadExtension)) : File.Exists(PathOf(id));

    /// <summary>
    /// The copies of documents in the folder, in every generation, whose names start with two hex digits that fall in share
    /// <paramref name="share"/> of <paramref name="shares"/>: read back one share a night, every copy is read back once every
    /// <paramref name="shares"/> nights. Copies marked bad, and unfinished ones, are left out.
    /// </summary>
    public IEnumerable<(string Id, string Path)> CopiesInShare(int share, int shares)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(shares);
        var roots = new List<string> { Path.Combine(Directory, BlobsFolder) };
        roots.AddRange(System.IO.Directory.EnumerateDirectories(Directory, GenerationPrefix + "*").Order(StringComparer.Ordinal)
            .Select(generation => Path.Combine(generation, BlobsFolder)));
        foreach (var root in roots.Where(System.IO.Directory.Exists))
        {
            foreach (var first in System.IO.Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(first);
                if (name.Length != 2 || !byte.TryParse(name, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) || value % shares != share)
                {
                    continue;
                }

                foreach (var path in System.IO.Directory.EnumerateFiles(first, "*" + BlobExtension, SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    // <id>.blob, or <id>.<n>.blob beside a copy cut short in an append-only folder.
                    var id = Path.GetFileName(path).Split('.')[0];
                    if (IsBlobId(id) && id.StartsWith(name, StringComparison.Ordinal) && !File.Exists(path + BadExtension))
                    {
                        yield return (id, path);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Takes a copy of a document out of use, once reading it back found it damaged: deleted from a folder HC360 may delete
    /// from; marked bad in an append-only folder, which keeps it, so it is never taken for a good copy (<see cref="FindCopy"/>).
    /// </summary>
    public void Discard(BackupStoreLock held, string path)
    {
        RequireLock(held);
        if (!Path.GetFullPath(path).StartsWith(Directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !Path.GetFileName(path).EndsWith(BlobExtension, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{path}' is not a copy of a document in {Directory}.", nameof(path));
        }

        if (!AppendOnly)
        {
            File.Delete(path);
        }
        else if (!File.Exists(path + BadExtension))
        {
            WriteDurably(path + BadExtension, []);
        }
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
        if (AppendOnly)
        {
            await AddWithoutRenameAsync(sourcePath, id, ct);
            return true;
        }

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

    /// <summary>
    /// In an append-only folder a copy is written straight under its name, never renamed and never given a time (a share
    /// that removes files by date would remove one dated 2000 at once). It is read back; one that does not match, or a copy
    /// cut short by a crash, cannot be deleted, so it is marked bad and the next copy goes beside it.
    /// </summary>
    private async Task AddWithoutRenameAsync(string sourcePath, string id, CancellationToken ct)
    {
        var first = PathOf(id);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var target = Next(first, id);
            string written;
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
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
                WriteDurably(target + BadExtension, []);
                throw new InvalidDataException($"{sourcePath} changed while it was being backed up.");
            }

            if (string.Equals(await HashFileAsync(target, ct), id, StringComparison.Ordinal))
            {
                return;
            }

            WriteDurably(target + BadExtension, []);
        }

        throw new IOException($"The copy of a document could not be written correctly to {Directory} three times: the disk may be failing.");

        static string Next(string first, string id)
        {
            if (!File.Exists(first))
            {
                return first;
            }

            for (var n = 1; ; n++)
            {
                var candidate = Path.Combine(Path.GetDirectoryName(first)!, $"{id}.{n}{BlobExtension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
    }

    /// <summary>
    /// Copies an archive of another backup folder here, with what a restore from this folder alone needs: the documents it
    /// lists that are not here yet, from <paramref name="source"/> (each hashed as it is read, so a copy damaged there is
    /// refused), then its list, then the archive itself, which must hash to <paramref name="sha256"/> as it is read. The
    /// archive is written beside its place and renamed in once complete, so an archive here always has its documents and its
    /// list; in an append-only folder it is written once under its name.
    /// </summary>
    /// <param name="readBack">Read the archive's copy back too, so one the disk did not write as it was sent is not counted.</param>
    /// <param name="checkpoint">Called at each step, for the tests that stop a backup as a crash would.</param>
    /// <returns>Why the archive was not copied (its copy removed where the folder allows); null once it is copied.</returns>
    /// <exception cref="InvalidDataException">A document's copy in <paramref name="source"/> is not the document it is named for.</exception>
    public async Task<string?> CopyArchiveAsync(BackupStoreLock held, BackupStore source, string archive, string sha256, IReadOnlyList<string> ids, byte[] key,
        bool readBack, Action<string>? checkpoint, CancellationToken ct)
    {
        RequireLock(held);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            await AddAsync(held, source.PathOf(id), id, ct);
            checkpoint?.Invoke("secondary document copied");
        }

        var name = CheckArchiveName(Path.GetFileName(archive));
        WriteReferences(held, name, ids, key);
        checkpoint?.Invoke("secondary list written");

        var target = Path.Combine(Directory, name);
        var temp = AppendOnly ? target : target + PartialExtension;
        if (!AppendOnly)
        {
            // Left by a copy that stopped: this one holds the lock, so it is nobody's.
            File.Delete(temp);
        }

        string read;
        await using (var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }

            output.Flush(flushToDisk: true);
            read = Convert.ToHexString(hash.GetHashAndReset());
        }

        var problem = !string.Equals(read, sha256, StringComparison.OrdinalIgnoreCase)
            ? $"{name} on the server no longer matches the SHA-256 it was checked with when it was made: the server's disk may be failing."
            : readBack && !string.Equals(await HashFileAsync(temp, ct), sha256, StringComparison.OrdinalIgnoreCase)
                ? $"The copy of {name} does not read back as it was written: the disk may be failing."
                : null;
        if (problem is not null)
        {
            if (!AppendOnly)
            {
                File.Delete(temp);
            }

            return problem;
        }

        if (!AppendOnly)
        {
            File.Move(temp, target, overwrite: true);
        }

        return null;
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
        if (AppendOnly)
        {
            // Nothing is removed from an append-only folder, so a list is only ever read by a restore, which does not use it.
            WriteDurably(path, Encoding.UTF8.GetBytes(text));
            return;
        }

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
    /// Deletes, with their lists, the archives grandfather-father-son retention no longer keeps (<see cref="BackupRetention"/>:
    /// by the date in their names, never the files' times, and never the newest full backup); removes what a crashed run
    /// left; then removes the documents no remaining archive's list names, unless an archive has no list or its list does
    /// not verify, when none is removed. A document a kept weekly or monthly backup needs stays as long as that backup.
    /// </summary>
    /// <param name="zone">The hospital's time zone, whose days, weeks and months retention counts.</param>
    /// <exception cref="InvalidOperationException">The folder is append-only: nothing is removed from it by HC360.</exception>
    /// <exception cref="UnauthorizedAccessException">A delete was refused: the folder is then recorded as append-only (F24).</exception>
    public BackupPruneResult Prune(BackupStoreLock held, BackupRetentionOptions retention, TimeZoneInfo zone, ReadOnlySpan<byte> key)
    {
        RequireLock(held);
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(zone);
        if (AppendOnly)
        {
            throw new InvalidOperationException($"{Directory} is append-only: HC360 removes nothing from it.");
        }

        try
        {
            return PruneDeletable(retention, zone, key);
        }
        catch (UnauthorizedAccessException)
        {
            // A folder that refuses a delete is append-only after all (a WORM share that allowed it for a while): from the next
            // backup its documents go into generations, which its own retention cannot break.
            MarkAppendOnly(Directory, appendOnly: true);
            throw;
        }
    }

    private BackupPruneResult PruneDeletable(BackupRetentionOptions retention, TimeZoneInfo zone, ReadOnlySpan<byte> key)
    {
        // The archives retention no longer keeps, by their names (other names are never deleted). The newest full backup is
        // always kept: a disk that was away longer than the longest period keeps its last one until a newer one is on it.
        var archivesDeleted = 0;
        foreach (var expired in BackupRetention.ToRemove(BackupFile.In(Directory), retention, zone, _time.GetUtcNow()))
        {
            var name = Path.GetFileName(expired.Path);
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

        // Only the store's own temporary files: other features write into the secondary folder too (the erasure ledger's
        // and the number series' mirrors).
        foreach (var temp in System.IO.Directory.EnumerateFiles(Directory, "*" + ReferencesExtension + ".*" + TempExtension)
            .Concat(System.IO.Directory.EnumerateFiles(Directory, "hc360-probe-*" + TempExtension + "*")))
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
