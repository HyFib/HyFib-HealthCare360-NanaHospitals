using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The off-site copies (DED-16) against an S3-compatible store in memory (<see cref="FakeS3"/>), which checks each
/// request's signature and checksums as S3 does: a small backup in one PUT, a large one in parts that go on after a
/// failure or a restart where they stopped, the backups missed during an outage caught up newest first, each copy locked
/// with Object Lock, and a copy that does not match the backup never counted as off site.
/// </summary>
public sealed class BackupOffsiteUploadTests : IDisposable
{
    private const string Bucket = "nana-hc360-backups";
    private static readonly S3Credentials Key = new("AKIAHC360TESTKEY0001", "test-secret-access-key-not-a-real-one");
    private static readonly S3Location Mumbai = new(new Uri("https://s3.ap-south-1.amazonaws.com"), "ap-south-1", Bucket, PathStyle: false);

    // Parts of 1 KiB, and one request for a file up to 2 KiB: what S3 does with megabytes, in kilobytes.
    private static readonly OffsiteUploadSizes Small = new(SinglePutLimit: 2048, MinPartSize: 1024);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 14, 2, 0, 0, TimeSpan.Zero));
    private readonly FakeS3 _store = new(Bucket, Key);
    private readonly PlatformOptions _options;
    private readonly DataDirectories _directories;
    private FieldProtector? _escrow;

    public BackupOffsiteUploadTests()
    {
        _options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        _directories = new DataDirectories(_options);
        _directories.EnsureCreated();
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BackupOffsiteSettings Settings(int retainDays = 35, int backfillDays = 7) =>
        new() { Enabled = true, Endpoint = "https://s3.ap-south-1.amazonaws.com", Region = "ap-south-1", Bucket = Bucket, Prefix = "hc360/", RetainDays = retainDays, BackfillDays = backfillDays };

    [Fact]
    public async Task A_small_backup_goes_in_one_put_with_its_checksums_and_lock()
    {
        var run = Backup(daysAgo: 0, size: 1500);

        var result = await UploadAsync([run]);

        result.ShouldBe(new BackupOffsiteRun(1, 0, Stopped: false));
        var key = "hc360/" + run.FileName;
        _store.Requests.Select(r => (r.Method, r.Key)).ShouldBe([("PUT", key), ("HEAD", key)]);
        var put = _store.Requests[0];
        put.Headers["content-md5"].ShouldBe(Convert.ToBase64String(FakeS3.Md5(File.ReadAllBytes(PathOf(run)))));
        put.Headers["x-amz-content-sha256"].ShouldBe(run.Sha256!.ToLowerInvariant());
        _store.Objects[key].Data.ShouldBe(File.ReadAllBytes(PathOf(run)));
        _store.Objects[key].Metadata.ShouldBe(new Dictionary<string, string>
        {
            [BackupOffsite.Sha256Metadata] = run.Sha256.ToLowerInvariant(),
            [BackupOffsite.RecoveryKeyMetadata] = run.RecoveryKeyId!,
        });

        (run.OffsiteUploadedAt, run.OffsiteKey, run.OffsiteSizeBytes, run.OffsiteAttempts, run.OffsiteError, run.OffsiteUpload)
            .ShouldBe((_clock.Now, key, 1500L, 1, (string?)null, (string?)null));
    }

    [Fact]
    public async Task A_large_backup_goes_in_parts_and_each_one_is_saved_as_the_store_takes_it()
    {
        var run = Backup(daysAgo: 0, size: 5000);
        var savedParts = new List<int>();

        await UploadAsync([run], onSave: () => savedParts.Add(OffsiteUploadState.Read(run.OffsiteUpload)?.Parts.Count ?? -1));

        _store.Requests.Select(r => (r.Method, r.PartNumber)).ShouldBe([
            ("POST", null), ("PUT", 1), ("PUT", 2), ("PUT", 3), ("PUT", 4), ("PUT", 5), ("POST", null), ("HEAD", null),
        ]);
        _store.Requests.Where(r => r.PartNumber is not null).ShouldAllBe(r => r.Headers.ContainsKey("content-md5") && r.Headers.ContainsKey("x-amz-content-sha256"));
        var stored = _store.Objects["hc360/" + run.FileName];
        stored.Data.ShouldBe(File.ReadAllBytes(PathOf(run)));
        stored.ETag.ShouldEndWith("-5");

        // The upload id first, then each part, then the file checked again; then the copy is counted.
        savedParts.ShouldBe([0, 1, 2, 3, 4, 5, 5, -1]);
        (run.OffsiteUploadedAt, run.OffsiteSizeBytes, run.OffsiteAttempts).ShouldBe((_clock.Now, 5000L, 1));
    }

    [Theory]
    [InlineData(35, true)]
    [InlineData(0, false)]
    public async Task Each_copy_is_locked_in_compliance_mode_for_the_days_set(int retainDays, bool locked)
    {
        var small = Backup(daysAgo: 0, size: 100);
        var large = Backup(daysAgo: 1, size: 4000);

        await UploadAsync([small, large], Settings(retainDays));

        var until = (_clock.Now + TimeSpan.FromDays(35)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        foreach (var created in _store.Requests.Where(r => r.Method == "PUT" && r.PartNumber is null || r.Query.ContainsKey("uploads")))
        {
            created.Headers.TryGetValue("x-amz-object-lock-mode", out var mode).ShouldBe(locked);
            if (locked)
            {
                (mode, created.Headers["x-amz-object-lock-retain-until-date"]).ShouldBe(("COMPLIANCE", until));
            }
        }

        _store.Objects.Values.ShouldAllBe(o => (o.Lock != null) == locked);
        _store.Objects.Count.ShouldBe(2);

        // Only the request that creates an object carries the lock; its parts and the joining do not.
        _store.Requests.Where(r => r.PartNumber is not null || r.Query.ContainsKey("uploadId")).ShouldAllBe(r => !r.Headers.ContainsKey("x-amz-object-lock-mode"));
    }

    /// <summary>
    /// The uplink drops in the middle of a large backup: the parts the store took are kept with the run, and after a
    /// restart (a new uploader, the run read back from the database) the upload goes on from the part that failed.
    /// </summary>
    [Fact]
    public async Task An_upload_cut_off_goes_on_after_a_restart_from_the_part_that_failed()
    {
        var run = Backup(daysAgo: 0, size: 5000);
        _store.Fault = request => request.PartNumber == 3 ? throw new HttpRequestException("The network connection was lost.") : null;

        var first = await UploadAsync([run]);

        first.ShouldBe(new BackupOffsiteRun(0, 1, Stopped: true));
        run.OffsiteUploadedAt.ShouldBeNull();
        run.OffsiteError.ShouldNotBeNull().ShouldContain("The network connection was lost.");
        var state = OffsiteUploadState.Read(run.OffsiteUpload).ShouldNotBeNull();
        state.Parts.Select(p => p.Number).ShouldBe([1, 2]);
        state.Progress.ShouldBe((2, 5));

        _store.Fault = null;
        var restarted = Reload(run);
        await UploadAsync([restarted]);

        _store.Requests.Where(r => r.Query.ContainsKey("uploads")).Count().ShouldBe(1, "the same multipart upload goes on");
        _store.Requests.Where(r => r.PartNumber is not null).Select(r => r.PartNumber).ShouldBe([1, 2, 3, 3, 4, 5]);
        _store.Objects["hc360/" + run.FileName].Data.ShouldBe(File.ReadAllBytes(PathOf(run)));
        (restarted.OffsiteUploadedAt, restarted.OffsiteAttempts, restarted.OffsiteError, restarted.OffsiteUpload)
            .ShouldBe((_clock.Now, 2, (string?)null, (string?)null));
    }

    /// <summary>A part damaged on the way is refused by the store (its MD5 does not match), never stored; the next run sends it again.</summary>
    [Fact]
    public async Task A_part_damaged_on_the_way_is_refused_by_the_store_and_sent_again()
    {
        var run = Backup(daysAgo: 0, size: 3000);
        _store.Fault = request => request.PartNumber == 2
            ? FakeS3.Error(HttpStatusCode.BadRequest, "BadDigest", "The Content-MD5 you specified did not match what we received.")
            : null;

        await UploadAsync([run]);

        run.OffsiteError.ShouldNotBeNull().ShouldContain("BadDigest");
        _store.Fault = null;
        await UploadAsync([run]);
        run.OffsiteUploadedAt.ShouldNotBeNull();
        _store.Objects["hc360/" + run.FileName].Data.ShouldBe(File.ReadAllBytes(PathOf(run)));
    }

    /// <summary>
    /// The bucket's clean-up removed an unfinished upload (or someone aborted it): the store no longer knows its id, and
    /// the next run starts it again.
    /// </summary>
    [Fact]
    public async Task An_upload_the_store_no_longer_knows_starts_again()
    {
        var run = Backup(daysAgo: 0, size: 5000);
        _store.Fault = request => request.PartNumber == 3 ? throw new HttpRequestException("Timed out.") : null;
        await UploadAsync([run]);
        var lost = OffsiteUploadState.Read(run.OffsiteUpload).ShouldNotBeNull().UploadId;
        _store.Fault = request => request.Query.TryGetValue("uploadId", out var id) && id == lost
            ? FakeS3.Error(HttpStatusCode.NotFound, "NoSuchUpload", "The specified upload does not exist.")
            : null;

        await UploadAsync([run]);

        run.OffsiteError.ShouldNotBeNull().ShouldContain("starts again");
        run.OffsiteUpload.ShouldBeNull();
        await UploadAsync([run]);
        run.OffsiteUploadedAt.ShouldNotBeNull();
        _store.Requests.Count(r => r.Query.ContainsKey("uploads")).ShouldBe(2);
    }

    /// <summary>
    /// Two days without internet: each run tries the newest backup only, and stops at the first failure. Once the line is
    /// back, the newest goes first, then the older ones within the back-fill days, newest first; one older than that stays.
    /// </summary>
    [Fact]
    public async Task Backups_missed_during_an_outage_are_caught_up_newest_first_within_the_backfill_days()
    {
        var runs = new[] { Backup(daysAgo: 0, size: 900), Backup(daysAgo: 1, size: 2500), Backup(daysAgo: 2, size: 700), Backup(daysAgo: 9, size: 600) };
        _store.Fault = _ => throw new HttpRequestException("No such host is known. (s3.ap-south-1.amazonaws.com:443)");

        for (var tries = 0; tries < 3; tries++)
        {
            (await UploadAsync(BackupOffsite.Candidates(runs, 7, _clock.Now))).Stopped.ShouldBeTrue();
        }

        runs.Select(r => r.OffsiteAttempts).ShouldBe([3, 0, 0, 0]);
        runs[0].OffsiteError.ShouldNotBeNull().ShouldContain("could not be reached");

        _store.Fault = null;
        _store.Requests.Clear();
        var result = await UploadAsync(BackupOffsite.Candidates(runs, 7, _clock.Now));

        result.ShouldBe(new BackupOffsiteRun(3, 0, Stopped: false));
        _store.Requests.Where(r => r.Method == "HEAD").Select(r => r.Key).ShouldBe(runs[..3].Select(r => "hc360/" + r.FileName));
        runs[..3].ShouldAllBe(r => r.OffsiteUploadedAt != null && r.OffsiteError == null);
        runs[3].OffsiteUploadedAt.ShouldBeNull();
        BackupOffsite.Candidates(runs, 7, _clock.Now).ShouldBeEmpty();
    }

    /// <summary>The newest backup goes off site however old it is (the nightly backups may have stopped); older ones only within the back-fill days.</summary>
    [Fact]
    public void The_newest_backup_is_always_a_candidate_and_older_ones_within_the_backfill_days()
    {
        var newest = Backup(daysAgo: 10, size: 10);
        var older = Backup(daysAgo: 11, size: 10);
        var notChecked = Backup(daysAgo: 0, size: 10);
        notChecked.VerifiedAt = null;
        var beforeUpdate = Backup(daysAgo: 0, size: 10);
        beforeUpdate.Kind = BackupKind.PreMigration;

        BackupOffsite.Candidates([older, newest, notChecked, beforeUpdate], 7, _clock.Now).ShouldBe([newest]);
        BackupOffsite.Candidates([older, newest], 30, _clock.Now).ShouldBe([newest, older]);

        newest.OffsiteUploadedAt = _clock.Now;
        BackupOffsite.Candidates([older, newest], 7, _clock.Now).ShouldBeEmpty();
    }

    /// <summary>
    /// The store holds something other than what was sent once the upload is done: the copy does not count, the upload
    /// starts again at the next run, and after three such copies the backup is no longer uploaded by itself.
    /// </summary>
    [Fact]
    public async Task A_copy_that_does_not_match_the_backup_is_not_counted_and_is_uploaded_again()
    {
        var run = Backup(daysAgo: 0, size: 5000);
        _store.TamperETag = "0123456789abcdef0123456789abcdef-5";

        await UploadAsync([run]);

        run.OffsiteUploadedAt.ShouldBeNull();
        run.OffsiteError.ShouldNotBeNull().ShouldContain("does not match the backup");
        OffsiteUploadState.Read(run.OffsiteUpload).ShouldNotBeNull().ShouldSatisfyAllConditions(
            s => s.UploadId.ShouldBeNull(), s => s.Mismatches.ShouldBe(1));

        await UploadAsync([run]);
        await UploadAsync([run]);
        _store.Requests.Count(r => r.Query.ContainsKey("uploads")).ShouldBe(3);

        _store.Requests.Clear();
        await UploadAsync([run]);
        _store.Requests.ShouldBeEmpty();
        run.OffsiteError.ShouldNotBeNull().ShouldContain("no longer uploaded by itself");

        // The store answers truly again once someone has seen to it: a run read back with its count cleared goes up.
        _store.TamperETag = null;
        run.OffsiteUpload = null;
        await UploadAsync([run]);
        run.OffsiteUploadedAt.ShouldNotBeNull();
    }

    /// <summary>A copy of the right size whose single-request checksum differs is caught too.</summary>
    [Fact]
    public async Task A_small_copy_with_another_checksum_is_caught()
    {
        var run = Backup(daysAgo: 0, size: 600);
        _store.TamperETag = "0123456789abcdef0123456789abcdef";

        await UploadAsync([run]);

        run.OffsiteUploadedAt.ShouldBeNull();
        run.OffsiteError.ShouldNotBeNull().ShouldContain("ETag 0123456789abcdef0123456789abcdef");
    }

    /// <summary>
    /// The backup file on the server's disk no longer matches the SHA-256 it was checked with: nothing is sent (or what was
    /// sent is given up), and the next backup goes on.
    /// </summary>
    [Fact]
    public async Task A_backup_file_that_no_longer_matches_its_checksum_is_not_uploaded()
    {
        var damaged = Backup(daysAgo: 0, size: 600);
        var older = Backup(daysAgo: 1, size: 600);
        var large = Backup(daysAgo: 2, size: 4000);
        Damage(damaged);
        Damage(large);

        var result = await UploadAsync([damaged, older, large]);

        result.ShouldBe(new BackupOffsiteRun(1, 2, Stopped: false));
        damaged.OffsiteError.ShouldNotBeNull().ShouldContain("no longer matches the SHA-256");
        large.OffsiteError.ShouldNotBeNull().ShouldContain("no longer matches the SHA-256");
        older.OffsiteUploadedAt.ShouldNotBeNull();
        _store.Objects.Keys.ShouldBe(["hc360/" + older.FileName]);
        _store.Requests.ShouldContain(r => r.Method == "DELETE", "the large one's upload is given up");
        _store.PartsHeld.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_backup_no_longer_on_the_disk_is_recorded_and_the_next_one_goes()
    {
        var gone = Backup(daysAgo: 0, size: 600);
        var older = Backup(daysAgo: 1, size: 600);
        File.Delete(PathOf(gone));

        await UploadAsync([gone, older]);

        gone.OffsiteError.ShouldNotBeNull().ShouldContain("is no longer on this server's disk");
        older.OffsiteUploadedAt.ShouldNotBeNull();
    }

    /// <summary>With several servers, a backup taken on another one is uploaded from its copy on the second disk or NAS.</summary>
    [Fact]
    public async Task A_backup_found_only_on_the_second_disk_is_uploaded_from_there()
    {
        _options.Backup.SecondaryDirectory = Path.Combine(_root, "nas");
        Directory.CreateDirectory(_options.Backup.SecondaryDirectory);
        var run = Backup(daysAgo: 0, size: 600);
        File.Move(PathOf(run), Path.Combine(_options.Backup.SecondaryDirectory, run.FileName!));

        await UploadAsync([run]);

        run.OffsiteUploadedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_store_that_refuses_the_access_key_stops_the_run_with_its_answer()
    {
        var run = Backup(daysAgo: 0, size: 600);
        var older = Backup(daysAgo: 1, size: 600);
        _store.Fault = _ => FakeS3.Error(HttpStatusCode.Forbidden, "AccessDenied", "Access Denied");

        var result = await UploadAsync([run, older]);

        result.ShouldBe(new BackupOffsiteRun(0, 1, Stopped: true));
        run.OffsiteError.ShouldBe("The store answered 403 AccessDenied: Access Denied");
        older.OffsiteAttempts.ShouldBe(0);
    }

    /// <summary>S3 keeps the connection open with spaces while it joins a large upload, before its answer: that answer is read all the same.</summary>
    [Fact]
    public async Task The_answer_to_joining_a_large_upload_is_read_after_the_spaces_that_keep_the_connection_open()
    {
        var run = Backup(daysAgo: 0, size: 3000);
        _store.KeepAliveWhileJoining = true;

        var result = await UploadAsync([run]);

        result.ShouldBe(new BackupOffsiteRun(1, 0, Stopped: false));
        (run.OffsiteUploadedAt, run.OffsiteAttempts).ShouldBe((_clock.Now, 1));
    }

    /// <summary>The store checks every request's signature, as S3 does: one made with another secret is refused, and nothing is stored.</summary>
    [Fact]
    public async Task A_request_signed_with_another_secret_is_refused_by_the_store()
    {
        var run = Backup(daysAgo: 0, size: 600);

        await UploadAsync([run], credentials: new S3Credentials(Key.AccessKeyId, "not-the-secret-access-key"));

        run.OffsiteUploadedAt.ShouldBeNull();
        run.OffsiteError.ShouldNotBeNull().ShouldContain("403 SignatureDoesNotMatch");
        _store.Objects.ShouldBeEmpty();
    }

    /// <summary>A bucket in the path (MinIO, OCI) is signed and uploaded to the same way.</summary>
    [Fact]
    public async Task A_path_style_store_takes_the_uploads_too()
    {
        var run = Backup(daysAgo: 0, size: 3000);
        var pathStyle = new S3Location(new Uri("https://objects.example.in"), "ap-south-1", Bucket, PathStyle: true);

        await UploadAsync([run], location: pathStyle);

        run.OffsiteUploadedAt.ShouldNotBeNull();
        _store.Objects.ShouldContainKey("hc360/" + run.FileName);
    }

    [Fact]
    public void A_large_backup_is_cut_into_whole_mebibytes_within_the_stores_part_limit()
    {
        OffsiteUploadSizes.Default.PartSizeFor(100L << 20).ShouldBe(8L << 20);
        OffsiteUploadSizes.Default.PartSizeFor(200L << 30).ShouldBe(23L << 20);
        ((200L << 30) / OffsiteUploadSizes.Default.PartSizeFor(200L << 30)).ShouldBeLessThan(OffsiteUploadSizes.MaxParts);
    }

    // ---------------------------------------------------------------------------------------------------- settings

    [Theory]
    [InlineData("""{ "enabled": true, "endpoint": "https://s3.ap-south-1.amazonaws.com", "region": "ap-south-1", "bucket": "nana-hc360-backups", "prefix": "hc360/" }""", null)]
    [InlineData("""{ "enabled": false, "bucket": "nana-hc360-backups" }""", null)]
    [InlineData("""{ "enabled": true, "endpoint": "http://s3.ap-south-1.amazonaws.com", "region": "ap-south-1", "bucket": "b-1" }""", "https address")]
    [InlineData("""{ "enabled": true, "endpoint": "https://key:secret@s3.example.in", "region": "ap-south-1", "bucket": "b-1" }""", "no user name")]
    [InlineData("""{ "enabled": true, "endpoint": "https://s3.example.in/bucket", "region": "ap-south-1", "bucket": "b-1" }""", "no user name, path or query")]
    [InlineData("""{ "enabled": true, "endpoint": "https://s3.ap-south-1.amazonaws.com", "bucket": "b-1" }""", "region")]
    [InlineData("""{ "enabled": true, "endpoint": "https://s3.ap-south-1.amazonaws.com", "region": "ap-south-1", "bucket": "Nana_Backups" }""", "bucket's name")]
    [InlineData("""{ "enabled": true, "endpoint": "https://s3.ap-south-1.amazonaws.com", "region": "ap-south-1", "bucket": "nana.backups" }""", "pathStyle")]
    [InlineData("""{ "enabled": true, "endpoint": "https://s3.ap-south-1.amazonaws.com", "region": "ap-south-1", "bucket": "b-1", "prefix": "/hc360" }""", "prefix")]
    [InlineData("""{ "enabled": true, "endpoint": "https://s3.ap-south-1.amazonaws.com", "region": "ap-south-1", "bucket": "b-1", "prefix": "a/../b" }""", "prefix")]
    [InlineData("""{ "enabled": true, "endpoint": "https://s3.ap-south-1.amazonaws.com", "region": "ap-south-1", "bucket": "b-1", "retainDays": -1 }""", "retainDays")]
    [InlineData("""{ "enabled": true, "endpoint": "https://s3.ap-south-1.amazonaws.com", "region": "ap-south-1", "bucket": "b-1", "backfillDays": 400 }""", "backfillDays")]
    [InlineData("""{ "enabled": "yes" }""", "true or false")]
    [InlineData("""[ "https://s3.ap-south-1.amazonaws.com" ]""", "one object")]
    public void Off_site_settings_the_uploads_could_not_use_are_refused_when_saved(string json, string? problem)
    {
        var refused = BackupOffsite.Problem(BackupOffsite.SettingKey, JsonNode.Parse(json));

        if (problem is null)
        {
            refused.ShouldBeNull();
        }
        else
        {
            refused.ShouldNotBeNull().ShouldContain(problem);
        }
    }

    [Fact]
    public void The_access_key_needs_both_halves_and_is_never_repeated_back()
    {
        BackupOffsite.Problem(BackupOffsite.SecretKey, JsonNode.Parse("""{ "accessKeyId": "AKIAHC360", "secretAccessKey": "s3cr3t/value+x" }""")).ShouldBeNull();
        var refused = BackupOffsite.Problem(BackupOffsite.SecretKey, JsonNode.Parse("""{ "accessKeyId": "AKIAHC360" }""")).ShouldNotBeNull();
        refused.ShouldContain("both the access key ID and the secret access key");
        refused.ShouldNotContain("AKIAHC360");
        BackupOffsite.Problem(BackupOffsite.SettingKey, null).ShouldBeNull("clearing it stops the copies");
        BackupOffsite.Problem("integrations.sms", JsonNode.Parse("[]")).ShouldBeNull();
        BackupOffsite.Problem(BackupOffsite.SettingKey, JsonNode.Parse("""{ "enabled": false }"""), Guid.NewGuid()).ShouldNotBeNull().ShouldContain("whole hospital");
    }

    /// <summary>The off-site copy sends the hospital's records somewhere: changing it waits for a second administrator, as an integration's settings do.</summary>
    [Fact]
    public void Changing_where_the_backups_go_waits_for_a_second_administrator()
    {
        var on = JsonNode.Parse("""{ "enabled": true, "bucket": "nana-hc360-backups" }""");
        SettingGuard.Judge(BackupOffsite.SettingKey, null, on, secret: false).Waits.ShouldBeTrue();
        SettingGuard.Judge(BackupOffsite.SettingKey, on, JsonNode.Parse("""{ "enabled": false, "bucket": "nana-hc360-backups" }"""), secret: false).Waits.ShouldBeTrue();
        SettingGuard.RuleFor(BackupOffsite.SettingKey).ShouldBeOfType<SettingRedirect>();
        SettingGuard.RuleFor(BackupOffsite.SecretKey).ShouldBeOfType<SettingRedirect>().Name.ShouldBe("Off-site backup access key");
        var credential = SettingGuard.Judge(BackupOffsite.SecretKey, null, JsonNode.Parse("""{ "accessKeyId": "AKIA" }"""), secret: true);
        (credential.Waits, credential.To).ShouldBe((true, SettingGuard.NewSecretWords));
    }

    /// <summary>Settings and access keys are read as the integrations read theirs: what cannot be read is a problem in words, never an exception or the secret.</summary>
    [Fact]
    public async Task Settings_that_cannot_be_read_are_a_problem_in_words()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<BackupOffsiteSettings>(BackupOffsite.SettingKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(Settings());
        settings.GetAsync<BackupOffsiteSecrets>(BackupOffsite.SecretKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new CryptographicException("Padding is invalid: wJalrXUtnFEMI"));

        var unreadable = await BackupOffsite.ReadAsync(settings, NullLogger.Instance, Ct);

        (unreadable.Enabled, unreadable.Ready).ShouldBe((true, false));
        unreadable.Problem.ShouldNotBeNull().ShouldContain("could not be read");
        unreadable.Problem.ShouldNotContain("wJalrXUtnFEMI");

        settings.GetAsync<BackupOffsiteSecrets>(BackupOffsite.SecretKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(new BackupOffsiteSecrets { AccessKeyId = Key.AccessKeyId, SecretAccessKey = Key.SecretAccessKey });
        var ready = await BackupOffsite.ReadAsync(settings, NullLogger.Instance, Ct);
        ready.Ready.ShouldBeTrue();
        ready.Location.ShouldBe(Mumbai);

        settings.GetAsync<BackupOffsiteSettings>(BackupOffsite.SettingKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(Settings() with { Bucket = null });
        (await BackupOffsite.ReadAsync(settings, NullLogger.Instance, Ct)).Problem.ShouldNotBeNull().ShouldContain("not complete");

        settings.GetAsync<BackupOffsiteSettings>(BackupOffsite.SettingKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns((BackupOffsiteSettings?)null);
        var off = await BackupOffsite.ReadAsync(settings, NullLogger.Instance, Ct);
        (off.Enabled, off.Problem).ShouldBe((false, (string?)null));
    }

    /// <summary>
    /// What the privacy notice and the erasure answer say of erased data in backups (DPDP Act s8(7)) counts the off-site
    /// copies too while they are on: locked for 35 days, and removed by the bucket's lifecycle the day after.
    /// </summary>
    [Fact]
    public async Task How_long_backups_are_kept_counts_the_off_site_lock_while_it_is_on()
    {
        var settings = Substitute.For<ISettingsService>();
        var options = new PlatformOptions();
        (await BackupRetention.DaysAsync(settings, options, Ct)).ShouldBe(30);

        // Locked 35 days, the documents in generations of 7 (docs/design/incremental-backup.md, section 5.6), and the lifecycle a day after.
        settings.GetAsync<BackupOffsiteSettings>(BackupOffsite.SettingKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(Settings());
        (await BackupRetention.DaysAsync(settings, options, Ct)).ShouldBe(43);
        options.Backup.RetentionDays = 60;
        (await BackupRetention.DaysAsync(settings, options, Ct)).ShouldBe(60);

        options.Backup.RetentionDays = 30;
        settings.GetAsync<BackupOffsiteSettings>(BackupOffsite.SettingKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(Settings() with { Enabled = false });
        (await BackupRetention.DaysAsync(settings, options, Ct)).ShouldBe(30);
        settings.GetAsync<BackupOffsiteSettings>(BackupOffsite.SettingKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(Settings(retainDays: 0));
        (await BackupRetention.DaysAsync(settings, options, Ct)).ShouldBe(30);

        // An append-only second copy keeps a generation more than the backup period.
        options.Backup.SecondaryDirectory = @"\\NAS\HC360-Backups";
        options.Backup.SecondaryAppendOnly = true;
        (await BackupRetention.DaysAsync(settings, options, Ct)).ShouldBe(37);
    }

    [Theory]
    [InlineData("hc360/", "hc360/hc360-x.hc360bak")]
    [InlineData("hc360", "hc360/hc360-x.hc360bak")]
    [InlineData("", "hc360-x.hc360bak")]
    [InlineData(null, "hc360-x.hc360bak")]
    public void A_backup_is_stored_under_the_prefix_and_its_file_name(string? prefix, string key) =>
        BackupOffsite.ObjectKey(prefix, "hc360-x.hc360bak").ShouldBe(key);

    // ------------------------------------------------------------------------- documents in generations (section 5.6)

    [Fact]
    public async Task A_backups_documents_go_up_into_the_generation_before_it_each_locked_and_checked()
    {
        var run = BackupWithDocuments(daysAgo: 0, "first", "second");
        var ids = new[] { Hash("first"), Hash("second") }.Order(StringComparer.Ordinal).ToList();

        var result = await UploadAsync([run], escrow: Escrow);

        result.ShouldBe(new BackupOffsiteRun(1, 0, Stopped: false));
        var archive = "hc360/" + run.FileName;
        _store.Requests.Select(r => (r.Method, r.Key)).ShouldBe([
            ("PUT", BlobKey("gen-20261114", ids[0])), ("HEAD", BlobKey("gen-20261114", ids[0])),
            ("PUT", BlobKey("gen-20261114", ids[1])), ("HEAD", BlobKey("gen-20261114", ids[1])),
            ("PUT", archive), ("HEAD", archive)]);
        var blob = _store.Objects[BlobKey("gen-20261114", ids[0])];
        blob.Data.ShouldBe(File.ReadAllBytes(Path.Combine(_directories.Backups, "blobs", ids[0][..2], ids[0][2..4], ids[0] + ".blob")));
        blob.Metadata[BackupOffsite.Sha256Metadata].ShouldBe(ids[0]);

        // Locked until the generation's last backup has expired: its start, plus a generation, plus the backup period.
        blob.Lock.ShouldNotBeNull().RetainUntil.ShouldBe("2026-12-26T00:00:00Z");
        run.OffsiteUploadedAt.ShouldBe(_clock.Now);
    }

    [Fact]
    public async Task Within_a_generation_only_the_new_documents_go_up_and_a_new_generation_takes_them_all_again()
    {
        await UploadAsync([BackupWithDocuments(daysAgo: 0, "a", "b")], escrow: Escrow);
        _store.Requests.Clear();

        _clock.Now = _clock.Now.AddDays(1);
        await UploadAsync([BackupWithDocuments(daysAgo: 0, "a", "b", "c")], escrow: Escrow);
        _store.Requests.Where(r => r.Method == "PUT" && r.Key!.Contains("/blobs/", StringComparison.Ordinal)).Select(r => r.Key)
            .ShouldBe([BlobKey("gen-20261114", Hash("c"))]);
        _store.Requests.Clear();

        _clock.Now = _clock.Now.AddDays(7);
        await UploadAsync([BackupWithDocuments(daysAgo: 0, "a", "b", "c")], escrow: Escrow);
        _store.Requests.Where(r => r.Method == "PUT" && r.Key!.Contains("/blobs/", StringComparison.Ordinal)).Select(r => r.Key).Order()
            .ShouldBe(new[] { "a", "b", "c" }.Select(d => BlobKey("gen-20261122", Hash(d))).Order());
    }

    [Fact]
    public async Task A_backup_is_not_uploaded_until_all_its_documents_are_and_the_next_run_goes_on_where_it_stopped()
    {
        // F22.
        var run = BackupWithDocuments(daysAgo: 0, "a", "b", "c");
        var ids = new[] { "a", "b", "c" }.Select(Hash).Order(StringComparer.Ordinal).ToList();
        _store.Fault = request => request.Key == BlobKey("gen-20261114", ids[1]) && request.Method == "PUT"
            ? throw new HttpRequestException("The network connection was lost.")
            : null;

        var first = await UploadAsync([run], escrow: Escrow);

        first.Stopped.ShouldBeTrue();
        run.OffsiteUploadedAt.ShouldBeNull();
        run.OffsiteError.ShouldNotBeNull().ShouldContain("documents of " + run.FileName);
        _store.Objects.Keys.ShouldNotContain("hc360/" + run.FileName);

        _store.Fault = null;
        _store.Requests.Clear();
        (await UploadAsync([run], escrow: Escrow)).Uploaded.ShouldBe(1);
        _store.Requests.Where(r => r.Method == "PUT").Select(r => r.Key).ShouldBe([BlobKey("gen-20261114", ids[1]), BlobKey("gen-20261114", ids[2]), "hc360/" + run.FileName]);
    }

    [Fact]
    public async Task A_backup_whose_list_of_documents_is_missing_or_changed_is_not_uploaded()
    {
        var missing = BackupWithDocuments(daysAgo: 0, "a");
        File.Delete(Path.ChangeExtension(PathOf(missing), ".refs"));
        (await UploadAsync([missing], escrow: Escrow)).Failed.ShouldBe(1);
        missing.OffsiteError.ShouldNotBeNull().ShouldContain("has no list of the documents it needs");

        var changed = BackupWithDocuments(daysAgo: 0, "b");
        File.AppendAllText(Path.ChangeExtension(PathOf(changed), ".refs"), Hash("x") + "\n");
        (await UploadAsync([changed], escrow: Escrow)).Failed.ShouldBe(1);
        changed.OffsiteError.ShouldNotBeNull().ShouldContain("cannot be used");

        _store.Objects.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------- helpers

    private FieldProtector Escrow => _escrow ??= new FieldProtector(_directories, new EphemeralDataProtectionProvider());

    private static string Hash(string content) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    private static string BlobKey(string generation, string id) => $"hc360/{generation}/blobs/{id[..2]}/{id[2..4]}/{id}.blob";

    /// <summary>A verified format 3 backup, as the backup service leaves it: its documents in the backup folder, then its sealed list, then its file.</summary>
    private BackupRun BackupWithDocuments(int daysAgo, params string[] documents)
    {
        var run = Backup(daysAgo, size: 1200);
        var masterKey = Escrow.ExportMasterKey();
        var referencesKey = FieldMasterKey.DeriveKey(masterKey, BackupStore.ReferencesKeyPurpose);
        var store = BackupStore.Open(_directories.Backups, Escrow.Fingerprint, referencesKey, _clock);
        using var held = store.Lock();
        foreach (var document in documents)
        {
            var source = Path.Combine(_root, "source-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(source, document);
            store.AddAsync(held, source, Hash(document), CancellationToken.None).GetAwaiter().GetResult();
        }

        store.WriteReferences(held, run.FileName!, documents.Select(Hash), referencesKey);
        return run;
    }

    private Task<BackupOffsiteRun> UploadAsync(
        IReadOnlyList<BackupRun> runs, BackupOffsiteSettings? settings = null, Action? onSave = null, S3Location? location = null,
        S3Credentials? credentials = null, IFieldKeyEscrow? escrow = null)
    {
        var uploader = new BackupOffsiteUploader(Substitute.For<IDbContextFactory<HospitalDbContext>>(), Substitute.For<ISettingsService>(),
            Substitute.For<IHttpClientFactory>(), _directories, Options.Create(_options), _clock, NullLogger<BackupOffsiteUploader>.Instance, escrow)
        {
            Sizes = Small,
        };
#pragma warning disable CA2000 // The client is the store's for the test's lifetime; disposing it would dispose the store.
        var client = new S3Client(new HttpClient(_store, disposeHandler: false), location ?? Mumbai, credentials ?? Key, _clock);
#pragma warning restore CA2000
        return uploader.UploadAsync(client, settings ?? Settings(), runs, _ =>
        {
            onSave?.Invoke();
            return Task.CompletedTask;
        }, Ct);
    }

    /// <summary>A verified full backup made <paramref name="daysAgo"/> days ago at 01:30, its file of <paramref name="size"/> random bytes on the disk.</summary>
    private BackupRun Backup(int daysAgo, int size)
    {
        var startedAt = new DateTimeOffset(_clock.Now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-daysAgo).AddMinutes(-240);
        var fileName = $"hc360-{startedAt:yyyyMMdd-HHmmss}-scheduled-{Guid.NewGuid():N}{BackupService.FileExtension}";
        var data = RandomNumberGenerator.GetBytes(size);
        File.WriteAllBytes(Path.Combine(_directories.Backups, fileName), data);
        return new BackupRun
        {
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMinutes(10),
            VerifiedAt = startedAt.AddMinutes(10),
            Kind = BackupKind.Scheduled,
            Status = BackupStatus.Succeeded,
            FileName = fileName,
            SizeBytes = size,
            Sha256 = Convert.ToHexString(SHA256.HashData(data)),
            RecoveryKeyId = "7B1E0A44C2D9F3A8",
        };
    }

    private string PathOf(BackupRun run) => Path.Combine(_directories.Backups, run.FileName!);

    /// <summary>One byte of the file changed on the disk after the backup was checked.</summary>
    private void Damage(BackupRun run)
    {
        var data = File.ReadAllBytes(PathOf(run));
        data[^1] ^= 0xFF;
        File.WriteAllBytes(PathOf(run), data);
    }

    /// <summary>The run as it would be read back from the database: only what is saved in its columns.</summary>
    private static BackupRun Reload(BackupRun run) => new()
    {
        Id = run.Id,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        VerifiedAt = run.VerifiedAt,
        Kind = run.Kind,
        Status = run.Status,
        FileName = run.FileName,
        SizeBytes = run.SizeBytes,
        Sha256 = run.Sha256,
        RecoveryKeyId = run.RecoveryKeyId,
        OffsiteAttempts = run.OffsiteAttempts,
        OffsiteKey = run.OffsiteKey,
        OffsiteError = run.OffsiteError,
        OffsiteUpload = run.OffsiteUpload,
    };

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
