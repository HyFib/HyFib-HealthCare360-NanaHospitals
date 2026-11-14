using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Numbering;

/// <summary>
/// One series' counter as the high-water mark keeps it: the last number issued (<see cref="Value"/>) in
/// <see cref="Period"/>, with the series' reset and format, which a series the database has lost is made again with.
/// </summary>
public sealed record NumberSeriesMark(Guid? FacilityId, string Key, string? Period, long Value, NumberResetPolicy ResetPolicy, string Format);

/// <summary>
/// The high-water mark of every number series, as it was at <see cref="WrittenAt"/>. <see cref="Database"/> is the database
/// it was taken from, by the id of the hospital's first facility: a restore brings that back with the rest, and a new
/// database (a hospital going live on a database of its own after training) has another.
/// </summary>
public sealed record NumberSeriesMarks(Guid Database, DateTimeOffset WrittenAt, IReadOnlyList<NumberSeriesMark> Series);

/// <summary>
/// A series start-up moves past the high-water mark: its counter becomes <see cref="Value"/> in <see cref="Period"/>.
/// <see cref="Before"/> is the counter the database had, or null when the database had no row for the series (its first
/// number was issued after the backup was made); <see cref="Mark"/> is what the mark recorded.
/// </summary>
public sealed record NumberSeriesRaise(NumberSeriesMark? Before, NumberSeriesMark Mark, string? Period, long Value);

/// <summary>
/// The rules of the high-water mark (<see cref="NumberSeriesHighWaterMark"/>), as pure functions: when a counter in the
/// database is behind the mark, how far it is raised, and what the mark keeps.
/// <para>
/// A counter is behind when the last number it would count from now (in the period it counts in now: this year, this
/// month, this day, or ever) is below the last number the mark records in that period. Only a restore puts a counter
/// back: an administrator's next number must be above the last one issued, and a change of reset keeps counting from it
/// (<see cref="NumberFormat.CounterAfterChange"/>). A mark of a reset the database no longer has counts where their periods
/// overlap ("2026" and "202611" do), so a reset changed after the backup never hides numbers issued under the other.
/// </para>
/// </summary>
public static class NumberSeriesHighWater
{
    /// <summary>
    /// How far past the last number the mark records a series is raised: room for the numbers issued after the mark was
    /// last written, which is at most a minute before a power cut (the mark is written every minute while numbers are
    /// issued, and as the server stops). A raised series leaves this gap; the audit trail says why.
    /// </summary>
    public const long Margin = 100;

    /// <summary>The last number <paramref name="mark"/> records in <paramref name="period"/>, the period a series counts in now; 0 when its periods do not overlap.</summary>
    public static long LastIssuedIn(NumberSeriesMark mark, string? period)
    {
        ArgumentNullException.ThrowIfNull(mark);
        return mark.Period is null || period is null
            || mark.Period.StartsWith(period, StringComparison.Ordinal) || period.StartsWith(mark.Period, StringComparison.Ordinal)
            ? mark.Value
            : 0;
    }

    /// <summary>Whether <paramref name="counter"/>, as the database has it, is behind <paramref name="mark"/> at <paramref name="localNow"/> (hospital time).</summary>
    public static bool IsBehind(NumberSeriesMark counter, NumberSeriesMark mark, DateTimeOffset localNow)
    {
        ArgumentNullException.ThrowIfNull(counter);
        return NumberFormat.LastIssued(counter.Value, counter.Period, counter.ResetPolicy, localNow)
            < LastIssuedIn(mark, NumberFormat.PeriodFor(counter.ResetPolicy, localNow));
    }

