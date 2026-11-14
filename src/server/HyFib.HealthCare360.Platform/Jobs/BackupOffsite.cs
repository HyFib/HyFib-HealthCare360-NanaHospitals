using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// The hospital-wide setting <c>backup.offsite</c>: the S3-compatible bucket outside the building that every verified
/// full backup is uploaded to (<see cref="BackupOffsiteUploader"/>). Its access key is the encrypted setting
/// <c>backup.offsite.secret</c> (<see cref="BackupOffsiteSecrets"/>).
/// </summary>
public sealed record BackupOffsiteSettings
{
    /// <summary>Off until a person switches it on, once the bucket and its access key are ready.</summary>
    public bool Enabled { get; init; }

    /// <summary>The store's https address: <c>https://s3.ap-south-1.amazonaws.com</c> for AWS Mumbai, say.</summary>
    public string? Endpoint { get; init; }

    /// <summary>The store's region, which every request is signed for: <c>ap-south-1</c>, <c>ap-mumbai-1</c>.</summary>
    public string? Region { get; init; }

    public string? Bucket { get; init; }

    /// <summary>Put before each backup's file name to make its object key: <c>hc360/</c>, say. Optional.</summary>
    public string? Prefix { get; init; }

    /// <summary>Name the bucket in the path rather than the host, as MinIO-based stores and OCI want.</summary>
    public bool PathStyle { get; init; }

    /// <summary>
    /// How long each copy is locked once uploaded (S3 Object Lock in compliance mode), in days; 0 uploads without a lock,
    /// for a store that locks at the bucket instead (OCI's retention rules).
    /// </summary>
    public int RetainDays { get; init; } = BackupOffsite.DefaultRetainDays;

    /// <summary>How far back, in days, backups not yet off site are uploaded after the newest one.</summary>
    public int BackfillDays { get; init; } = BackupOffsite.DefaultBackfillDays;
}

/// <summary>
/// The encrypted setting <c>backup.offsite.secret</c>: the access key the uploads are signed with. A class, not a record,
/// so the secret is never written out by a generated <c>ToString</c>.
/// </summary>
public sealed class BackupOffsiteSecrets
{
    public string? AccessKeyId { get; init; }

    public string? SecretAccessKey { get; init; }
}

/// <summary>The off-site settings as a run or System health reads them: switched on and ready, or why not.</summary>
/// <param name="Problem">Why the copies cannot be made, in words for the administrator; null when they can (or are off).</param>
internal sealed record BackupOffsiteConfig(BackupOffsiteSettings? Settings, S3Location? Location, S3Credentials? Credentials, string? Problem)
{
    public bool Enabled => Settings is { Enabled: true };

    /// <summary>Switched on, and nothing stops a run.</summary>
    public bool Ready => Enabled && Problem is null && Location is not null && Credentials is not null;
}

/// <summary>
/// What the backup history records of the full backups' copies off site, for System health (<see cref="BackupOffsite"/>).
/// </summary>
/// <param name="LatestAt">When the latest verified full backup was started.</param>
/// <param name="LatestVerifiedAt">When it was read back and checked.</param>
/// <param name="LatestUploaded">Whether it is off site.</param>
/// <param name="LatestError">Why its last upload failed, when it did.</param>
/// <param name="LatestProgress">Its multipart upload's progress, when one is under way: parts done, and parts in all.</param>
/// <param name="LastOffsiteAt">When the newest full backup off site was started; null when none is.</param>
/// <param name="FirstFailedAt">When the first full backup since then whose upload was tried, and is not done, was started.</param>
internal sealed record BackupOffsiteHistory(
    DateTimeOffset LatestAt,
    DateTimeOffset LatestVerifiedAt,
    bool LatestUploaded,
    string? LatestError,
    (int Done, int Total)? LatestProgress,
    DateTimeOffset? LastOffsiteAt,
    DateTimeOffset? FirstFailedAt);

/// <summary>
/// Copies of the backups outside the building (DED-16): every verified full backup is uploaded to an S3-compatible bucket
/// (AWS Mumbai or Hyderabad, OCI, or any S3-compatible store in India), each copy locked against deletion for
/// <see cref="BackupOffsiteSettings.RetainDays"/> (Object Lock, compliance mode) with an access key that may upload but
/// not delete. The backups are already encrypted with the recovery key, which the store never sees.
/// <list type="bullet">
/// <item>A newest backup not off site whose upload failed is a warning on System health (<see cref="Code"/>), with the
/// store's reason.</item>
/// <item>It is critical, and told to IT on-call by SMS and escalated (<see cref="SystemHealthAlerts"/>), when no backup
/// has reached the bucket for <see cref="UrgentAge"/>, as for the copy to the second disk (<see cref="BackupCopies"/>),
/// or when the newest verified backup is still not off site <see cref="NewestGrace"/> after it was checked.</item>
/// <item>On a live server with nightly backups, while no off-site copy is set up at all, it is a warning: "No copy of
/// the backups leaves the building".</item>
/// </list>
/// Only full backups count, as for the second copy: the one taken before an update holds no documents.
/// </summary>
internal static class BackupOffsite
{
    public const string SettingKey = "backup.offsite";
    public const string SecretKey = "backup.offsite.secret";
    public const string HttpClientName = "backup.offsite";
    public const string Code = "backup.offsite_stale";

    public const int DefaultRetainDays = 35;
    public const int DefaultBackfillDays = 7;
    public const int MaxRetainDays = 3650;
    public const int MaxBackfillDays = 365;

    /// <summary>The metadata each copy carries: the SHA-256 of the backup file, as the Backups list shows it.</summary>
    public const string Sha256Metadata = "hc360-sha256";

    /// <summary>The metadata each copy carries: the id of the recovery key that opens it.</summary>
    public const string RecoveryKeyMetadata = "hc360-recovery-key";

    /// <summary>
    /// How long a request may take: a part of a large backup on a slow uplink takes minutes, and a connection that stalls
    /// is given up after this, to be tried again at the next run.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long without a new copy off site the warning turns critical: three nights' copies missed and the half day
    /// <c>backup.stale</c> also allows, as for the second disk (<see cref="BackupCopies.UrgentAge"/>).
    /// </summary>
    public static readonly TimeSpan UrgentAge = BackupCopies.UrgentAge;

