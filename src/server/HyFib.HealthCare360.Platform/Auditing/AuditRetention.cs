using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Platform.Auditing;

/// <summary>
/// How long the audit trail is kept (ADR-015): permanently, unless super administrators (<see cref="Security.PlatformPermissions.SystemManage"/>),
/// two people agreeing, set a number of years, never fewer than <see cref="MinimumYears"/>. The audit trail records changes
/// to bills and payments too, and books of account, with their audit trail, are kept 8 years (Companies Act 2013 s.128(5),
/// Companies (Accounts) Rules 2014 r.3(1)); the DPDP Rules' year of logs is less. A period removes nothing by itself: the
/// months it no longer keeps are removed only by a purge another super administrator approves
/// (<see cref="AuditRetentionService"/>), which exports them first and re-anchors the chain where it then starts.
/// </summary>
public static partial class AuditRetention
{
    /// <summary>The setting that holds the period, in years; the Privacy module's retention rule <c>audit</c> reads it.</summary>
    public const string YearsSetting = "privacy.retention.audit";

    public const int MinimumYears = 8;

    public const int MaximumYears = RecordRetention.MaximumYears;

    /// <summary>The approval that changes the period.</summary>
    public const string ChangeKind = "audit.retention";

    /// <summary>The approval that purges the months the period no longer keeps.</summary>
    public const string PurgeKind = "audit.purge";

    /// <summary>What both approvals are about: the audit trail, one for the whole hospital.</summary>
    public const string SubjectType = "AuditTrail";

    public static readonly Guid SubjectId = new("0193a0d1-7a0e-7c35-9f42-5e5d1a7c0a15");

    /// <summary>The audit entry of a change to the period.</summary>
    public const string ChangedAction = "audit.retention.changed";

    /// <summary>The audit entry of a purge, made after it.</summary>
    public const string PurgedAction = "audit.purged";

    public const string Basis =
        "The audit trail also records changes to bills and payments: books of account and their audit trail are kept 8 years " +
        "(Companies Act 2013 s.128(5), Companies (Accounts) Rules 2014 r.3(1)), as the retention schedule keeps bills and payments.";

    public static readonly string MinimumMessage = string.Create(CultureInfo.InvariantCulture,
        $"Keep the audit trail at least {MinimumYears} years: it also records changes to bills and payments, and books of account and their audit trail are kept {MinimumYears} years (Companies Act 2013 s.128(5)).");

    /// <summary>Why <paramref name="years"/> cannot be the period, or null when it can; null years is permanently.</summary>
    public static string? Problem(int? years) => years switch
    {
        null => null,
        < MinimumYears => MinimumMessage,
        > MaximumYears => string.Create(CultureInfo.InvariantCulture, $"Keep the audit trail at most {MaximumYears} years, or permanently."),
        _ => null,
    };

    /// <summary>The period in force for a stored value: none, permanently; else within the allowed range.</summary>
    public static int? Effective(int? stored) => stored is { } years ? Math.Clamp(years, MinimumYears, MaximumYears) : null;

    /// <summary>The period in force; null while the audit trail is kept permanently.</summary>
    public static async Task<int?> YearsAsync(ISettingsService settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Effective(await settings.GetAsync<JsonElement?>(YearsSetting, null, ct) switch
        {
            { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var v) => v,
            { ValueKind: JsonValueKind.String } s when int.TryParse(s.GetString(), CultureInfo.InvariantCulture, out var v) => v,
            _ => null,
        });
    }

    /// <summary>The period in words: "permanently" or "for 8 years".</summary>
    public static string Words(int? years) => years is { } y ? string.Create(CultureInfo.InvariantCulture, $"for {y} years") : "permanently";

    /// <summary>
    /// The first month a purge keeps under a period of <paramref name="years"/>: <paramref name="now"/> (UTC) less the
    /// period, rounded down to the month's first day. Every month before it is older than the period, whole.
    /// </summary>
    public static DateOnly CutoffMonth(DateTimeOffset now, int years)
    {
        var at = now.UtcDateTime.AddYears(-years);
        return new DateOnly(at.Year, at.Month, 1);
    }