    /// <summary>
    /// The series the database (<paramref name="counters"/>) has behind <paramref name="marks"/>, each raised past the last
    /// number the mark records in the period it counts in now, by <paramref name="margin"/>; and the series the database
    /// lacks that issued numbers in their current period, made again likewise. A series the mark does not know is left as
    /// it is, as is one whose numbers the mark has only in an earlier period (the series starts again from 1 anyway).
    /// </summary>
    public static IReadOnlyList<NumberSeriesRaise> Raises(
        IReadOnlyList<NumberSeriesMark> counters, IReadOnlyList<NumberSeriesMark> marks, DateTimeOffset localNow, long margin = Margin)
    {
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(marks);
        var rows = counters.ToDictionary(c => (c.Key, c.FacilityId));
        var raises = new List<NumberSeriesRaise>();
        foreach (var mark in marks.DistinctBy(m => (m.Key, m.FacilityId)))
        {
            if (rows.TryGetValue((mark.Key, mark.FacilityId), out var counter))
            {
                if (IsBehind(counter, mark, localNow))
                {
                    var period = NumberFormat.PeriodFor(counter.ResetPolicy, localNow);
                    raises.Add(new NumberSeriesRaise(counter, mark, period, LastIssuedIn(mark, period) + margin));
                }

                continue;
            }

            var current = NumberFormat.PeriodFor(mark.ResetPolicy, localNow);
            if (LastIssuedIn(mark, current) is > 0 and var last)
            {
                raises.Add(new NumberSeriesRaise(null, mark, current, last + margin));
            }
        }

        return raises;
    }

    /// <summary>
    /// The mark to write from the database's <paramref name="counters"/> and the mark written before
    /// (<paramref name="previous"/>): each series as the database has it, except one the database has behind the mark, or
    /// in an earlier period than the mark's, which keeps the mark's counter, as does a series the database lacks. So a
    /// database restored while the server ran does not lower the mark before start-up has raised the series past it. A
    /// series the database lacks whose numbers the mark has only in an earlier period protects nothing, and is dropped.
    /// </summary>
    public static IReadOnlyList<NumberSeriesMark> Merge(IReadOnlyList<NumberSeriesMark>? previous, IReadOnlyList<NumberSeriesMark> counters, DateTimeOffset localNow)
    {
        ArgumentNullException.ThrowIfNull(counters);
        var marks = (previous ?? []).DistinctBy(m => (m.Key, m.FacilityId)).ToDictionary(m => (m.Key, m.FacilityId));
        var merged = new List<NumberSeriesMark>();
        foreach (var counter in counters)
        {
            merged.Add(marks.Remove((counter.Key, counter.FacilityId), out var mark) && (IsBehind(counter, mark, localNow) || IsLater(mark, counter))
                ? mark
                : counter);
        }

        merged.AddRange(marks.Values.Where(m => LastIssuedIn(m, NumberFormat.PeriodFor(m.ResetPolicy, localNow)) > 0));
        return [.. merged.OrderBy(m => m.Key, StringComparer.Ordinal).ThenBy(m => m.FacilityId)];
    }

    /// <summary>
    /// Every series' counter as the database has it now. Read without query filters: the allocation's own statements see
    /// every row.
    /// </summary>
    public static async Task<IReadOnlyList<NumberSeriesMark>> ReadCountersAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Set<NumberSeries>().IgnoreQueryFilters().AsNoTracking()
            .Select(s => new NumberSeriesMark(s.FacilityId, s.Key, s.CurrentPeriod, s.CurrentValue, s.ResetPolicy, s.Format))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The database's id for the mark (<see cref="NumberSeriesMarks.Database"/>): its first facility's; null on a new database, before its first start has made one.</summary>
    public static async Task<Guid?> DatabaseIdAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.Set<Facility>().IgnoreQueryFilters().AsNoTracking()
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>A counter of a period after the database's, of the same reset: numbers issued while the server's clock was ahead, or before a restore.</summary>
    private static bool IsLater(NumberSeriesMark mark, NumberSeriesMark counter) =>
        mark.ResetPolicy == counter.ResetPolicy && mark.Period is not null && counter.Period is not null
        && string.CompareOrdinal(mark.Period, counter.Period) > 0;
}