    /// <summary>How long after it was checked the newest backup may still be waiting to go off site before that is critical.</summary>
    public static readonly TimeSpan NewestGrace = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Regex RegionPattern = new("^[a-z0-9][a-z0-9-]{1,39}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex BucketPattern = new("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex PrefixPattern = new("^[A-Za-z0-9._/-]{0,200}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private const string Where = "under Administration › Backups (Off-site copy)";

    private const string Fix =
        "Check the server's internet connection and the off-site settings " + Where + "; the deployment guide (section 8) says what each answer from the store means.";

    // ---------------------------------------------------------------------------------------------------- settings

    /// <summary>
    /// Why <paramref name="value"/> cannot be saved as <paramref name="key"/>, or null (and for any other key), so that a
    /// setting the uploads could not use is refused when it is saved rather than read leniently later. Both are the whole
    /// hospital's (the backups are), never one facility's (<paramref name="facilityId"/>). Clearing either (null) is fine:
    /// the copies stop. A value switched off is checked only for what it holds.
    /// </summary>
    public static string? Problem(string key, JsonNode? value, Guid? facilityId = null)
    {
        if (key != SettingKey && key != SecretKey)
        {
            return null;
        }

        if (facilityId is not null)
        {
            return "The off-site copy is set for the whole hospital, as the backups are: save it for all facilities.";
        }

        if (value is null)
        {
            return null;
        }

        if (value is not JsonObject)
        {
            return key == SettingKey
                ? "Enter the off-site settings as one object, such as { \"enabled\": true, \"endpoint\": \"https://s3.ap-south-1.amazonaws.com\", \"region\": \"ap-south-1\", \"bucket\": \"…\" }."
                : "Enter the access key as one object: { \"accessKeyId\": \"…\", \"secretAccessKey\": \"…\" }.";
        }

        try
        {
            return key == SettingKey
                ? SettingsProblem(value.Deserialize<BackupOffsiteSettings>(JsonOptions)!, complete: false)
                : SecretsProblem(value.Deserialize<BackupOffsiteSecrets>(JsonOptions)!);
        }
        catch (JsonException)
        {
            return key == SettingKey
                ? "The off-site settings are not valid: enabled and pathStyle are true or false, retainDays and backfillDays whole numbers, the rest text."
                : "The access key is not valid: accessKeyId and secretAccessKey are text.";
        }
    }

    /// <summary>
    /// What is wrong with <paramref name="settings"/>; with <paramref name="complete"/>, or once they are switched on,
    /// what is missing too.
    /// </summary>
    internal static string? SettingsProblem(BackupOffsiteSettings settings, bool complete)
    {
        complete |= settings.Enabled;
        if (settings.Endpoint is { Length: > 0 } || complete)
        {
            if (EndpointProblem(settings.Endpoint) is { } endpoint)
            {
                return endpoint;
            }
        }

        if ((settings.Region is { Length: > 0 } || complete) && !RegionPattern.IsMatch(settings.Region?.Trim() ?? string.Empty))
        {
            return "Enter the store's region as it names it, in lower case, such as ap-south-1 (AWS Mumbai) or ap-mumbai-1 (OCI).";
        }

        if (settings.Bucket is { Length: > 0 } || complete)
        {
            var bucket = settings.Bucket?.Trim() ?? string.Empty;
            if (!BucketPattern.IsMatch(bucket) || bucket.Contains("..", StringComparison.Ordinal))
            {
                return "Enter the bucket's name: 3 to 63 lower-case letters, digits, dots or hyphens, starting and ending with a letter or digit.";
            }

            if (bucket.Contains('.', StringComparison.Ordinal) && !settings.PathStyle)
            {
                return "A bucket name with dots does not match the store's certificate in the host name: set pathStyle to true, or use a name without dots.";
            }
        }

        var prefix = settings.Prefix ?? string.Empty;
        if (!PrefixPattern.IsMatch(prefix) || prefix.StartsWith('/') || prefix.Contains("//", StringComparison.Ordinal)
            || prefix.Split('/').Any(segment => segment is "." or ".."))
        {
            return "The prefix is at most 200 letters, digits, dots, hyphens, underscores and slashes, such as hc360/, and does not start with a slash.";
        }

        if (settings.RetainDays is < 0 or > MaxRetainDays)
        {
            return $"retainDays is the number of days each copy is locked, from 0 (no lock) to {MaxRetainDays}.";
        }

        if (settings.BackfillDays is < 0 or > MaxBackfillDays)
        {
            return $"backfillDays is how many days back older backups are uploaded, from 0 to {MaxBackfillDays}.";
        }

        return null;
    }

    /// <summary>The access key's problem, in words that never repeat what was typed.</summary>
    internal static string? SecretsProblem(BackupOffsiteSecrets? secrets) =>
        secrets?.AccessKeyId is not { Length: > 0 and <= 256 } id || id.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
        || secrets.SecretAccessKey is not { Length: > 0 and <= 256 } secret || secret.Any(char.IsControl)
            ? "Enter both the access key ID and the secret access key, as the store gave them."
            : null;

    private static string? EndpointProblem(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return "The endpoint is the store's https address, such as https://s3.ap-south-1.amazonaws.com. Plain http is never used: the backups would cross the internet unprotected by TLS.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return "The endpoint is the store's address alone, such as https://s3.ap-south-1.amazonaws.com: no user name, path or query. The bucket and the access key are set apart.";
        }

        return null;
    }

    /// <summary>
    /// The settings as a run reads them, as the integrations read theirs: a value that cannot be read, or an access key
    /// that cannot be decrypted, is a problem in words rather than an exception, and only the exception's type is logged,
    /// never its message, which could echo a secret.
    /// </summary>
    public static async Task<BackupOffsiteConfig> ReadAsync(ISettingsService settings, ILogger? logger, CancellationToken ct)
    {
        BackupOffsiteSettings? values;
        try
        {
            values = await settings.GetAsync<BackupOffsiteSettings>(SettingKey, cancellationToken: ct);
        }
        catch (JsonException)
        {
            logger?.LogWarning("The off-site backup setting {Key} is not valid", SettingKey);
            return new(null, null, null, $"The off-site settings ({SettingKey}) are not valid. Correct them {Where}.");
        }

        if (values is not { Enabled: true })
        {
            return new(values, null, null, null);
        }

        if (SettingsProblem(values, complete: true) is { } problem)
        {
            return new(values, null, null, $"The off-site settings ({SettingKey}) are not complete: {problem}");
        }

        BackupOffsiteSecrets? secrets;
        try
        {
            secrets = await settings.GetAsync<BackupOffsiteSecrets>(SecretKey, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException)
        {
            logger?.LogWarning("The off-site access key could not be read ({ErrorType})", ex.GetType().Name);
            return new(values, null, null, $"The off-site access key ({SecretKey}) could not be read. Enter it again {Where}.");
        }

        if (SecretsProblem(secrets) is not null)
        {
            return new(values, null, null, $"No access key is set for the off-site store. Enter it {Where} ({SecretKey}).");
        }

        var location = new S3Location(new Uri(values.Endpoint!.Trim(), UriKind.Absolute), values.Region!.Trim(), values.Bucket!.Trim(), values.PathStyle);
        return new(values, location, new S3Credentials(secrets!.AccessKeyId!, secrets.SecretAccessKey!), null);
    }

    /// <summary>The object key of a backup file: the prefix, then its file name.</summary>
    public static string ObjectKey(string? prefix, string fileName) =>
        prefix is { Length: > 0 } p ? (p.EndsWith('/') ? p + fileName : $"{p}/{fileName}") : fileName;

    /// <summary>
    /// The backups to upload, in the order they go: the newest verified full backup, whatever its age, then each older one
    /// not yet off site that was made within <paramref name="backfillDays"/>, newest first.
    /// </summary>
    public static IReadOnlyList<BackupRun> Candidates(IEnumerable<BackupRun> runs, int backfillDays, DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromDays(Math.Clamp(backfillDays, 0, MaxBackfillDays));
        return runs
            .Where(IsVerifiedFull)
            .OrderByDescending(r => r.StartedAt)
            .Where((r, index) => r.OffsiteUploadedAt is null && (index == 0 || r.StartedAt >= cutoff))
            .ToList();
    }

    private static bool IsVerifiedFull(BackupRun r) =>
        r.Status == BackupStatus.Succeeded && r.VerifiedAt is not null && r.FileName is not null && r.Kind is BackupKind.Scheduled or BackupKind.Manual;

    // ---------------------------------------------------------------------------------------------------- System health

    /// <summary>The warning, from the settings and the backup history; null when the copies are made, or nothing is backed up nightly.</summary>
    public static async Task<HealthWarning?> WarningAsync(
        HospitalDbContext db, ISettingsService settings, PlatformOptions options, DateTimeOffset now, CancellationToken ct)
    {
        if (!options.Backup.Enabled)
        {
            return null;
        }

        var config = await ReadAsync(settings, null, ct);
        if (!config.Enabled)
        {
            return options.AllowsSimulators ? null : NotSetUp(config.Problem);
        }

        var history = await HistoryAsync(db, ct);
        var store = Describe(config.Settings!);
        if (history is null)
        {
            return config.Problem is null
                ? null
                : new HealthWarning(Code, "warning", $"Off-site copies are switched on, but cannot be made: {config.Problem}");
        }

        return Warning(store, history with { LatestError = history.LatestError ?? config.Problem }, now,
            PlatformServiceCollectionExtensions.ResolveTimeZone(options.TimeZone));
    }

    /// <summary>The warning while no off-site copy is set up: every copy is in the building.</summary>
    public static HealthWarning NotSetUp(string? problem) => new(Code, "warning",
        "No copy of the backups leaves the building: every backup, and its copy on the second disk or NAS, is in the hospital, " +
        "where a fire, a flood, a theft or ransomware takes them with the server. " +
        (problem is null ? string.Empty : problem + " ") +
        $"Set up the off-site copy {Where}, as the deployment guide says (section 8).");

    /// <summary>
    /// The warning for <paramref name="history"/> at <paramref name="now"/>, with times in the hospital's
    /// <paramref name="zone"/>; <paramref name="store"/> names the bucket. Critical once the newest copy off site is older
    /// than <see cref="UrgentAge"/>, or, where no backup ever reached the bucket, once uploads have been failing that long,
    /// or once the newest verified backup has waited <see cref="NewestGrace"/>. Before that, a warning while its upload
    /// fails, with the store's reason; nothing while it is waiting for, or in, its first upload.
    /// </summary>
    public static HealthWarning? Warning(string store, BackupOffsiteHistory history, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (history.LatestUploaded)
        {
            return null;
        }

        var reason = Sentence(history.LatestError);
        var progress = history.LatestProgress is { } p
            ? string.Create(CultureInfo.InvariantCulture, $" Its upload is under way: {p.Done:N0} of {p.Total:N0} parts are in the bucket.")
            : string.Empty;
        var lastAttempt = reason is null ? progress : $"{progress} Last attempt: {reason}";
        if (history.LastOffsiteAt is { } copied && now - copied > UrgentAge)
        {
            return new(Code, "critical",
                $"No backup has reached the off-site copy in {store} since the one of {Local(copied, zone)}, which is the newest backup outside the building.{lastAttempt} {Fix}");
        }

        if (history.LastOffsiteAt is null && history.FirstFailedAt is { } failing && now - failing > UrgentAge)
        {
            return new(Code, "critical",
                $"No backup has ever reached the off-site copy in {store}, and the uploads tried since the backup of {Local(failing, zone)} have not finished: no backup is outside the building.{lastAttempt} {Fix}");
        }

        if (now - history.LatestVerifiedAt > NewestGrace)
        {
            return new(Code, "critical", reason is null && progress.Length == 0
                ? $"The backup of {Local(history.LatestAt, zone)} is still not in {store}, a day after it was made, and HC360 has not tried to upload it: check that the server that runs the background jobs is up and that the off-site copy is switched on."
                : $"The backup of {Local(history.LatestAt, zone)} is still not in {store}, a day after it was made.{lastAttempt} {Fix}");
        }

        return reason is null
            ? null
            : new(Code, "warning",
                $"The backup of {Local(history.LatestAt, zone)} has not reached the off-site copy in {store} yet: {reason} HC360 tries again every 15 minutes.{progress}");
    }

    /// <summary>The bucket and its store, as the warnings name them.</summary>
    public static string Describe(BackupOffsiteSettings settings) =>
        Uri.TryCreate(settings.Endpoint?.Trim(), UriKind.Absolute, out var endpoint)
            ? $"bucket {settings.Bucket} at {endpoint.Host}"
            : $"bucket {settings.Bucket}";

    /// <summary>What the backup history says of the copies off site; null before any verified full backup.</summary>
    internal static async Task<BackupOffsiteHistory?> HistoryAsync(HospitalDbContext db, CancellationToken ct)
    {
        var full = db.Set<BackupRun>().AsNoTracking()
            .Where(r => r.Status == BackupStatus.Succeeded && r.VerifiedAt != null && (r.Kind == BackupKind.Scheduled || r.Kind == BackupKind.Manual));
        var latest = await full.OrderByDescending(r => r.StartedAt)
            .Select(r => new { r.StartedAt, r.VerifiedAt, r.OffsiteUploadedAt, r.OffsiteError, r.OffsiteUpload })
            .FirstOrDefaultAsync(ct);
        if (latest is null)
        {
            return null;
        }

        if (latest.OffsiteUploadedAt is not null)
        {
            return new(latest.StartedAt, latest.VerifiedAt!.Value, true, null, null, latest.StartedAt, null);
        }

        var lastOffsiteAt = await full.Where(r => r.OffsiteUploadedAt != null).MaxAsync(r => (DateTimeOffset?)r.StartedAt, ct);
        var tried = full.Where(r => r.OffsiteUploadedAt == null && r.OffsiteAttempts > 0);
        if (lastOffsiteAt is { } copied)
        {
            tried = tried.Where(r => r.StartedAt > copied);
        }

        var firstFailedAt = await tried.MinAsync(r => (DateTimeOffset?)r.StartedAt, ct);
        return new(latest.StartedAt, latest.VerifiedAt!.Value, false, latest.OffsiteError, OffsiteUploadState.Read(latest.OffsiteUpload)?.Progress,
            lastOffsiteAt, firstFailedAt);
    }

    private static string? Sentence(string? text)
    {
        var trimmed = text?.Trim();
        return trimmed is null or { Length: 0 } ? null : trimmed.EndsWith('.') ? trimmed : trimmed + ".";
    }

    private static string Local(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// How many days a backup is kept, wherever HC360 puts it: on the server and its second copy for
/// <c>HealthCare360:Backup:RetentionDays</c>; and, while the off-site copy is on, in the bucket until its lock
/// (<see cref="BackupOffsiteSettings.RetainDays"/>) has passed and the bucket's lifecycle removes it the day after
/// (DEPLOYMENT.md section 8). What the privacy notice and the erasure answer say of erased data in backups is counted
/// from it (DPDP Act s8(7)), so it never says less than the copies off site are kept.
/// </summary>
public static class BackupRetention
{
    public static async Task<int> DaysAsync(ISettingsService settings, PlatformOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        var days = Math.Max(options.Backup.RetentionDays, 1);

        // An append-only secondary keeps its documents in weekly generations, which its own retention removes a generation
        // after the backup period (docs/design/incremental-backup.md, section 5.6).
        if (options.Backup.SecondaryAppendOnly && options.Backup.SecondaryDirectory is { Length: > 0 })
        {
            days += BackupStore.GenerationDays;
        }

        BackupOffsiteSettings? offsite;
        try
        {
            offsite = await settings.GetAsync<BackupOffsiteSettings>(BackupOffsite.SettingKey, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return days;
        }

        // The bucket's documents are in generations too: a copy stays until its generation's last backup has expired, a
        // generation after the lock of the backup that first needed it, and the lifecycle removes it the day after.
        return offsite is { Enabled: true, RetainDays: > 0 } ? Math.Max(days, offsite.RetainDays + BackupStore.GenerationDays + 1) : days;
    }
}

/// <summary>A part of a multipart upload that the store holds: its number, the ETag it gave, and the MD5 of what was sent.</summary>
internal sealed record OffsiteUploadPart(int Number, string ETag, string Md5);

/// <summary>
/// A backup's upload as it stands, saved with its run after each step (<see cref="BackupRun.OffsiteUpload"/>), so an
/// upload stopped by a failure, a restart or a slow uplink goes on from the last part the store took.
/// </summary>
/// <param name="Location">Where it goes (<see cref="S3Location.Describe"/>): a change of bucket or prefix starts again.</param>
/// <param name="UploadId">The multipart upload's id; null before one is started, and after one is given up.</param>
/// <param name="PartSize">The size of every part but the last, kept so the parts go on where they stopped.</param>
/// <param name="Checked">The backup file was read again in full and still matches its checksum and the parts sent.</param>
/// <param name="Mismatches">Uploads that finished but whose copy in the bucket did not match the backup.</param>
internal sealed record OffsiteUploadState(
    string Location, string? UploadId, long PartSize, IReadOnlyList<OffsiteUploadPart> Parts, bool Checked = false, int Mismatches = 0, long Length = 0)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Parts in the bucket, and parts in all; null when no multipart upload is under way.</summary>
    public (int Done, int Total)? Progress =>
        UploadId is not null && PartSize > 0 && Length > 0 ? (Parts.Count, (int)((Length + PartSize - 1) / PartSize)) : null;

    public string Write() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>The saved state, or null when there is none or it cannot be read: the upload then starts again.</summary>
    public static OffsiteUploadState? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OffsiteUploadState>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The sizes uploads are made in. A file up to <see cref="SinglePutLimit"/> goes in one request; a larger one in parts of
/// at least <see cref="MinPartSize"/>, larger for a file so big that S3's 10,000 parts would not hold it.
/// </summary>
internal sealed record OffsiteUploadSizes(long SinglePutLimit, long MinPartSize)
{
    public const int MaxParts = 10_000;

    public static readonly OffsiteUploadSizes Default = new(16L << 20, 8L << 20);

    /// <summary>
    /// The part size for a file of <paramref name="length"/> bytes: <see cref="MinPartSize"/>, or for a file that would then
    /// need more than 9,000 parts, the size that fits it in 9,000, in whole mebibytes.
    /// </summary>
    public long PartSizeFor(long length)
    {
        var needed = (length + 8_999) / 9_000;
        return needed <= MinPartSize ? MinPartSize : ((needed + (1 << 20) - 1) >> 20) << 20;
    }
}

/// <summary>What a run of the uploader did, for its log line and the tests.</summary>
internal sealed record BackupOffsiteRun(int Uploaded, int Failed, bool Stopped)
{
    public static readonly BackupOffsiteRun Nothing = new(0, 0, false);
}

/// <summary>
/// Uploads the verified full backups to the off-site bucket (<see cref="BackupOffsite"/>), on the server that runs the
/// background jobs, every 15 minutes (<see cref="BackupOffsiteJob"/>): the newest first, then older ones not yet there
/// within <see cref="BackupOffsiteSettings.BackfillDays"/>, one at a time.
/// <para>
/// A file up to <see cref="OffsiteUploadSizes.SinglePutLimit"/> goes in one PUT; a larger one in parts, and the upload's id
/// and each part the store takes are saved with the backup's run as they happen (<see cref="OffsiteUploadState"/>), so a
/// large backup on a slow uplink finishes over several runs, across failures and restarts. Before the parts are joined,
/// the file is read again in full: it must still match the SHA-256 it was checked with and the MD5 of each part sent, or
/// the upload is given up. Once joined, the copy's size, ETag and SHA-256 metadata are read back from the store; only a
/// copy that matches counts. Each copy is locked for <see cref="BackupOffsiteSettings.RetainDays"/> (Object Lock,
/// compliance mode) and carries the backup's SHA-256 and recovery key id as metadata.
/// </para>
/// <para>
/// Each run records, per backup, the object key, the attempts, the last error and, once verified, when the copy was
/// uploaded and its size. A failure of the store or the network stops the run (the next one tries again); a problem of
/// one backup's file (gone from the disk, or no longer matching its checksum) is recorded and the run goes on to the next.
/// </para>
/// </summary>
internal sealed class BackupOffsiteUploader(
    IDbContextFactory<HospitalDbContext> contexts,
    ISettingsService settings,
    IHttpClientFactory httpClients,
    DataDirectories directories,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<BackupOffsiteUploader> logger,
    IFieldKeyEscrow? keys = null)
{
    /// <summary>
    /// The folder in the primary backup folder that records, for each generation in the bucket, the documents confirmed
    /// there (HC360's key may not list the bucket): <c>gen-yyyyMMdd-&lt;location hash&gt;.ids</c>.
    /// </summary>
    public const string LedgerFolder = "offsite";

    /// <summary>
    /// Copies that finished but did not match the backup, after which a backup is no longer uploaded again by itself: a
    /// store whose ETags are not MD5s (a bucket encrypted with KMS keys) would otherwise have every backup uploaded over
    /// and over. The warning on System health stays until a person sees to it.
    /// </summary>
    public const int MaxMismatches = 3;

    /// <summary>The sizes uploads are made in; small ones in the tests.</summary>
    internal OffsiteUploadSizes Sizes { get; init; } = OffsiteUploadSizes.Default;

    private enum Outcome
    {
        Uploaded,
        FileProblem,
        Mismatch,
        StoreFailed,
    }

    /// <summary>One run: reads the settings and uploads what is due. Does nothing while the copies are switched off.</summary>
    public async Task<BackupOffsiteRun> RunAsync(CancellationToken ct)
    {
        var config = await BackupOffsite.ReadAsync(settings, logger, ct);
        if (!config.Enabled)
        {
            return BackupOffsiteRun.Nothing;
        }

        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var cutoff = now - TimeSpan.FromDays(Math.Clamp(config.Settings!.BackfillDays, 0, BackupOffsite.MaxBackfillDays));
        var full = db.Set<BackupRun>()
            .Where(r => r.Status == BackupStatus.Succeeded && r.VerifiedAt != null && r.FileName != null
                        && (r.Kind == BackupKind.Scheduled || r.Kind == BackupKind.Manual));
        var newest = await full.OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
        var recent = await full.Where(r => r.OffsiteUploadedAt == null && r.StartedAt >= cutoff).ToListAsync(ct);
        var candidates = BackupOffsite.Candidates(newest is null ? recent : recent.Append(newest).DistinctBy(r => r.Id), config.Settings.BackfillDays, now);
        if (candidates.Count == 0)
        {
            return BackupOffsiteRun.Nothing;
        }

        if (!config.Ready)
        {
            // Recorded as the newest backup's attempt, so System health counts the time nothing could be uploaded.
            var newestPending = candidates[0];
            newestPending.OffsiteAttempts++;
            newestPending.OffsiteError = Truncate(config.Problem ?? "The off-site copy cannot be made.");
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Backups are not uploaded off site: {Problem}", config.Problem);
            return new BackupOffsiteRun(0, 1, Stopped: true);
        }

        var client = new S3Client(httpClients.CreateClient(BackupOffsite.HttpClientName), config.Location!, config.Credentials!, time);
        return await UploadAsync(client, config.Settings, candidates, token => db.SaveChangesAsync(token), ct);
    }

    /// <summary>
    /// Uploads <paramref name="candidates"/> in order through <paramref name="client"/>, saving each run's record with
    /// <paramref name="save"/> after every step, until all are off site or the store fails.
    /// </summary>
    internal async Task<BackupOffsiteRun> UploadAsync(
        S3Client client, BackupOffsiteSettings settings, IReadOnlyList<BackupRun> candidates, Func<CancellationToken, Task> save, CancellationToken ct)
    {
        var uploaded = 0;
        var failed = 0;
        foreach (var run in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await UploadAsync(client, settings, run, save, ct);
            if (outcome == Outcome.Uploaded)
            {
                uploaded++;
                continue;
            }

            failed++;
            if (outcome == Outcome.StoreFailed)
            {
                return new BackupOffsiteRun(uploaded, failed, Stopped: true);
            }
        }

        return new BackupOffsiteRun(uploaded, failed, Stopped: false);
    }

    private async Task<Outcome> UploadAsync(S3Client client, BackupOffsiteSettings settings, BackupRun run, Func<CancellationToken, Task> save, CancellationToken ct)
    {
        var fileName = run.FileName!;
        var key = BackupOffsite.ObjectKey(settings.Prefix?.Trim(), fileName);
        var location = client.Location.Describe(key);
        var state = OffsiteUploadState.Read(run.OffsiteUpload);
        if (state is not null && state.Location != location)
        {
            // The bucket or prefix changed since: the upload starts again where the settings say now.
            state = null;
        }

        run.OffsiteAttempts++;
        run.OffsiteKey = key;
        var mismatchesSoFar = state?.Mismatches ?? 0;
        if (mismatchesSoFar >= MaxMismatches)
        {
            return await FileProblemAsync(run, save,
                $"The copies uploaded {mismatchesSoFar} times did not match the backup when read back from the store, so it is no longer uploaded by itself. " +
                "Check that the bucket encrypts with keys the store manages (SSE-S3), not KMS keys, whose ETags are not checksums, then take a backup under Administration › Backups.");
        }

        if (Locate(fileName) is not { } file)
        {
            return await FileProblemAsync(run, save, $"{fileName} is no longer on this server's disk, nor on the second disk or NAS, so it cannot be uploaded.");
        }

        var length = new FileInfo(file).Length;
        if (run.SizeBytes is { } made && made != length)
        {
            return await FileProblemAsync(run, save,
                string.Create(CultureInfo.InvariantCulture, $"{fileName} is {length:N0} bytes now, and was {made:N0} when it was made and checked: it is not uploaded."));
        }

        // The documents a format 3 backup needs go up first, into the bucket's generation: the backup is uploaded only once
        // every one of them is confirmed there, so a backup off site always has its documents (F22).
        if (await UploadDocumentsAsync(client, settings, run, file, location, save, ct) is { } stopped)
        {
            return stopped;
        }

        var objectLock = settings.RetainDays > 0 ? new S3ObjectLock(time.GetUtcNow() + TimeSpan.FromDays(settings.RetainDays)) : null;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (run.Sha256 is { Length: > 0 } sha256)
        {
            metadata[BackupOffsite.Sha256Metadata] = sha256.ToLowerInvariant();
        }

        if (run.RecoveryKeyId is { Length: > 0 } keyId)
        {
            metadata[BackupOffsite.RecoveryKeyMetadata] = keyId;
        }

        try
        {
            string expectedETag;
            var joinedBefore = false;
            if (length <= Sizes.SinglePutLimit && state?.UploadId is null)
            {
                var body = await File.ReadAllBytesAsync(file, ct);
                if (run.Sha256 is { Length: > 0 } expected && !string.Equals(SigV4.HexSha256(body), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return await FileProblemAsync(run, save, $"{fileName} no longer matches the SHA-256 it was checked with when it was made, so it is not uploaded: the server's disk may be failing.");
                }

                await client.PutObjectAsync(key, body, body.Length, objectLock, metadata, ct);
                expectedETag = Convert.ToHexStringLower(S3Client.Md5(body));
                state = null;
            }
            else
            {
                var multipart = await MultipartAsync(client, run, file, length, key, location, state, objectLock, metadata, save, ct);
                if (multipart is null)
                {
                    return Outcome.FileProblem;
                }

                (expectedETag, joinedBefore, state) = multipart.Value;
            }

            var stored = await client.HeadObjectAsync(key, ct);
            if (Mismatch(stored, length, expectedETag, run.Sha256) is { } mismatch)
            {
                if (joinedBefore)
                {
                    // The store no longer knew the upload, and what it holds is not this backup: start again.
                    throw new S3Exception(System.Net.HttpStatusCode.NotFound, "NoSuchUpload", "The store no longer knows the upload, and holds no matching copy.");
                }

                var mismatches = mismatchesSoFar + 1;
                run.OffsiteUpload = new OffsiteUploadState(location, null, 0, [], Mismatches: mismatches).Write();
                run.OffsiteError = Truncate($"{mismatch} It is uploaded again at the next run.");
                await save(ct);
                logger.LogError("The off-site copy of backup {File} at {Location} does not match it: {Mismatch}", fileName, location, mismatch);
                return Outcome.Mismatch;
            }

            run.OffsiteUploadedAt = time.GetUtcNow();
            run.OffsiteSizeBytes = stored!.Length;
            run.OffsiteError = null;
            run.OffsiteUpload = null;
            await save(ct);
            logger.LogInformation("Backup {File} uploaded off site to {Location} and checked ({Size:N0} bytes)", fileName, location, length);
            return Outcome.Uploaded;
        }
        catch (S3Exception ex) when (ex.NoSuchUpload)
        {
            // Aborted, or removed by the bucket's clean-up of unfinished uploads: the next run starts it again.
            run.OffsiteUpload = mismatchesSoFar == 0 ? null : new OffsiteUploadState(location, null, 0, [], Mismatches: mismatchesSoFar).Write();
            return await StoreFailedAsync(run, save, location, ex, "The store no longer knows the upload that was under way, so it starts again at the next run.");
        }
        catch (Exception ex) when (ex is S3Exception or HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return await StoreFailedAsync(run, save, location, ex, ex switch
            {
                S3Exception s3 => s3.Message,
                TaskCanceledException => string.Create(CultureInfo.InvariantCulture, $"The store did not answer within {BackupOffsite.RequestTimeout.TotalMinutes:0} minutes."),
                HttpRequestException http => $"The store could not be reached: {http.Message}",
                _ => $"The upload was cut off: {ex.Message}",
            });
        }
    }

    /// <summary>
    /// The multipart upload of a large file: started (or taken up where it stopped), each part not yet in the store sent and
    /// saved, the file checked again in full, and the parts joined. Returns the ETag the joined copy should have, whether
    /// the store had already joined it (its answer lost), and the state; null when the file failed its check.
    /// </summary>
    private async Task<(string ETag, bool JoinedBefore, OffsiteUploadState State)?> MultipartAsync(
        S3Client client, BackupRun run, string file, long length, string key, string location, OffsiteUploadState? state, S3ObjectLock? objectLock,
        IReadOnlyDictionary<string, string> metadata, Func<CancellationToken, Task> save, CancellationToken ct)
    {
        if (state?.UploadId is null)
        {
            var uploadId = await client.CreateMultipartUploadAsync(key, objectLock, metadata, ct);
            state = new OffsiteUploadState(location, uploadId, Sizes.PartSizeFor(length), [], Mismatches: state?.Mismatches ?? 0, Length: length);
            run.OffsiteUpload = state.Write();
            await save(ct);
        }

        var partSize = state.PartSize;
        var count = (int)((length + partSize - 1) / partSize);
        if (count > OffsiteUploadSizes.MaxParts)
        {
            throw new IOException($"{Path.GetFileName(file)} would need {count:N0} parts, more than the store takes.");
        }

        var parts = state.Parts.ToDictionary(p => p.Number);
        var buffer = new byte[Math.Min(partSize, length)];
        await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
        {
            for (var number = 1; number <= count; number++)
            {
                if (parts.ContainsKey(number))
                {
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                var offset = (number - 1) * partSize;
                var size = (int)Math.Min(partSize, length - offset);
                stream.Position = offset;
                await stream.ReadExactlyAsync(buffer.AsMemory(0, size), ct);
                var md5 = Convert.ToBase64String(S3Client.Md5(buffer.AsSpan(0, size)));
                var etag = await client.UploadPartAsync(key, state.UploadId!, number, buffer, size, ct);
                parts[number] = new OffsiteUploadPart(number, etag, md5);
                state = state with { Parts = [.. parts.Values.OrderBy(p => p.Number)] };
                run.OffsiteUpload = state.Write();
                await save(ct);
            }
        }

        if (!state.Checked)
        {
            if (await LocalProblemAsync(file, length, state, run.Sha256, ct) is { } problem)
            {
                try
                {
                    await client.AbortMultipartUploadAsync(key, state.UploadId!, ct);
                }
                catch (Exception ex) when (ex is S3Exception or HttpRequestException)
                {
                    // The bucket's clean-up of unfinished uploads removes the parts in any case.
                    logger.LogWarning(ex, "The abandoned off-site upload of {File} could not be aborted", run.FileName);
                }

                run.OffsiteUpload = null;
                await FileProblemAsync(run, save, problem);
                return null;
            }

            state = state with { Checked = true };
            run.OffsiteUpload = state.Write();
            await save(ct);
        }

        var joinedBefore = false;
        try
        {
            await client.CompleteMultipartUploadAsync(key, state.UploadId!, [.. state.Parts.Select(p => new S3Part(p.Number, p.ETag))], ct);
        }
        catch (S3Exception ex) when (ex.NoSuchUpload)
        {
            // Joined at an earlier run whose answer was lost: what the store holds is read back, as for any upload.
            joinedBefore = true;
        }

        return (MultipartETag(state.Parts), joinedBefore, state);
    }

    /// <summary>
    /// Reads the file again in full: its SHA-256 must be the one the backup was checked with, and each part the MD5 of what
    /// was sent for it, so the copy being joined is the verified backup and nothing else.
    /// </summary>
    private static async Task<string?> LocalProblemAsync(string file, long length, OffsiteUploadState state, string? sha256, CancellationToken ct)
    {
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Math.Min(state.PartSize, length)];
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        foreach (var part in state.Parts.OrderBy(p => p.Number))
        {
            var offset = (part.Number - 1) * state.PartSize;
            var size = (int)Math.Min(state.PartSize, length - offset);
            stream.Position = offset;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, size), ct);
            whole.AppendData(buffer, 0, size);
            if (Convert.ToBase64String(S3Client.Md5(buffer.AsSpan(0, size))) != part.Md5)
            {
                return $"{Path.GetFileName(file)} changed while it was being uploaded (part {part.Number} reads differently now), so the upload was given up: the server's disk may be failing.";
            }
        }

        return sha256 is { Length: > 0 } && !string.Equals(Convert.ToHexStringLower(whole.GetHashAndReset()), sha256, StringComparison.OrdinalIgnoreCase)
            ? $"{Path.GetFileName(file)} no longer matches the SHA-256 it was checked with when it was made, so the upload was given up: the server's disk may be failing."
            : null;
    }

    /// <summary>The ETag S3 gives an object joined from <paramref name="parts"/>: the MD5 of their MD5s, a dash, and how many there are.</summary>
    internal static string MultipartETag(IReadOnlyList<OffsiteUploadPart> parts)
    {
        var digests = parts.OrderBy(p => p.Number).SelectMany(p => Convert.FromBase64String(p.Md5)).ToArray();
        return string.Create(CultureInfo.InvariantCulture, $"{Convert.ToHexStringLower(S3Client.Md5(digests))}-{parts.Count}");
    }

    /// <summary>
    /// Why the copy the store holds is not the backup, or null when it is: its size, its SHA-256 metadata, and its ETag where
    /// the store gives an MD5-based one (one PUT: the file's MD5; parts: the MD5 of the parts' MD5s and their count). A store
    /// whose ETags are of another kind is judged by the size, the metadata and the MD5 it checked on each request.
    /// </summary>
    internal static string? Mismatch(S3ObjectInfo? stored, long length, string expectedETag, string? sha256)
    {
        if (stored is null)
        {
            return "The store says it holds nothing under the key just uploaded.";
        }

        if (stored.Length != length)
        {
            return string.Create(CultureInfo.InvariantCulture, $"The copy in the bucket is {stored.Length:N0} bytes, and the backup {length:N0}.");
        }

        if (sha256 is { Length: > 0 } && stored.Metadata.TryGetValue(BackupOffsite.Sha256Metadata, out var described)
            && !string.Equals(described, sha256, StringComparison.OrdinalIgnoreCase))
        {
            return "The copy in the bucket is described as another backup (its SHA-256 metadata differs).";
        }

        var etag = stored.ETag?.ToLowerInvariant();
        var md5Based = expectedETag.Contains('-', StringComparison.Ordinal)
            ? etag is not null && Regex.IsMatch(etag, "^[0-9a-f]{32}-[0-9]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            : etag is not null && Regex.IsMatch(etag, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return md5Based && etag != expectedETag
            ? $"The copy in the bucket does not match the backup: its checksum (ETag {etag}) is not the one sent ({expectedETag})."
            : null;
    }

    /// <summary>
    /// Uploads the documents a format 3 backup needs (its <c>.refs</c> list, beside it) that the bucket's current generation
    /// does not have yet, each checked against its SHA-256 before it goes, locked until its generation's last backup has
    /// expired, and read back. Returns null when all are there; otherwise the outcome the backup's upload stops with.
    /// An older backup, with its documents inside, has no list and needs none uploaded.
    /// </summary>
    private async Task<Outcome?> UploadDocumentsAsync(
        S3Client client, BackupOffsiteSettings settings, BackupRun run, string archive, string location, Func<CancellationToken, Task> save, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(archive)!;
        var fileName = Path.GetFileName(archive);
        if (!File.Exists(Path.ChangeExtension(archive, BackupStore.ReferencesExtension)))
        {
            return File.Exists(Path.Combine(folder, BackupStore.MarkerFileName))
                ? await FileProblemAsync(run, save, $"{fileName} has no list of the documents it needs beside it, so it is not uploaded without them.")
                : null;
        }

        if (keys is null)
        {
            return await FileProblemAsync(run, save, $"The list of the documents {fileName} needs cannot be checked on this server, so it is not uploaded.");
        }

        IReadOnlyList<string> ids;
        var masterKey = keys.ExportMasterKey();
        var referencesKey = FieldMasterKey.DeriveKey(masterKey, BackupStore.ReferencesKeyPurpose);
        try
        {
            var store = BackupStore.Open(folder, keys.Fingerprint, referencesKey, time);
            ids = store.ReadReferences(fileName, referencesKey) ?? [];
        }
        catch (Exception ex) when (ex is InvalidDataException or BackupStoreException)
        {
            return await FileProblemAsync(run, save, $"The list of the documents {fileName} needs cannot be used, so it is not uploaded: {ex.Message}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
            CryptographicOperations.ZeroMemory(referencesKey);
        }

        if (ids.Count == 0)
        {
            return null;
        }

        var prefix = settings.Prefix?.Trim();
        var ledger = OffsiteLedger.Open(Path.Combine(directories.Backups, LedgerFolder), client.Location.Describe(BackupOffsite.ObjectKey(prefix, "")), time.GetUtcNow());
        var objectLock = settings.RetainDays > 0
            ? new S3ObjectLock(ledger.Started + TimeSpan.FromDays(settings.RetainDays + BackupStore.GenerationDays))
            : null;
        var uploaded = 0;
        foreach (var id in ids.Where(id => !ledger.Has(id)))
        {
            ct.ThrowIfCancellationRequested();
            byte[]? body = null;
            foreach (var candidate in BackupBlobs.Candidates(folder, id))
            {
                var data = await File.ReadAllBytesAsync(candidate, ct);
                if (string.Equals(SigV4.HexSha256(data), id, StringComparison.Ordinal))
                {
                    body = data;
                    break;
                }
            }

            if (body is null)
            {
                return await FileProblemAsync(run, save,
                    $"A document {fileName} needs ({id}) has no good copy in {folder}, so the backup is not uploaded: the disk may be failing.");
            }

            var key = BackupOffsite.ObjectKey(prefix, $"{ledger.Generation}/{BackupStore.BlobsFolder}/{id[..2]}/{id[2..4]}/{id}{BackupStore.BlobExtension}");
            try
            {
                await client.PutObjectAsync(key, body, body.Length, objectLock,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [BackupOffsite.Sha256Metadata] = id }, ct);
                var stored = await client.HeadObjectAsync(key, ct);
                if (Mismatch(stored, body.Length, Convert.ToHexStringLower(S3Client.Md5(body)), id) is { } mismatch)
                {
                    throw new IOException($"A document's copy in the bucket does not match what was sent: {mismatch}");
                }
            }
            catch (Exception ex) when (ex is S3Exception or HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                return await StoreFailedAsync(run, save, location, ex,
                    $"The documents of {fileName} are going up into {ledger.Generation} ({uploaded} so far this run); one failed: " + (ex is HttpRequestException http
                        ? $"the store could not be reached: {http.Message}"
                        : ex.Message));
            }

            ledger.Add(id);
            uploaded++;
        }

        if (uploaded > 0)
        {
            logger.LogInformation("Uploaded {Count} new documents of backup {File} into {Generation} off site", uploaded, fileName, ledger.Generation);
        }

        return null;
    }

    /// <summary>The backup file: on this server's disk, else on the second disk or NAS, where a backup another server made is copied too.</summary>
    internal string? Locate(string fileName)
    {
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var directory in new[] { directories.Backups, options.Value.Backup.SecondaryDirectory })
        {
            if (directory is { Length: > 0 } && Path.Combine(directory, fileName) is var path && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private async Task<Outcome> FileProblemAsync(BackupRun run, Func<CancellationToken, Task> save, string problem)
    {
        run.OffsiteError = Truncate(problem);
        await save(CancellationToken.None);
        logger.LogError("Backup {File} is not uploaded off site: {Problem}", run.FileName, problem);
        return Outcome.FileProblem;
    }

    private async Task<Outcome> StoreFailedAsync(BackupRun run, Func<CancellationToken, Task> save, string location, Exception ex, string problem)
    {
        run.OffsiteError = Truncate(problem);
        await save(CancellationToken.None);
        logger.LogWarning(ex, "The off-site upload of backup {File} to {Location} failed; it is tried again at the next run", run.FileName, location);
        return Outcome.StoreFailed;
    }

    private static string Truncate(string text) => text.Length > 2000 ? text[..2000] : text;
}

/// <summary>
/// Uploads the backups off site every 15 minutes on the server that runs the jobs (<see cref="BackupOffsiteUploader"/>).
/// The schedule is on the clock, so every server that may run the jobs names the same times, and each is claimed once for
/// the hospital (<see cref="JobSlotClaim"/>); a run still uploading when the next is due makes that one wait.
/// </summary>
[DisallowConcurrentExecution]
internal sealed class BackupOffsiteJob(BackupOffsiteUploader uploader, ILogger<BackupOffsiteJob> logger) : IJob
{
    /// <summary>Every quarter of an hour, on the hour.</summary>
    public const string Schedule = "0 0/15 * * * ?";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            var run = await uploader.RunAsync(cancellationToken);
            if (run.Uploaded > 0 || run.Failed > 0)
            {
                logger.LogInformation("Off-site backup run: {Uploaded} uploaded, {Failed} not", run.Uploaded, run.Failed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The off-site backup run failed");
        }
    }
}

/// <summary>
/// The documents confirmed in the bucket's current generation (docs/design/incremental-backup.md, section 5.6), kept on the
/// server since HC360's key may not list the bucket: one file a generation and location, the location on its first line
/// and one SHA-256 a line, each added once its copy is read back from the store. A generation lasts
/// <see cref="BackupStore.GenerationDays"/> days; a new one, or a change of bucket or prefix, starts with every document again.
/// </summary>
internal sealed class OffsiteLedger
{
    private readonly string _path;
    private readonly HashSet<string> _ids;

    private OffsiteLedger(string path, string generation, DateTimeOffset started, HashSet<string> ids)
    {
        _path = path;
        Generation = generation;
        Started = started;
        _ids = ids;
    }

    /// <summary>The generation's folder in the bucket: <c>gen-yyyyMMdd</c>.</summary>
    public string Generation { get; }

    /// <summary>The start of the generation's day (UTC): its copies are locked from here for the backup period and a generation.</summary>
    public DateTimeOffset Started { get; }

    public static OffsiteLedger Open(string folder, string location, DateTimeOffset now)
    {
        Directory.CreateDirectory(folder);
        var tag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(location)))[..8];
        var generation = BackupStore.CurrentGeneration(folder, now, $"-{tag}.ids");
        var date = BackupStore.GenerationDate(generation)!.Value;
        var path = Path.Combine(folder, $"{generation}-{tag}.ids");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length > 0 && lines[0] == "location " + location)
            {
                ids.UnionWith(lines.Skip(1).Where(BackupStore.IsBlobId));
            }
        }
        else
        {
            File.WriteAllText(path, "location " + location + "\n");
        }

        return new OffsiteLedger(path, generation, new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), ids);
    }

    public bool Has(string id) => _ids.Contains(id);

    public void Add(string id)
    {
        using (var file = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            file.Write(Encoding.UTF8.GetBytes(id + "\n"));
            file.Flush(flushToDisk: true);
        }

        _ids.Add(id);
    }
}
