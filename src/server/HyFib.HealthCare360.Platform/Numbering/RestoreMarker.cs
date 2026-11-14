using System.Globalization;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Platform.Numbering;

/// <summary>
/// What Restore-HC360.ps1 writes in <c>restored.json</c> once a restore succeeded: when it ended, the backup file, and when
/// that backup was made, as <c>backup restore</c> printed it (null when it did not say).
/// </summary>
public sealed record RestoreMarkerContent(DateTimeOffset RestoredAt, string? BackupFile, DateTimeOffset? BackupTakenAt);

/// <summary>
/// The marker Restore-HC360.ps1 leaves in the data directory after a restore (<c>restored.json</c>), which the next start
/// acts on once (<see cref="NumberSeriesStartupCheck"/>) and then renames (<c>restored-&lt;time&gt;.json</c>, kept beside it).
/// </summary>
public sealed class RestoreMarker(DataDirectories directories, TimeProvider time, ILogger<RestoreMarker> logger)
{
    public const string FileName = "restored.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Path => System.IO.Path.Combine(directories.Root, FileName);

    /// <summary>
    /// The marker; null when there is none, or it cannot be opened (logged). One whose content does not read is a restore
    /// all the same, at the time the file was written.
    /// </summary>
    public RestoreMarkerContent? Read()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            var text = File.ReadAllText(Path);
            try
            {
                if (JsonSerializer.Deserialize<RestoreMarkerContent>(text, Json) is { } content && content.RestoredAt != default)
                {
                    return content;
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "The restore marker {Path} does not read; it is taken as a restore made when the file was written", Path);
            }

            return new RestoreMarkerContent(new DateTimeOffset(File.GetLastWriteTimeUtc(Path), TimeSpan.Zero), null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.LogError(ex, "The restore marker {Path} could not be read: outgoing messages are not held for the restore it marks", Path);
            return null;
        }
    }

    /// <summary>Renames the marker once it has been acted on, so that it is not acted on again. A failure is logged.</summary>
    public void Consume()
    {
        var done = System.IO.Path.Combine(directories.Root, string.Create(CultureInfo.InvariantCulture, $"restored-{time.GetUtcNow():yyyyMMdd-HHmmss}.json"));
        try
        {
            File.Move(Path, done, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The database records the restore it marks, which the next start recognises and does not act on again.
            logger.LogWarning(ex, "The restore marker {Path} was acted on but could not be renamed to {Done}", Path, done);
        }
    }
}

/// <summary>
/// The restore this server last started after (<see cref="NumberSeriesStartupCheck.RestoreSetting"/>): the marker's
/// content, when start-up acted on it, the high-water mark it used (null when none was found), and the series it raised.
/// </summary>
public sealed record ServerRestore(
    DateTimeOffset RestoredAt,
    string? BackupFile,
    DateTimeOffset? BackupTakenAt,
    DateTimeOffset StartedAt,
    DateTimeOffset? MarkWrittenAt,
    IReadOnlyList<RaisedNumberSeries> Raised);

/// <summary>
/// A series start-up raised past the high-water mark, as System health names it: what staff call its numbers, the facility
/// it counts for (null when all share it), the next number it issues now, and the one it would have issued (null when the
/// database had lost the series).
/// </summary>
public sealed record RaisedNumberSeries(string Key, string Name, string? FacilityCode, string NextNumber, string? WouldHaveBeen);

