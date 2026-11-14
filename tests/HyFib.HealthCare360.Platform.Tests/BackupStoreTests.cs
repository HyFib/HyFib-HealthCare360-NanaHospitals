using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Jobs;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The backup folder that keeps each stored document once (docs/design/incremental-backup.md). The F numbers are the
/// failure model's rows in that document.
/// </summary>
public sealed class BackupStoreTests : IDisposable
{
    private const string Installation = "A1B2C3D4E5F60718";
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(new DateTimeOffset(2027, 3, 15, 1, 30, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Folder => Path.Combine(_root, "backups");

    private BackupStore Open(string? installation = null, byte[]? key = null) =>
        BackupStore.Open(Folder, installation ?? Installation, key ?? Key, _time);

    private string Document(string name, string content)
    {
        var path = Path.Combine(_root, "files", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Id(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string ArchiveName(DateTimeOffset at) => $"hc360-{at:yyyyMMdd-HHmmss}-scheduled{BackupService.FileExtension}";

    /// <summary>What a run leaves: the documents, then the list, then the archive (the archive's content does not matter here).</summary>
    private async Task<string> BackUpAsync(BackupStore store, BackupStoreLock held, DateTimeOffset at, params string[] documents)
    {
        var ids = new List<string>();
        foreach (var path in documents)
        {
            var id = await BackupStore.HashFileAsync(path, CancellationToken.None);
            await store.AddAsync(held, path, id, CancellationToken.None);
            ids.Add(id);
        }

        var name = ArchiveName(at);
        store.WriteReferences(held, name, ids, Key);
        File.WriteAllText(Path.Combine(store.Directory, name), "archive");
        return name;
    }

    private int BlobCount() =>
        Directory.Exists(Path.Combine(Folder, BackupStore.BlobsFolder))
            ? Directory.EnumerateFiles(Path.Combine(Folder, BackupStore.BlobsFolder), "*" + BackupStore.BlobExtension, SearchOption.AllDirectories).Count()
            : 0;

    [Fact]
    public void A_new_folder_is_marked_as_this_installations_and_another_installation_is_refused()
    {
        var store = Open();
        store.Marker.Installation.ShouldBe(Installation);
        store.Marker.CreatedAt.ShouldBe(_time.GetUtcNow());
        File.Exists(Path.Combine(Folder, BackupStore.MarkerFileName)).ShouldBeTrue();

        Open().Marker.ShouldBe(store.Marker);

        // F13: a training server or a drill pointed at the hospital's folder would remove the hospital's documents.
        var refused = Should.Throw<BackupStoreException>(() => Open("FFFFFFFFFFFFFFFF"));
        refused.Message.ShouldContain("another installation");
    }

    [Fact]
    public void A_folder_set_up_by_a_later_version_is_refused()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, BackupStore.MarkerFileName),
            $$"""{ "format": {{BackupStore.Format + 1}}, "installation": "{{Installation}}", "createdAt": "2027-01-01T00:00:00Z" }""");

        Should.Throw<BackupStoreException>(() => Open()).Message.ShouldContain("later version");
    }

    [Fact]
    public async Task A_document_is_kept_once_byte_for_byte_under_its_hash_with_no_date_on_it()
    {
        var store = Open();
        var path = Document("2027/03/one.bin", "encrypted document");
        var id = await BackupStore.HashFileAsync(path, CancellationToken.None);
        id.ShouldBe(Id("encrypted document"));

        using var held = store.Lock();
        (await store.AddAsync(held, path, id, CancellationToken.None)).ShouldBeTrue();
        (await store.AddAsync(held, path, id, CancellationToken.None)).ShouldBeFalse();

        var blob = store.PathOf(id);
        blob.ShouldBe(Path.Combine(Folder, "blobs", id[..2], id[2..4], id + ".blob"));
        File.ReadAllText(blob).ShouldBe("encrypted document");
        store.Contains(id, new FileInfo(path).Length).ShouldBeTrue();
        File.GetLastWriteTimeUtc(blob).ShouldBe(BackupStore.BlobTimestamp);
        Directory.EnumerateFiles(Folder, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_document_that_changed_after_it_was_hashed_is_not_kept_under_the_old_name()
    {
        var store = Open();
        var path = Document("one.bin", "first");
        var id = await BackupStore.HashFileAsync(path, CancellationToken.None);
        File.WriteAllText(path, "moved to a new key");

        using var held = store.Lock();
        await Should.ThrowAsync<InvalidDataException>(() => store.AddAsync(held, path, id, CancellationToken.None));
        File.Exists(store.PathOf(id)).ShouldBeFalse();
        Directory.EnumerateFiles(Folder, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_copy_cut_short_is_replaced_by_the_next_copy()
    {
        // F5: a disk unplugged during a copy. A copy is renamed only once complete, but a file system without a journal
        // can still leave a short file under the name after a power cut.
        var store = Open();
        var path = Document("one.bin", "the whole document");
        var id = Id("the whole document");
        Directory.CreateDirectory(Path.GetDirectoryName(store.PathOf(id))!);
        File.WriteAllText(store.PathOf(id), "the wh");

        using var held = store.Lock();
        store.Contains(id, new FileInfo(path).Length).ShouldBeFalse();
        (await store.AddAsync(held, path, id, CancellationToken.None)).ShouldBeTrue();
        File.ReadAllText(store.PathOf(id)).ShouldBe("the whole document");
    }

    [Fact]
    public async Task Only_one_backup_holds_the_folder_and_nothing_changes_it_without_the_hold()
    {
        // F12: two servers backing up into one shared folder.
        var store = Open();
        var other = Open();
        var path = Document("one.bin", "x");

        using (var held = store.Lock())
        {
            Should.Throw<BackupStoreException>(() => other.Lock()).Message.ShouldContain("Another backup");
        }

        using var mine = other.Lock();
        mine.Dispose();
        await Should.ThrowAsync<InvalidOperationException>(() => other.AddAsync(mine, path, Id("x"), CancellationToken.None));
        using var current = store.Lock();
        Should.Throw<InvalidOperationException>(() => other.Prune(current, 30, Key));
    }

    [Fact]
    public void A_list_reads_back_and_any_change_to_it_is_caught()
    {
        var store = Open();
        var name = ArchiveName(_time.GetUtcNow());
        var ids = new[] { Id("b"), Id("a"), Id("b") };
        using var held = store.Lock();
        store.WriteReferences(held, name, ids, Key);

        store.ReadReferences(name, Key).ShouldBe(ids.Distinct().Order(StringComparer.Ordinal));
        store.ReadReferences(ArchiveName(_time.GetUtcNow().AddDays(1)), Key).ShouldBeNull();

        // F15: sealed with another key, or edited.
        Should.Throw<InvalidDataException>(() => store.ReadReferences(name, RandomNumberGenerator.GetBytes(32)));
        var path = store.ReferencesPath(name);
        var text = File.ReadAllText(path);
        File.WriteAllText(path, text.Replace(Id("a"), Id("c"), StringComparison.Ordinal));
        Should.Throw<InvalidDataException>(() => store.ReadReferences(name, Key));
        File.WriteAllText(path, text.Replace(Id("a") + "\n", "", StringComparison.Ordinal));
        Should.Throw<InvalidDataException>(() => store.ReadReferences(name, Key));

        // Moved beside another archive: the archive's name is sealed in it.
        File.WriteAllText(path, text);
        var other = ArchiveName(_time.GetUtcNow().AddDays(-1));
        File.Copy(path, store.ReferencesPath(other));
        Should.Throw<InvalidDataException>(() => store.ReadReferences(other, Key)).Message.ShouldContain("is not the list of " + other);

        Should.Throw<ArgumentException>(() => store.WriteReferences(held, name, ["../../outside"], Key));
        Should.Throw<ArgumentException>(() => store.WriteReferences(held, "..\\x.hc360bak", [], Key));
    }

    [Fact]
    public async Task Archives_older_than_the_backup_period_go_with_their_lists_by_the_date_in_their_name()
    {
        var store = Open();
        using var held = store.Lock();
        var now = _time.GetUtcNow();
        var expired = await BackUpAsync(store, held, now.AddDays(-31));
        var kept = await BackUpAsync(store, held, now.AddDays(-29));
        var latest = await BackUpAsync(store, held, now);

        // F19: the files' own times say nothing (a copy resets them, a clock can be wrong).
        File.SetLastWriteTimeUtc(Path.Combine(Folder, expired), now.UtcDateTime);
        File.SetLastWriteTimeUtc(Path.Combine(Folder, kept), now.UtcDateTime.AddYears(-5));
        File.WriteAllText(Path.Combine(Folder, "copied-by-hand.hc360bak"), "not ours to date");

        var result = store.Prune(held, 30, Key);

        result.ArchivesDeleted.ShouldBe(1);
        File.Exists(Path.Combine(Folder, expired)).ShouldBeFalse();
        File.Exists(store.ReferencesPath(expired)).ShouldBeFalse();
        File.Exists(Path.Combine(Folder, kept)).ShouldBeTrue();
        File.Exists(Path.Combine(Folder, latest)).ShouldBeTrue();
        File.Exists(Path.Combine(Folder, "copied-by-hand.hc360bak")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_disk_away_longer_than_the_backup_period_keeps_its_newest_archive()
    {
        // F4: the newest archive on a disk is its only restorable one until a newer one is copied.
        var store = Open();
        using var held = store.Lock();
        var path = Document("one.bin", "document");
        await BackUpAsync(store, held, _time.GetUtcNow().AddDays(-60), path);
        var newest = await BackUpAsync(store, held, _time.GetUtcNow().AddDays(-45), path);

        var result = store.Prune(held, 30, Key);

        result.ArchivesDeleted.ShouldBe(1);
        store.ArchiveNames().ShouldBe([newest]);
        result.BlobsDeleted.ShouldBe(0);
        BlobCount().ShouldBe(1);
    }

    [Fact]
    public async Task A_document_goes_once_no_kept_archive_needs_it()
    {
        // F14: a document erased, purged or moved to a new key leaves the backups when the last archive with it expires.
        var store = Open();
        using var held = store.Lock();
        var kept = Document("kept.bin", "kept document");
        var erased = Document("erased.bin", "erased document");
        var start = _time.GetUtcNow();
        await BackUpAsync(store, held, start, kept, erased);
        File.Delete(erased);
        await BackUpAsync(store, held, start.AddDays(1), kept);

        _time.Advance(TimeSpan.FromDays(30));
        var before = store.Prune(held, 30, Key);
        before.BlobsDeleted.ShouldBe(0);
        store.Contains(Id("erased document"), "erased document".Length).ShouldBeTrue();

        _time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        var after = store.Prune(held, 30, Key);

        after.ArchivesDeleted.ShouldBe(1);
        after.BlobsDeleted.ShouldBe(1);
        after.BytesFreed.ShouldBe("erased document".Length);
        after.SweepSkipped.ShouldBeNull();
        File.Exists(store.PathOf(Id("erased document"))).ShouldBeFalse();
        store.Contains(Id("kept document"), "kept document".Length).ShouldBeTrue();
    }

    [Fact]
    public async Task No_document_is_removed_while_an_archive_has_no_list_or_a_list_that_does_not_verify()
    {
        // F15: a list lost or edited must never let a needed document go.
        var store = Open();
        using var held = store.Lock();
        var path = Document("one.bin", "needed");
        var archive = await BackUpAsync(store, held, _time.GetUtcNow(), path);
        var later = await BackUpAsync(store, held, _time.GetUtcNow().AddHours(1));
        File.Delete(store.ReferencesPath(archive));

        var missing = store.Prune(held, 30, Key);
        missing.BlobsDeleted.ShouldBe(0);
        missing.SweepSkipped.ShouldNotBeNull();
        missing.SweepSkipped.ShouldContain(archive);
        BlobCount().ShouldBe(1);

        store.WriteReferences(held, archive, [Id("needed")], Key);
        File.AppendAllText(store.ReferencesPath(later), Id("other") + "\n");
        var edited = store.Prune(held, 30, Key);
        edited.BlobsDeleted.ShouldBe(0);
        edited.SweepSkipped.ShouldNotBeNull();
        BlobCount().ShouldBe(1);
    }

    [Fact]
    public async Task What_a_crashed_run_left_is_removed_and_nothing_an_archive_needs()
    {
        // F1: a run stopped while copying documents, before its list or its archive.
        var store = Open();
        using var held = store.Lock();
        var path = Document("one.bin", "needed");
        var archive = await BackUpAsync(store, held, _time.GetUtcNow().AddDays(-1), path);

        var orphan = Document("orphan.bin", "copied, then the power went");
        await store.AddAsync(held, orphan, Id("copied, then the power went"), CancellationToken.None);
        var blobTemp = store.PathOf(Id("half")) + ".0123.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(blobTemp)!);
        File.WriteAllText(blobTemp, "ha");
        var crashed = ArchiveName(_time.GetUtcNow());
        store.WriteReferences(held, crashed, [Id("copied, then the power went")], Key);
        File.WriteAllText(Path.Combine(Folder, crashed + ".partial"), "half an archive");

        var result = store.Prune(held, 30, Key);

        result.LeftoversDeleted.ShouldBe(3);
        result.BlobsDeleted.ShouldBe(1);
        File.Exists(blobTemp).ShouldBeFalse();
        File.Exists(store.ReferencesPath(crashed)).ShouldBeFalse();
        File.Exists(Path.Combine(Folder, crashed + ".partial")).ShouldBeFalse();
        store.ArchiveNames().ShouldBe([archive]);
        store.Contains(Id("needed"), "needed".Length).ShouldBeTrue();
        File.Exists(store.PathOf(Id("copied, then the power went"))).ShouldBeFalse();
    }

    [Fact]
    public async Task Archives_made_before_the_folder_kept_documents_apart_need_none_but_a_later_one_without_a_list_stops_removal()
    {
        Directory.CreateDirectory(Folder);
        var old = ArchiveName(_time.GetUtcNow().AddDays(-2));
        File.WriteAllText(Path.Combine(Folder, old), "documents inside");

        var store = Open();
        store.ReadReferences(old, Key).ShouldBeEmpty();

        using var held = store.Lock();
        await BackUpAsync(store, held, _time.GetUtcNow(), Document("one.bin", "x"));
        store.Prune(held, 30, Key).SweepSkipped.ShouldBeNull();

        // An archive written without a list after that (an older HC360 after a downgrade) is not taken as needing nothing.
        var downgraded = ArchiveName(_time.GetUtcNow().AddHours(1));
        File.WriteAllText(Path.Combine(Folder, downgraded), "documents inside");
        store.Prune(held, 30, Key).SweepSkipped!.ShouldContain(downgraded);
    }

    [Fact]
    public void Archive_dates_come_from_their_names()
    {
        BackupStore.ArchiveDate("hc360-20270315-013000-scheduled.hc360bak").ShouldBe(new DateTimeOffset(2027, 3, 15, 1, 30, 0, TimeSpan.Zero));
        BackupStore.ArchiveDate("hc360-20270315-013000-premigration.hc360bak").ShouldNotBeNull();
        BackupStore.ArchiveDate("hc360-20271345-013000-manual.hc360bak").ShouldBeNull();
        BackupStore.ArchiveDate("backup.hc360bak").ShouldBeNull();
    }

    [Theory]
    [InlineData("exFAT", true)]
    [InlineData("FAT32", true)]
    [InlineData("vfat", true)]
    [InlineData("NTFS", false)]
    [InlineData("ReFS", false)]
    [InlineData(null, false)]
    public void Disks_without_a_journal_are_flagged(string? format, bool unsuitable) =>
        BackupStore.UnsuitableFileSystem(format).ShouldBe(unsuitable);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
