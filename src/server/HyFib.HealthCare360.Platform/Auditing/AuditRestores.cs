using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Platform.Auditing;

/// <summary>What <see cref="AuditRestores.InForce"/> found in the anchor file.</summary>
/// <param name="Anchors">The anchors that describe the chain as it is now, in the order written.</param>
/// <param name="Restores">The restores accepted, in the order written: each anchor's position is the point restored to.</param>
/// <param name="SetAside">How many anchors the restores set aside: they were of the chain a restore left behind.</param>
public sealed record AuditAnchorsInForce(IReadOnlyList<AuditAnchor> Anchors, IReadOnlyList<AuditAnchor> Restores, int SetAside);

/// <summary>
/// A sanctioned restore of the audit trail. A restore puts the database back to its backup, and with it the audit chain,
/// which ends where the backup's ended; the anchors written outside the database since go on naming the positions after
/// that, as the chain had them before the restore. So every check after a restore found the chain cut short, or made
/// again, and System health raised a critical alert every day for good.
/// <para>
/// As HC360 first starts after Restore-HC360.ps1 (<see cref="Numbering.NumberSeriesStartupCheck"/>), it records the restore
/// twice, in the one transaction that acts on the restore marker: in the anchor file, an anchor of the restored chain's
/// end that carries a restore mark sealed with the audit seal key (<see cref="AuditAnchor.Restore"/>), and in the audit
/// trail, a critical entry naming that point (<see cref="RestoredAction"/>). The chain check then sets aside the anchors
/// written before that anchor at positions after it (<see cref="InForce"/>), and checks every other anchor and every
/// record as before: a record changed or removed, before or after the restore, still breaks the chain. A restore mark
/// needs both: one in the anchor file without its entry, or one whose seal is not this server's, sets nothing aside, so
/// neither whoever can write the database nor whoever can write the anchor file alone can pass a chain cut short for a
/// restore.
/// </para>
/// </summary>
public sealed class AuditRestores(AuditAnchors anchors, AuditSealKey key, ILogger<AuditRestores> logger)
{
    /// <summary>The audit action of a restore, a critical entry naming the point restored to.</summary>
    public const string RestoredAction = "audit.restored";

    /// <summary>The entity of <see cref="RestoredAction"/>: the audit chain, by the position restored to.</summary>
    public const string ChainEntity = "AuditChain";