/// <summary>
/// The high-water mark of the number series, kept outside the database: the UHID, visit, bill, receipt and GST invoice
/// counters are rows of <c>platform.number_series</c>, so a restore puts them back to where they were when the backup was
/// made, and every number issued after it (on cards, wristbands and GST invoices already printed) would be issued again.
/// Start-up raises every series behind the mark past it (<see cref="NumberSeriesStartupCheck"/>).
/// <list type="bullet">
/// <item>The file is in the data directory, or the folder the servers share when there are several
/// (<c>numbering/number-series-high-water-mark.json</c>), and is copied to the backups' second copy
/// (<c>HealthCare360:Backup:SecondaryDirectory</c>) when one is set, so that it outlives the Server Node's own disk. Reading
/// takes the newest of the two.</item>
/// <item>The node that runs the jobs writes it (<see cref="NumberSeriesHighWaterJob"/>): every minute when a counter
/// changed, and as the server stops. Each write replaces the whole file, through a file of its own renamed over it, so a
/// write cut short leaves the last one whole. One that fails is logged and tried again; numbers are issued as before.</item>
/// <item>It is sealed with the audit seal key (HMAC-SHA256), which a backup carries and a restore puts back: one written
/// by anyone without the key, or by another installation (a training server sharing the backup share), is not used, so
/// nobody who can write the folder can make the server's numbers jump.</item>
/// </list>
/// </summary>
public sealed class NumberSeriesHighWaterMark
{
    public const string FileName = "number-series-high-water-mark.json";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    private readonly AuditSealKey _key;
    private readonly ILogger<NumberSeriesHighWaterMark> _logger;
    private readonly Lock _gate = new();
    private bool _loaded;
    private NumberSeriesMarks? _last;
    private NumberSeriesMarks? _inFile;
    private NumberSeriesMarks? _inMirror;
    private bool _mirrorFailing;