    /// <summary>The start of a UTC month as an instant.</summary>
    public static DateTimeOffset Start(DateOnly month) => new(month.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>The month an audit partition holds (<c>audit_events_2015_03</c>); null for the default partition or any other.</summary>
    public static DateOnly? MonthOfPartition(string partition)
    {
        var match = PartitionName().Match(partition);
        return match.Success
            && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var year)
            && int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var month)
            && month is >= 1 and <= 12 && year >= 1
            ? new DateOnly(year, month, 1)
            : null;
    }

    /// <summary>"March 2015".</summary>
    public static string MonthWords(DateOnly month) => month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// Why the months a purge would take cannot go now, or null when they can (<see cref="AuditPurgeFacts"/>): there are
    /// some; every record in them is sealed; none from before the cut-off waits in the default partition, which is never
    /// removed; at least one sealed record is kept after them; and their chain positions are the start of the chain (from
    /// position 1, or from the last purge), contiguous, every one of them before every position kept.
    /// </summary>
    public static string? PurgeProblem(AuditPurgeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var cutoff = MonthWords(facts.CutoffMonth);
        if (facts.Months == 0)
        {
            return $"No month of the audit trail is entirely before {cutoff}: there is nothing to purge.";
        }

        if (facts.Unsealed > 0)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{facts.Unsealed:N0} record(s) in the months before {cutoff} are not sealed yet. Ask again in a minute.");
        }

        if (facts.Rows == 0 || facts.FirstRemoved is not { } first || facts.LastRemoved is not { } last)
        {
            return $"The months before {cutoff} hold no records: there is nothing to purge.";
        }

        if (facts.InDefaultBefore > 0)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{facts.InDefaultBefore:N0} record(s) from before {cutoff} wait in the audit trail's default partition, so their months cannot be removed whole. Partition upkeep moves them into their months within six hours; ask again then.");
        }

        if (facts.FirstKept is not { } kept)
        {
            return "Nothing sealed would be left after those months. A purge always keeps the latest records of the chain.";
        }

        if (last >= kept)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"The months before {cutoff} hold records sealed after later ones (position {last:N0} would go, position {kept:N0} is kept): only months at the start of the chain can be purged.");
        }

        if (first != facts.ExpectedFirst || facts.Rows != last - first + 1)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"The months before {cutoff} hold {facts.Rows:N0} record(s) at chain positions {first:N0} to {last:N0}, but the chain starts at position {facts.ExpectedFirst:N0}: positions are missing. Verify the audit trail's integrity first.");
        }

        return null;
    }

    /// <summary>
    /// Why the general settings API does not change <see cref="YearsSetting"/>: only the two-person change under
    /// Retention sets it.
    /// </summary>
    public static string? SettingProblem(string key) =>
        string.Equals(key, YearsSetting, StringComparison.OrdinalIgnoreCase)
            ? "Super administrators set how long the audit trail is kept, two people agreeing, under Privacy, Retention, Audit trail."
            : null;

    [GeneratedRegex(@"^audit_events_(\d{4})_(\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex PartitionName();
}

/// <summary>What <see cref="AuditRetention.PurgeProblem"/> decides on, as found in the database.</summary>
/// <param name="CutoffMonth">The first month kept.</param>
/// <param name="Months">How many monthly partitions lie entirely before it.</param>
/// <param name="Rows">How many records they hold.</param>
/// <param name="Unsealed">How many of those are not sealed yet.</param>
/// <param name="FirstRemoved">The lowest chain position among them.</param>
/// <param name="LastRemoved">The highest: the boundary of the purge.</param>
/// <param name="FirstKept">The lowest chain position among the records kept; null when none is sealed.</param>
/// <param name="ExpectedFirst">Where the chain starts: 1, or the position after the last purge's boundary.</param>
/// <param name="InDefaultBefore">Records from before the cut-off in the default partition, which is never removed.</param>
public sealed record AuditPurgeFacts(
    DateOnly CutoffMonth, int Months, long Rows, long Unsealed, long? FirstRemoved, long? LastRemoved, long? FirstKept, long ExpectedFirst,
    long InDefaultBefore);

/// <summary>A whole month of the audit trail a purge would remove: its records and their chain positions.</summary>
public sealed record AuditPurgeMonth(DateOnly Month, long Rows, long? FirstChainIndex, long? LastChainIndex);

/// <summary>
/// What a purge would remove now: the months entirely before <see cref="CutoffMonth"/>, their records and chain positions,
/// and the hash at the last of them (the boundary), where the chain will start from. <see cref="Problem"/> says why no
/// purge can be asked for now.
/// </summary>
public sealed record AuditPurgePlan(
    int? Years,
    DateOnly? CutoffMonth,
    IReadOnlyList<AuditPurgeMonth> Months,
    long Rows,
    long? FirstChainIndex,
    long? BoundaryChainIndex,
    string? BoundaryHash,
    string? Problem);

/// <summary>A purge as asked for, and approved only while the trail still holds exactly this.</summary>
public sealed record AuditPurgePayload(
    int Years, DateOnly CutoffMonth, IReadOnlyList<DateOnly> Months, long Rows, long FirstChainIndex, long BoundaryChainIndex, string BoundaryHash);

/// <summary>A change of the period as asked for: <see cref="Years"/> (null: permanently), made only while <see cref="Previous"/> is in force.</summary>
public sealed record AuditRetentionPayload(int? Years, int? Previous);

/// <summary>
/// The record of a purge of the audit trail (<c>audit.audit_purges</c>, append-only): the chain positions it removed, up to
/// <see cref="BoundaryChainIndex"/> and its hash, the months, how many records, the export they went to with its SHA-256,
/// and who asked and who approved. It is sealed with the audit seal key (<see cref="Mac"/>, an HMAC of
/// <see cref="ContentHash"/>), and an anchor of the boundary is written outside the database as it is made: verification
/// starts from the latest purge after checking both, so a purge record made up to hide rows removed does not verify.
/// </summary>
public sealed class AuditPurge
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public DateTimeOffset PurgedAt { get; set; }

    /// <summary>The period in force, in years.</summary>
    public int RetentionYears { get; set; }

    /// <summary>The first month kept.</summary>
    public DateOnly CutoffMonth { get; set; }

    /// <summary>The months removed, "2015-03,2015-04".</summary>
    public string Months { get; set; } = string.Empty;

    public long RowCount { get; set; }

    public long FirstChainIndex { get; set; }

    /// <summary>The last chain position removed: the chain now starts at the next one, linked to <see cref="BoundaryHash"/>.</summary>
    public long BoundaryChainIndex { get; set; }

    public string BoundaryHash { get; set; } = string.Empty;

    /// <summary>The export's file name in the audit archive (<see cref="Hosting.DataDirectories.AuditArchive"/>).</summary>
    public string ExportFile { get; set; } = string.Empty;

    /// <summary>Upper-case hex SHA-256 of the export file.</summary>
    public string ExportSha256 { get; set; } = string.Empty;

    public Guid ApprovalId { get; set; }

    public Guid RequestedBy { get; set; }

    public string RequestedByName { get; set; } = string.Empty;

    public Guid ApprovedBy { get; set; }

    public string ApprovedByName { get; set; } = string.Empty;

    /// <summary>HMAC-SHA256 of <see cref="ContentHash"/> under the <see cref="AuditSealKey"/>.</summary>
    public string Mac { get; set; } = string.Empty;

    /// <summary>The months removed, in order.</summary>
    public IReadOnlyList<DateOnly> MonthList() =>
        [.. Months.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => DateOnly.ParseExact(m + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture))];

    /// <summary>Every column but the seal, in a fixed order with invariant formatting.</summary>
    public string CanonicalContent() => string.Join('\u001f',
        Id.ToString("D"),
        PurgedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        RetentionYears.ToString(CultureInfo.InvariantCulture),
        CutoffMonth.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Months,
        RowCount.ToString(CultureInfo.InvariantCulture),
        FirstChainIndex.ToString(CultureInfo.InvariantCulture),
        BoundaryChainIndex.ToString(CultureInfo.InvariantCulture),
        BoundaryHash,
        ExportFile,
        ExportSha256,
        ApprovalId.ToString("D"),
        RequestedBy.ToString("D"),
        RequestedByName,
        ApprovedBy.ToString("D"),
        ApprovedByName);

    /// <summary>Upper-case hex SHA-256 of <see cref="CanonicalContent"/>, which the seal covers.</summary>
    public string ContentHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalContent())));
}

internal sealed class AuditPurgeConfiguration : IEntityTypeConfiguration<AuditPurge>
{
    public void Configure(EntityTypeBuilder<AuditPurge> b)
    {
        b.ToTable("audit_purges", "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.BoundaryHash).HasMaxLength(64);
        b.Property(x => x.ExportFile).HasMaxLength(260);
        b.Property(x => x.ExportSha256).HasMaxLength(64);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.ApprovedByName).HasMaxLength(200);
        b.Property(x => x.Mac).HasMaxLength(64);
        b.HasIndex(x => x.BoundaryChainIndex).IsUnique();
    }
}