/// <summary>
/// System health after a restore: critical while the outgoing messages start-up held for it are held
/// (<see cref="NumberSeriesStartupCheck.OutboxHoldSetting"/>, reason "restore"), saying which backup the server was restored
/// from, which number series were raised, and that the held messages must be checked and released. Also critical on a
/// server whose start-up could not compare the series with the high-water mark (<see cref="NumberSeriesStartupStatus"/>).
/// </summary>
internal sealed class RestoreHealthWarnings(ISettingsService settings, NumberSeriesHighWaterMark highWater, NumberSeriesStartupStatus status)
    : IHealthWarningSource
{
    /// <summary>How many raised series the warning names; the rest are counted, and each is in the audit trail.</summary>
    internal const int Named = 8;

    public async Task<IReadOnlyList<HealthWarning>> WarningsAsync(CancellationToken cancellationToken)
    {
        var warnings = new List<HealthWarning>();
        if (status.Failure is { } failure)
        {
            warnings.Add(new("numbering.startup_check_failed", "critical",
                $"This server's start-up could not compare the number series with their high-water mark ({failure}). After a restore, numbers already issued may be issued again: fix the cause in the server log and start HC360 again."));
        }

        var hold = await settings.GetAsync<JsonElement?>(NumberSeriesStartupCheck.OutboxHoldSetting, null, cancellationToken);
        if (RestoreHold(hold) is { } held)
        {
            var restore = await NumberSeriesStartupCheck.RecordedAsync(settings, cancellationToken);
            warnings.Add(Warning(restore, held.Since, held.BackupTakenAt, highWater.Path, highWater.MirrorPath));
        }

        return warnings;
    }

    /// <summary>When outgoing messages were held for a restore, and the backup's time, from the hold's value; null when they are not held for one.</summary>
    internal static (DateTimeOffset? Since, DateTimeOffset? BackupTakenAt)? RestoreHold(JsonElement? hold)
    {
        if (hold is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String
            || !string.Equals(reason.GetString(), NumberSeriesStartupCheck.RestoreHoldReason, StringComparison.Ordinal))
        {
            return null;
        }

        return (Time(value, "since"), Time(value, "backupTakenAt"));
    }

    /// <summary>The warning's words for <paramref name="restore"/> (null when start-up's record of it is gone: the hold's own times are used).</summary>
    internal static HealthWarning Warning(ServerRestore? restore, DateTimeOffset? heldSince, DateTimeOffset? backupTakenAt, string markPath, string? mirrorPath)
    {
        var text = new StringBuilder();
        var takenAt = restore?.BackupTakenAt ?? backupTakenAt;
        var file = restore?.BackupFile is { Length: > 0 } path ? System.IO.Path.GetFileName(path.Replace('\\', '/')) : null;
        text.Append(CultureInfo.InvariantCulture, $"HC360 was restored from {(takenAt is { } at ? $"the backup of {At(at)}" : "a backup")}");
        if (file is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $" ({file})");
        }

        text.Append(restore is not null ? $" on {At(restore.RestoredAt)}. " : ". ");
        if (restore is null)
        {
            text.Append("Which number series start-up moved past the numbers issued before it is in the audit trail (numbering.raised). ");
        }
        else if (restore.MarkWrittenAt is null)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"No high-water mark of the number series was found ({markPath}{(mirrorPath is null ? string.Empty : " or " + mirrorPath)}), so no series was moved past the numbers issued before the restore: compare the last UHID, visit, bill and receipt numbers issued before it with Hospital setup › Number formats, and set each next number past them. ");
        }
        else if (restore.Raised.Count == 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"Every number series was already past the last numbers issued before it (high-water mark of {At(restore.MarkWrittenAt.Value)}). ");
        }
        else
        {
            text.Append(CultureInfo.InvariantCulture,
                $"So that numbers already printed on cards, wristbands and invoices are not issued again, start-up moved {restore.Raised.Count} number series {NumberSeriesHighWater.Margin} past the last numbers issued before it (high-water mark of {At(restore.MarkWrittenAt.Value)}): ");
            text.AppendJoin("; ", restore.Raised.Take(Named).Select(Raised));
            text.Append(restore.Raised.Count > Named
                ? string.Create(CultureInfo.InvariantCulture, $"; and {restore.Raised.Count - Named} more, each in the audit trail (numbering.raised). ")
                : ". ");
        }

        text.Append(CultureInfo.InvariantCulture,
            $"Outgoing messages are held{(heldSince is { } since ? $" since {At(since)}" : string.Empty)}: some were sent after the backup was made and would go again. Check them, then release them.");
        return new HealthWarning("system.restored", "critical", text.ToString());
    }

    private static string Raised(RaisedNumberSeries series) =>
        series.FacilityCode is { } code
            ? Describe($"{series.Name} at {code}", series)
            : Describe(series.Name, series);

    private static string Describe(string name, RaisedNumberSeries series) =>
        series.WouldHaveBeen is { } before ? $"{name}, next {series.NextNumber} instead of {before}" : $"{name}, next {series.NextNumber}";

    private static string At(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(JsonElement value, string property) =>
        value.TryGetProperty(property, out var time) && time.ValueKind == JsonValueKind.String && time.TryGetDateTimeOffset(out var at) ? at : null;
}