    /// <summary>The hash a restore anchor names when the restored database had no sealed record.</summary>
    private static readonly string EmptyChain = new('0', 64);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Records a restore, in the caller's transaction, before any record is sealed on the restored database: the sealer's
    /// lock is taken to the transaction's end, the restored chain's end is read, the restore is anchored outside the database
    /// with this server's seal, and the critical entry is added to the audit trail and saved. A start that anchored it but
    /// could not commit anchors the same restore once: the next start finds that anchor and adds its entry.
    /// </summary>
    /// <param name="restoredAt">When the restore ended, as the restore marker says.</param>
    /// <param name="backupFile">The backup restored, as the restore marker names it.</param>
    /// <param name="backupTakenAt">When that backup was made, when the marker says.</param>
    /// <returns>The restore anchor: its position is the point restored to.</returns>
    public async Task<AuditAnchor> RecordAsync(
        HospitalDbContext db, DateTimeOffset restoredAt, string? backupFile, DateTimeOffset? backupTakenAt, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({AuditChainService.AdvisoryLockKey})", ct);
        var (index, hash, mac) = await ChainEndAsync(db, ct);
        var written = anchors.Read().Anchors;
        var file = backupFile is { Length: > 0 } ? Path.GetFileName(backupFile.Replace('\\', '/')) : null;
        var anchor = written.LastOrDefault(a => a.Restore is { } mark && a.ChainIndex == index && a.Hash == hash && mark.RestoredAt == restoredAt && IsSealed(a, key));
        if (anchor is null)
        {
            // The anchors in force past the point restored to are those of the chain it leaves behind: named, for the auditors.
            var inForce = InForce(written, key, await RecordedAsync(db, ct)).Anchors;
            var superseded = inForce.Select(a => a.ChainIndex).Where(i => i > index).DefaultIfEmpty(index).Max();
            anchor = Seal(new AuditAnchor(index, hash, mac, now) { Restore = new AuditRestoreMark(restoredAt, file, superseded, string.Empty) }, key);
            anchors.Append(anchor);
        }

        var restore = anchor.Restore!;
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            OccurredAt = now,
            Category = AuditCategories.Security,
            Action = RestoredAction,
            Severity = AuditSeverity.Critical,
            EntityType = ChainEntity,
            EntityId = index.ToString(CultureInfo.InvariantCulture),
            Summary = Summary(anchor, backupTakenAt),
            Details = JsonSerializer.Serialize(new
            {
                chainIndex = index,
                hash,
                restoredAt,
                backupFile = file,
                backupTakenAt,
                supersededThrough = restore.Superseded,
            }, Json),
        });
        await db.SaveChangesAsync(ct);
        logger.LogWarning("The database was restored from {Backup}: the audit chain goes on from position {ChainIndex}, where the backup's ended, as the audit trail records",
            file ?? "a backup", index);
        return anchor;
    }

    /// <summary>
    /// The anchors that describe the chain as it is, in the order written. A restore anchor whose mark carries this server's
    /// seal, and which the audit trail records (<paramref name="recorded"/>: a critical <see cref="RestoredAction"/> entry
    /// naming its position and hash), sets aside every anchor written before it at a later position: those were of the
    /// chain the restore left behind. Every other anchor stays in force, a restore anchor that lacks either among them.
    /// </summary>
    public static AuditAnchorsInForce InForce(IReadOnlyList<AuditAnchor> written, AuditSealKey key, IReadOnlySet<(long ChainIndex, string Hash)> recorded)
    {
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(recorded);
        var inForce = new List<AuditAnchor>();
        var restores = new List<AuditAnchor>();
        var setAside = 0;
        foreach (var anchor in written)
        {
            if (anchor.Restore is not null && recorded.Contains((anchor.ChainIndex, anchor.Hash)) && IsSealed(anchor, key))
            {
                setAside += inForce.RemoveAll(a => a.ChainIndex > anchor.ChainIndex);
                restores.Add(anchor);
            }

            // A restore to an empty chain anchors no record.
            if (anchor.ChainIndex > 0)
            {
                inForce.Add(anchor);
            }
        }

        return new AuditAnchorsInForce(inForce, restores, setAside);
    }

    /// <summary>The points the audit trail records restores to: its critical <see cref="RestoredAction"/> entries, sealed yet or not.</summary>
    public static async Task<IReadOnlySet<(long ChainIndex, string Hash)>> RecordedAsync(HospitalDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entries = await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Action == RestoredAction && e.Category == AuditCategories.Security && e.EntityType == ChainEntity && e.Severity == AuditSeverity.Critical)
            .Select(e => new { e.EntityId, e.Details })
            .ToListAsync(ct);
        var points = new HashSet<(long, string)>();
        foreach (var entry in entries)
        {
            if (long.TryParse(entry.EntityId, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && HashIn(entry.Details) is { } hash)
            {
                points.Add((index, hash));
            }
        }

        return points;
    }

    /// <summary>What a restore anchor's seal covers: its position, hash and time, and its restore, in a fixed order.</summary>
    internal static string Digest(AuditAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var restore = anchor.Restore ?? throw new ArgumentException("The anchor carries no restore.", nameof(anchor));
        var canonical = string.Join('|', "restore-v1", anchor.ChainIndex.ToString(CultureInfo.InvariantCulture), anchor.Hash,
            anchor.AnchoredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), restore.RestoredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            restore.Backup ?? string.Empty, restore.Superseded.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>The anchor with its restore mark sealed by <paramref name="key"/>.</summary>
    internal static AuditAnchor Seal(AuditAnchor anchor, AuditSealKey key) =>
        anchor with { Restore = anchor.Restore! with { Seal = key.Seal(Digest(anchor)) } };

    private static bool IsSealed(AuditAnchor anchor, AuditSealKey key) => anchor.Restore is { Seal.Length: > 0 } mark && key.Sealed(Digest(anchor), mark.Seal);

    /// <summary>The restored chain's end: its last sealed record, or the boundary of the last purge when none is left, or an empty chain.</summary>
    private static async Task<(long Index, string Hash, string? Mac)> ChainEndAsync(HospitalDbContext db, CancellationToken ct)
    {
        var last = await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.ChainIndex != null)
            .OrderByDescending(e => e.ChainIndex)
            .Select(e => new { e.ChainIndex, e.Hash, e.Mac })
            .FirstOrDefaultAsync(ct);
        if (last is { ChainIndex: { } index, Hash: { } hash })
        {
            return (index, hash, last.Mac);
        }

        var purged = await db.Database.SqlQuery<bool>($"SELECT to_regclass('audit.audit_purges') IS NOT NULL AS \"Value\"").SingleAsync(ct)
            ? await db.Set<AuditPurge>().AsNoTracking().OrderByDescending(p => p.BoundaryChainIndex).Select(p => new { p.BoundaryChainIndex, p.BoundaryHash }).FirstOrDefaultAsync(ct)
            : null;
        return purged is null ? (0, EmptyChain, null) : (purged.BoundaryChainIndex, purged.BoundaryHash, null);
    }

    private static string Summary(AuditAnchor anchor, DateTimeOffset? backupTakenAt)
    {
        var restore = anchor.Restore!;
        var from = restore.Backup is { } file
            ? backupTakenAt is { } taken ? $"the backup of {taken.ToUniversalTime():yyyy-MM-dd HH:mm} UTC ({file})" : $"the backup {file}"
            : "a backup";
        var text = string.Create(CultureInfo.InvariantCulture,
            $"The database was restored from {from} on {restore.RestoredAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC: the audit chain goes on from position {anchor.ChainIndex:N0}, where the backup's ended");
        return restore.Superseded > anchor.ChainIndex
            ? text + string.Create(CultureInfo.InvariantCulture, $". Positions {anchor.ChainIndex + 1:N0} to {restore.Superseded:N0}, anchored before, were of the chain the restore left behind.")
            : text + ".";
    }

    private static string? HashIn(string? details)
    {
        if (details is null)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(details);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("hash", out var hash) && hash.ValueKind == JsonValueKind.String
                ? hash.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