    public NumberSeriesHighWaterMark(DataDirectories directories, IOptions<PlatformOptions> options, AuditSealKey key, ILogger<NumberSeriesHighWaterMark> logger)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(options);
        _key = key;
        _logger = logger;
        Path = System.IO.Path.Combine(directories.Shared ?? directories.Root, "numbering", FileName);
        MirrorPath = options.Value.Backup.SecondaryDirectory is { Length: > 0 } secondary
            ? System.IO.Path.Combine(System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(secondary)), FileName)
            : null;
    }

    /// <summary>The mark in the data directory (in the shared folder when there are several servers).</summary>
    public string Path { get; }

    /// <summary>Its copy with the backups' second copy, or null when no second copy is set.</summary>
    public string? MirrorPath { get; }

    /// <summary>
    /// The newest mark of <paramref name="database"/> in the two copies; null when neither holds one. A copy that does not
    /// read, whose seal does not verify, or that was taken from another database, is logged and passed over.
    /// </summary>
    public NumberSeriesMarks? ReadNewest(Guid database)
    {
        var marks = new List<NumberSeriesMarks>();
        foreach (var path in Copies())
        {
            if (ReadFile(path) is not { } mark)
            {
                continue;
            }

            if (mark.Database != database)
            {
                _logger.LogWarning(
                    "The number series' high-water mark at {Path}, written {WrittenAt:u}, was taken from another database (first facility {Database}, not {Current}); it is not used",
                    path, mark.WrittenAt, mark.Database, database);
                continue;
            }

            marks.Add(mark);
        }

        return marks.MaxBy(m => m.WrittenAt);
    }

    /// <summary>
    /// Records the counters of <paramref name="database"/> (<see cref="NumberSeriesHighWater.Merge"/> with the mark as
    /// last written): writes the file when the mark changed, or the file does not hold it yet, and then the copy likewise.
    /// True when the file was written. Throws when the file cannot be written; a copy that cannot be written is logged, and
    /// written again next time.
    /// </summary>
    public bool Record(Guid database, IReadOnlyList<NumberSeriesMark> counters, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(counters);
        lock (_gate)
        {
            if (!_loaded)
            {
                _inFile = ReadFile(Path);
                _inMirror = MirrorPath is { } copy ? ReadFile(copy) : null;
                _last = new[] { _inFile, _inMirror }.OfType<NumberSeriesMarks>().Where(m => m.Database == database).MaxBy(m => m.WrittenAt);
                _loaded = true;
            }

            var previous = _last?.Database == database ? _last.Series : null;
            var merged = NumberSeriesHighWater.Merge(previous, counters, NumberFormat.ToHospitalTime(now));
            var mark = new NumberSeriesMarks(database, now.ToUniversalTime(), merged);
            _last = mark;

            var written = false;
            if (!Holds(_inFile, mark))
            {
                WriteFile(Path, mark);
                _inFile = mark;
                written = true;
            }

            if (MirrorPath is { } mirror && !Holds(_inMirror, mark))
            {
                try
                {
                    WriteFile(mirror, mark);
                    _inMirror = mark;
                    if (_mirrorFailing)
                    {
                        _logger.LogInformation("The number series' high-water mark is copied to {Path} again", mirror);
                    }

                    _mirrorFailing = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
                {
                    if (!_mirrorFailing)
                    {
                        _logger.LogWarning(ex, "The number series' high-water mark could not be copied to {Path}; it is kept in {File}, and copied once the folder can be written", mirror, Path);
                    }

                    _mirrorFailing = true;
                }
            }

            return written;
        }
    }

    /// <summary>What is sealed: the mark's fields in a fixed order, so the seal does not depend on how the JSON is laid out.</summary>
    internal static string Canonical(NumberSeriesMarks mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        var text = new StringBuilder("v1").Append('\u001e').Append(mark.Database.ToString("N"))
            .Append('\u001e').Append(mark.WrittenAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        foreach (var series in mark.Series)
        {
            text.Append('\u001e').AppendJoin('\u001f',
                series.FacilityId?.ToString("N") ?? "-",
                series.Key,
                series.Period ?? "-",
                series.Value.ToString(CultureInfo.InvariantCulture),
                series.ResetPolicy.ToString(),
                series.Format);
        }

        return text.ToString();
    }

    private IEnumerable<string> Copies() => new[] { Path, MirrorPath }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool Holds(NumberSeriesMarks? file, NumberSeriesMarks mark) =>
        file is not null && file.Database == mark.Database && file.Series.SequenceEqual(mark.Series);

    private string Seal(NumberSeriesMarks mark) =>
        _key.Seal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(mark)))));

    private NumberSeriesMarks? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var file = JsonSerializer.Deserialize<MarkFile>(File.ReadAllText(path), Json);
            if (file is not { Version: FormatVersion, Series: not null, Mac: { Length: > 0 } }
                || file.Series.Any(s => s is null || string.IsNullOrEmpty(s.Key) || s.Format is null))
            {
                _logger.LogWarning("The number series' high-water mark at {Path} is not one this version reads; it is not used", path);
                return null;
            }

            var mark = new NumberSeriesMarks(file.Database, file.WrittenAt, file.Series);
            if (!_key.Sealed(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(mark)))), file.Mac))
            {
                _logger.LogWarning("The number series' high-water mark at {Path} is not sealed with this server's audit seal key; it is not used", path);
                return null;
            }

            return mark;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException
                                       or ArgumentException or JsonException or CryptographicException)
        {
            _logger.LogWarning(ex, "The number series' high-water mark at {Path} could not be read; it is not used", path);
            return null;
        }
    }

    private void WriteFile(string path, NumberSeriesMarks mark)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".writing";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new MarkFile(FormatVersion, mark.Database, mark.WrittenAt, mark.Series, Seal(mark)), Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private sealed record MarkFile(int Version, Guid Database, DateTimeOffset WrittenAt, IReadOnlyList<NumberSeriesMark> Series, string Mac);
}
