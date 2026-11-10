using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FluentValidation;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Platform.Auditing;

/// <summary>A change or a purge of the audit trail waiting for a second super administrator.</summary>
public sealed record AuditPendingRequest(Guid ApprovalId, string Summary, string RequestedByName, DateTimeOffset RequestedAt);

/// <summary>A purge made: what it removed, the export it went to, who asked and who approved.</summary>
public sealed record AuditPurgeSummary(
    DateTimeOffset PurgedAt, IReadOnlyList<DateOnly> Months, long Rows, long FirstChainIndex, long BoundaryChainIndex, string ExportFile, string ExportSha256,
    string RequestedByName, string ApprovedByName);

/// <summary>How long the audit trail is kept, what a purge would remove now, and what waits for approval.</summary>
/// <param name="Years">The period in force; null while the audit trail is kept permanently.</param>
public sealed record AuditRetentionStatus(
    int? Years, int MinimumYears, int MaximumYears, string Basis, AuditPurgePlan Purge, AuditPendingRequest? PendingChange, AuditPendingRequest? PendingPurge,
    AuditPurgeSummary? LastPurge);

/// <param name="Years">The period asked for, 8 years at the least; null for permanently.</param>
public sealed record AskAuditRetentionRequest(int? Years, string Reason);

public sealed record AskAuditPurgeRequest(string Reason);

public sealed class AskAuditRetentionRequestValidator : AbstractValidator<AskAuditRetentionRequest>
{
    public AskAuditRetentionRequestValidator()
    {
        RuleFor(x => x.Years).Must(years => AuditRetention.Problem(years) is null).WithMessage(x => AuditRetention.Problem(x.Years) ?? string.Empty);
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Say why: the super administrator who decides it reads it.").SafeText(500);
    }
}

public sealed class AskAuditPurgeRequestValidator : AbstractValidator<AskAuditPurgeRequest>
{
    public AskAuditPurgeRequestValidator() =>
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Say why: the super administrator who decides it reads it.").SafeText(500);
}

/// <summary>
/// How long the audit trail is kept, and the purge of the months that period no longer keeps (ADR-015, SEC-04). Both are
/// asked for by one holder of <see cref="PlatformPermissions.SystemManage"/> and made only when another approves them
/// (<see cref="AuditRetention.ChangeKind"/>, <see cref="AuditRetention.PurgeKind"/>).
/// <para>
/// A purge removes whole monthly partitions entirely before the cut-off month, and only those at the start of the chain,
/// every record in them sealed. Approved, in the approval's transaction: the records are exported, in the auditor's format,
/// to the audit archive outside the database and the file is checked; a purge record is made, sealed with the audit seal
/// key, and an anchor of its boundary is written to the anchor file; the partitions are detached and dropped (the only
/// partition ever dropped); and <see cref="AuditRetention.PurgedAction"/> is audited. Verification then starts from the
/// purge's boundary (<see cref="AuditChainService.VerifyAsync"/>).
/// </para>
/// </summary>
public sealed class AuditRetentionService(
    HospitalDbContext db,
    ISettingsService settings,
    IApprovals approvals,
    IAuditLogger audit,
    ICurrentUser current,
    AuditSealKey key,
    AuditAnchors anchors,
    DataDirectories directories,
    TimeProvider time,
    ILogger<AuditRetentionService> logger)
{
    private const int ExportBatch = 5000;

    /// <summary>How long the purge waits for the old months' locks, and the audit trail's, before it is tried again later.</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public async Task<AuditRetentionStatus> StatusAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var pending = await db.Set<ApprovalRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => r.SubjectType == AuditRetention.SubjectType && r.SubjectId == AuditRetention.SubjectId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderBy(r => r.RequestedAt)
            .Select(r => new { r.Kind, r.Id, r.Summary, r.RequestedByName, r.RequestedAt })
            .ToListAsync(ct);
        AuditPendingRequest? Pending(string kind) => pending.Where(r => r.Kind == kind)
            .Select(r => new AuditPendingRequest(r.Id, r.Summary, r.RequestedByName, r.RequestedAt)).FirstOrDefault();
        var last = await LastPurgeAsync(ct);
        return new AuditRetentionStatus(await AuditRetention.YearsAsync(settings, ct), AuditRetention.MinimumYears, AuditRetention.MaximumYears, AuditRetention.Basis,
            await PlanAsync(ct), Pending(AuditRetention.ChangeKind), Pending(AuditRetention.PurgeKind),
            last is null
                ? null
                : new AuditPurgeSummary(last.PurgedAt, last.MonthList(), last.RowCount, last.FirstChainIndex, last.BoundaryChainIndex, last.ExportFile, last.ExportSha256,
                    last.RequestedByName, last.ApprovedByName));
    }

    // ---------------------------------------------------------------- the period

    /// <summary>
    /// Asks another super administrator to keep the audit trail for <paramref name="years"/> (8 at the least), or
    /// permanently (null). The request says the period in force, the one asked for, and what it means: the months a purge
    /// could then remove, and how many records they hold today.
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> AskChangeAsync(int? years, string reason, CancellationToken ct)
    {
        if (AuditRetention.Problem(years) is { } problem)
        {
            return Error.Field("years", problem);
        }

        var now = await AuditRetention.YearsAsync(settings, ct);
        if (now == years)
        {
            return Error.Rule("audit.retention_unchanged", $"The audit trail is kept {AuditRetention.Words(years)} already.");
        }

        string meaning;
        if (years is { } asked)
        {
            var cutoff = AuditRetention.CutoffMonth(time.GetUtcNow(), asked);
            var cutoffAt = AuditRetention.Start(cutoff);
            var older = await db.Set<AuditEvent>().LongCountAsync(e => e.OccurredAt < cutoffAt, ct);
            meaning = string.Create(CultureInfo.InvariantCulture,
                $"A purge could then remove the months before {AuditRetention.MonthWords(cutoff)}, exported first, when two super administrators agree; today {older:N0} record(s) are that old. Nothing is removed by this change itself.");
        }
        else
        {
            meaning = "Nothing is ever purged: the audit trail is kept permanently.";
        }

        return await approvals.RequestAsync(
            AuditRetention.ChangeKind,
            await SubjectAsync(ct),
            new AuditRetentionPayload(years, now),
            $"Audit trail kept {AuditRetention.Words(years)} (now {AuditRetention.Words(now)})",
            [
                new ApprovalDetail("Kept now", Capital(AuditRetention.Words(now))),
                new ApprovalDetail("Asked for", Capital(AuditRetention.Words(years))),
                new ApprovalDetail("What it means", meaning),
                new ApprovalDetail("Why at least 8 years", AuditRetention.Basis),
            ],
            reason,
            ct);
    }

    /// <summary>
    /// Sets the period as approved, in the approval's transaction, if it is still what it was when it was asked for and
    /// still allowed.
    /// </summary>
    internal async Task<Result<string>> ApplyChangeAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<AuditRetentionPayload>(request.Payload);
        var now = await AuditRetention.YearsAsync(settings, ct);
        if (now != asked.Previous)
        {
            return Error.Rule("audit.retention_changed",
                $"The audit trail's period changed after this was asked for (it is kept {AuditRetention.Words(now)} now), so nothing was changed. Ask again.");
        }

        if (AuditRetention.Problem(asked.Years) is { } problem)
        {
            return Error.Rule("audit.retention_minimum", problem);
        }

        audit.Add(new AuditEntry(AuditCategories.Security, AuditRetention.ChangedAction,
            $"The audit trail is kept {AuditRetention.Words(asked.Years)} (was {AuditRetention.Words(asked.Previous)}): asked for by {request.RequestedByName}, approved by {Name()}")
        {
            Severity = AuditSeverity.Warning,
            EntityType = AuditRetention.SubjectType,
            EntityId = AuditRetention.SubjectId.ToString(),
            Details = new { years = asked.Years, previous = asked.Previous, approval = request.Id, requestedBy = request.RequestedBy },
        });
        await settings.SetAsync(AuditRetention.YearsSetting, asked.Years, null, false, ct);
        return $"Kept {AuditRetention.Words(asked.Years)} from now";
    }

    // ---------------------------------------------------------------- the purge

    /// <summary>
    /// What a purge would remove now: the monthly partitions entirely before the cut-off month (none while the trail is
    /// kept permanently), with their records and chain positions, and why none can be asked for, if so.
    /// </summary>
    public async Task<AuditPurgePlan> PlanAsync(CancellationToken ct)
    {
        var years = await AuditRetention.YearsAsync(settings, ct);
        if (years is null)
        {
            return new AuditPurgePlan(null, null, [], 0, null, null, null,
                "The audit trail is kept permanently, so nothing is purged. Two super administrators set a period first.");
        }

        var cutoff = AuditRetention.CutoffMonth(time.GetUtcNow(), years.Value);
        var cutoffAt = AuditRetention.Start(cutoff);
        var partitions = await db.Database.SqlQuery<string>($"""
            SELECT c.relname AS "Value"
            FROM pg_inherits i
            JOIN pg_class c ON c.oid = i.inhrelid
            WHERE i.inhparent = 'audit.audit_events'::regclass AND c.relkind = 'r'
            """).ToListAsync(ct);

        var months = new List<AuditPurgeMonth>();
        long unsealed = 0;
        foreach (var month in partitions.Select(AuditRetention.MonthOfPartition).OfType<DateOnly>().Where(m => m < cutoff).Order())
        {
            // A partition's name comes from a month, never from a request.
            var sql = $"""
                SELECT count(*) AS "Rows", count(*) FILTER (WHERE hash IS NULL) AS "Unsealed", min(chain_index) AS "First", max(chain_index) AS "Last"
                FROM ONLY audit."{PartitionedTables.Audit.PartitionFor(month)}"
                """;
            var stats = (await db.Database.SqlQueryRaw<PartitionStats>(sql).ToListAsync(ct))[0];
            months.Add(new AuditPurgeMonth(month, stats.Rows, stats.First, stats.Last));
            unsealed += stats.Unsealed;
        }

        var rows = months.Sum(m => m.Rows);
        var first = months.Min(m => m.FirstChainIndex);
        var last = months.Max(m => m.LastChainIndex);
        var inDefault = 0L;
        if (await db.Database.SqlQuery<bool>($"SELECT to_regclass('audit.audit_events_default') IS NOT NULL AS \"Value\"").SingleAsync(ct))
        {
            inDefault = await db.Database.SqlQuery<long>(
                $"SELECT count(*) AS \"Value\" FROM ONLY audit.audit_events_default WHERE occurred_at < {cutoffAt}").SingleAsync(ct);
        }

        var kept = await db.Set<AuditEvent>().Where(e => e.ChainIndex != null && e.OccurredAt >= cutoffAt).MinAsync(e => e.ChainIndex, ct);
        var lastPurge = await LastPurgeAsync(ct);
        var problem = AuditRetention.PurgeProblem(new AuditPurgeFacts(cutoff, months.Count, rows, unsealed, first, last, kept,
            (lastPurge?.BoundaryChainIndex ?? 0) + 1, inDefault));

        string? boundaryHash = null;
        if (problem is null && last is { } boundary)
        {
            boundaryHash = await db.Set<AuditEvent>().Where(e => e.ChainIndex == boundary && e.OccurredAt < cutoffAt).Select(e => e.Hash).FirstOrDefaultAsync(ct);
            var next = await db.Set<AuditEvent>().Where(e => e.ChainIndex == boundary + 1).Select(e => e.PreviousHash).FirstOrDefaultAsync(ct);
            if (boundaryHash is null || !string.Equals(next, boundaryHash, StringComparison.Ordinal))
            {
                problem = string.Create(CultureInfo.InvariantCulture,
                    $"The audit chain is broken at position {boundary + 1:N0}, the first that would be kept. Verify the audit trail's integrity first.");
            }
        }

        if (problem is null && await AuditChainChecks.WarningAsync(db, ct) is { } broken)
        {
            problem = $"The audit trail failed its latest check, so none of it is purged until a check passes. {broken.Message}";
        }

        return new AuditPurgePlan(years, cutoff, months, rows, first, last, boundaryHash, problem);
    }

    /// <summary>Asks another super administrator to purge what <see cref="PlanAsync"/> finds now, exactly that.</summary>
    public async Task<Result<ApprovalRequestDto>> AskPurgeAsync(string reason, CancellationToken ct)
    {
        var plan = await PlanAsync(ct);
        if (plan.Problem is { } problem)
        {
            return Error.Rule("audit.purge_refused", problem);
        }

        var payload = Payload(plan);
        return await approvals.RequestAsync(
            AuditRetention.PurgeKind,
            await SubjectAsync(ct),
            payload,
            string.Create(CultureInfo.InvariantCulture,
                $"Purge of the audit trail before {AuditRetention.MonthWords(payload.CutoffMonth)}: {payload.Rows:N0} records of {MonthsWords(payload.Months)}"),
            [
                new ApprovalDetail("Kept now", Capital(AuditRetention.Words(payload.Years))),
                new ApprovalDetail("Months to remove", string.Join(", ", payload.Months.Select(AuditRetention.MonthWords))),
                new ApprovalDetail("Records", payload.Rows.ToString("N0", CultureInfo.InvariantCulture)),
                new ApprovalDetail("Chain positions", string.Create(CultureInfo.InvariantCulture, $"{payload.FirstChainIndex:N0} to {payload.BoundaryChainIndex:N0}")),
                new ApprovalDetail("The chain then starts after", string.Create(CultureInfo.InvariantCulture, $"Position {payload.BoundaryChainIndex:N0}, hash {payload.BoundaryHash}")),
                new ApprovalDetail("Before anything is removed",
                    "The records are exported in the auditor's format to the audit archive on the server and checked, a sealed purge record is made, and the chain is anchored outside the database where it then starts."),
            ],
            reason,
            ct);
    }

    /// <summary>
    /// Makes the purge as approved, in the approval's transaction, if the trail still holds exactly what was asked for:
    /// exports and checks the records, records and seals the purge with an anchor outside the database, drops the months'
    /// partitions and audits it.
    /// </summary>
    internal async Task<Result<string>> ApplyPurgeAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<AuditPurgePayload>(request.Payload);
        if (Changed(await PlanAsync(ct), asked) is { } changed)
        {
            return Error.Rule("audit.purge_changed", changed);
        }

        var timeout = await db.Database.SqlQuery<string>($"SELECT current_setting('lock_timeout') AS \"Value\"").SingleAsync(ct);
        string? export = null;
        try
        {
            await ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $"SET LOCAL lock_timeout = '{LockTimeout.TotalMilliseconds:0}ms'"), ct);

            // The months held still while they are exported and removed: nothing comes into them, nor is sealed there.
            foreach (var month in asked.Months)
            {
                await ExecuteAsync($"LOCK TABLE ONLY audit.\"{PartitionedTables.Audit.PartitionFor(month)}\" IN SHARE MODE", ct);
            }

            if (Changed(await PlanAsync(ct), asked) is { } since)
            {
                return Error.Rule("audit.purge_changed", since);
            }

            var lastPurge = await LastPurgeAsync(ct);
            var exported = await ExportAsync(asked, asked.FirstChainIndex == 1 ? null : lastPurge?.BoundaryHash, ct);
            if (exported.IsFailure)
            {
                return exported.Error!;
            }

            var (file, sha256) = exported.Value;
            export = Path.Combine(directories.AuditArchive, file);
            var cutoffAt = AuditRetention.Start(asked.CutoffMonth);
            var boundaryMac = await db.Set<AuditEvent>().AsNoTracking()
                .Where(e => e.ChainIndex == asked.BoundaryChainIndex && e.OccurredAt < cutoffAt).Select(e => e.Mac).FirstOrDefaultAsync(ct);
            var now = SqlTime.ToMilliseconds(time.GetUtcNow());
            var purge = new AuditPurge
            {
                PurgedAt = now,
                RetentionYears = asked.Years,
                CutoffMonth = asked.CutoffMonth,
                Months = string.Join(',', asked.Months.Select(m => m.ToString("yyyy-MM", CultureInfo.InvariantCulture))),
                RowCount = asked.Rows,
                FirstChainIndex = asked.FirstChainIndex,
                BoundaryChainIndex = asked.BoundaryChainIndex,
                BoundaryHash = asked.BoundaryHash,
                ExportFile = file,
                ExportSha256 = sha256,
                ApprovalId = request.Id,
                RequestedBy = request.RequestedBy,
                RequestedByName = request.RequestedByName,
                ApprovedBy = current.UserId ?? Guid.Empty,
                ApprovedByName = Name(),
            };
            purge.Mac = key.Seal(purge.ContentHash());
            db.Add(purge);
            anchors.Append(new AuditAnchor(asked.BoundaryChainIndex, asked.BoundaryHash, boundaryMac, now));

            // The only partitions ever dropped. Upkeep makes a month gone by again only for rows waiting in the default
            // partition, which a purge never leaves behind.
            foreach (var month in asked.Months)
            {
                var partition = $"audit.\"{PartitionedTables.Audit.PartitionFor(month)}\"";
                // Through the function that drops nothing else: the account the server works as may not drop a table.
                await ExecuteAsync($"SELECT audit.drop_purged_month('{partition}'::regclass)", ct);
            }

            await db.Database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', {timeout}, true)", ct);
            var what = string.Create(CultureInfo.InvariantCulture,
                $"{asked.Rows:N0} records of {MonthsWords(asked.Months)} (positions {asked.FirstChainIndex:N0} to {asked.BoundaryChainIndex:N0})");
            audit.Add(new AuditEntry(AuditCategories.Security, AuditRetention.PurgedAction,
                $"Purged the audit trail before {AuditRetention.MonthWords(asked.CutoffMonth)}: {what}, exported to {file} (SHA-256 {sha256}); asked for by {request.RequestedByName}, approved by {Name()}")
            {
                Severity = AuditSeverity.Critical,
                EntityType = nameof(AuditPurge),
                EntityId = purge.Id.ToString(),
                Details = new
                {
                    purge = purge.Id,
                    years = asked.Years,
                    cutoffMonth = asked.CutoffMonth,
                    months = asked.Months,
                    rows = asked.Rows,
                    firstChainIndex = asked.FirstChainIndex,
                    boundaryChainIndex = asked.BoundaryChainIndex,
                    boundaryHash = asked.BoundaryHash,
                    exportFile = file,
                    exportSha256 = sha256,
                    approval = request.Id,
                    requestedBy = request.RequestedBy,
                },
            });
            logger.LogWarning("Audit trail purged before {Cutoff}: {What}, exported to {File} (SHA-256 {Sha256})", asked.CutoffMonth, what, file, sha256);
            export = null;
            return $"Removed {what}, exported to {file}";
        }
        catch (Exception ex) when (DatabaseInitializer.IsLockTimeout(ex))
        {
            return Error.Conflict("approval.conflict",
                "The audit trail is in use (a report or a backup reads it), so nothing was removed. Approve it again in a few minutes.");
        }
        finally
        {
            // Nothing removed: no export left without its purge record.
            if (export is not null)
            {
                TryDelete(export);
            }
        }
    }

    /// <summary>
    /// Exports the records to purge, in the auditor's format, to a new file in the audit archive, checking as it goes that
    /// each is the next of the chain and carries this server's seal, then checks the file as an auditor would. Returns the
    /// file's name and its SHA-256.
    /// </summary>
    private async Task<Result<(string File, string Sha256)>> ExportAsync(AuditPurgePayload asked, string? previousHash, CancellationToken ct)
    {
        Directory.CreateDirectory(directories.AuditArchive);
        var now = time.GetUtcNow();
        var file = string.Create(CultureInfo.InvariantCulture, $"hc360-audit-purge-{asked.FirstChainIndex}-{asked.BoundaryChainIndex}-{now:yyyyMMddHHmmss}.json");
        var path = Path.Combine(directories.AuditArchive, file);
        var partial = path + ".partial";
        var inRange = anchors.Read().Anchors.Where(a => a.ChainIndex >= asked.FirstChainIndex && a.ChainIndex <= asked.BoundaryChainIndex).ToList();
        var walk = new PurgeWalk(asked, previousHash, key);
        try
        {
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await AuditExportWriter.WriteAsync(stream, now, Name(), asked.FirstChainIndex, asked.BoundaryChainIndex, inRange, RecordsAsync(asked, walk, ct), ct);
                await stream.FlushAsync(ct);
            }

            if ((walk.Problem ?? walk.Finish()) is { } broken)
            {
                TryDelete(partial);
                return Error.Rule("audit.purge_broken", broken);
            }

            AuditExportCheck check;
            await using (var read = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            {
                check = await AuditExportVerifier.VerifyAsync(read, inRange, ct);
            }

            if (!check.Valid || check.EventsChecked != asked.Rows)
            {
                TryDelete(partial);
                return Error.Rule("audit.purge_export", $"The export of the records to purge did not check ({check.Message}), so nothing was removed.");
            }

            string sha256;
            await using (var read = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            {
                sha256 = Convert.ToHexString(await SHA256.HashDataAsync(read, ct));
            }

            File.Move(partial, path);
            return (file, sha256);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>The records to purge in chain order, a batch at a time, each checked by <paramref name="walk"/> as it is read.</summary>
    private async IAsyncEnumerable<AuditExportEvent> RecordsAsync(AuditPurgePayload asked, PurgeWalk walk, [EnumeratorCancellation] CancellationToken ct)
    {
        var cutoffAt = AuditRetention.Start(asked.CutoffMonth);
        var next = asked.FirstChainIndex;
        while (next <= asked.BoundaryChainIndex)
        {
            var from = next;
            var batch = await db.Set<AuditEvent>().AsNoTracking()
                .Where(e => e.ChainIndex >= from && e.ChainIndex <= asked.BoundaryChainIndex && e.OccurredAt < cutoffAt)
                .OrderBy(e => e.ChainIndex)
                .Take(ExportBatch)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                yield break;
            }

            foreach (var record in batch)
            {
                if (!walk.Check(record))
                {
                    yield break;
                }

                yield return AuditExportEvent.From(record);
            }

            next = batch[^1].ChainIndex!.Value + 1;
        }
    }

    private static string? Changed(AuditPurgePlan plan, AuditPurgePayload asked)
    {
        if (plan.Problem is { } problem)
        {
            return $"The audit trail can no longer be purged as asked, so nothing was removed: {problem}";
        }

        var now = Payload(plan);
        return now.Years == asked.Years && now.CutoffMonth == asked.CutoffMonth && now.Months.SequenceEqual(asked.Months) && now.Rows == asked.Rows
               && now.FirstChainIndex == asked.FirstChainIndex && now.BoundaryChainIndex == asked.BoundaryChainIndex
               && string.Equals(now.BoundaryHash, asked.BoundaryHash, StringComparison.Ordinal)
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"What the purge would remove changed after it was asked for (now {now.Rows:N0} records of {MonthsWords(now.Months)}, positions {now.FirstChainIndex:N0} to {now.BoundaryChainIndex:N0}), so nothing was removed. Ask again.");
    }

    private static AuditPurgePayload Payload(AuditPurgePlan plan) => new(
        plan.Years ?? 0, plan.CutoffMonth ?? default, [.. plan.Months.Select(m => m.Month)], plan.Rows, plan.FirstChainIndex ?? 0, plan.BoundaryChainIndex ?? 0,
        plan.BoundaryHash ?? string.Empty);

    private Task<AuditPurge?> LastPurgeAsync(CancellationToken ct) =>
        db.Set<AuditPurge>().AsNoTracking().OrderByDescending(p => p.BoundaryChainIndex).FirstOrDefaultAsync(ct);

    private async Task<ApprovalSubject> SubjectAsync(CancellationToken ct)
    {
        var facility = current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);
        return new ApprovalSubject(AuditRetention.SubjectType, AuditRetention.SubjectId, null, facility, null);
    }

    private string Name() => current.DisplayName ?? current.UserName ?? "Unknown user";

    /// <summary>"March 2015, April 2015", or "March 2015 to August 2015 (6 months)" for more than three.</summary>
    internal static string MonthsWords(IReadOnlyList<DateOnly> months) => months.Count switch
    {
        0 => "no month",
        <= 3 => string.Join(", ", months.Select(AuditRetention.MonthWords)),
        _ => string.Create(CultureInfo.InvariantCulture,
            $"{AuditRetention.MonthWords(months[0])} to {AuditRetention.MonthWords(months[^1])} ({months.Count} months)"),
    };

    private static string Capital(string words) => string.Concat(char.ToUpperInvariant(words[0]).ToString(), words[1..]);

    // Partition DDL names tables, which cannot be parameters; every name comes from a month, never from a request.
    private Task<int> ExecuteAsync(string sql, CancellationToken ct) => db.Database.ExecuteSqlRawAsync(sql, ct);

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "The audit purge's unused export {File} was not deleted", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "The audit purge's unused export {File} was not deleted", path);
        }
    }

    private sealed record PartitionStats(long Rows, long Unsealed, long? First, long? Last);

    /// <summary>
    /// Checks the records to purge as they are exported: the chain's next position each time, from the asked first one,
    /// linked to the one before (the last purge's boundary, for the first) and sealed after it, with this server's seal.
    /// The first that is not stops the export (<see cref="Problem"/>).
    /// </summary>
    private sealed class PurgeWalk(AuditPurgePayload asked, string? previousHash, AuditSealKey key)
    {
        private long _expected = asked.FirstChainIndex;
        private string? _previous = previousHash;
        private bool _sealedSeen;

        /// <summary>Why the export stopped, or null.</summary>
        public string? Problem { get; private set; }

        /// <summary>Whether <paramref name="record"/> is the next of the chain, sealed; else <see cref="Problem"/> says why not.</summary>
        public bool Check(AuditEvent record)
        {
            var at = _expected.ToString("N0", CultureInfo.InvariantCulture);
            Problem = record.ChainIndex != _expected
                ? $"Audit position {at} is missing from the months to purge, so nothing was removed. Verify the audit trail's integrity."
                : !string.Equals(record.PreviousHash, _previous, StringComparison.Ordinal) || !record.IsSealedAfter(_previous)
                    ? $"The audit chain is broken at position {at}, so nothing was removed. Verify the audit trail's integrity."
                    : record.Mac is null ? _sealedSeen ? $"The audit record at position {at} has lost its seal, so nothing was removed." : null
                    : !key.Sealed(record.Hash!, record.Mac)
                        ? $"The seal of the audit record at position {at} does not match, so nothing was removed. Verify the audit trail's integrity."
                        : null;
            if (Problem is not null)
            {
                return false;
            }

            _sealedSeen |= record.Mac is not null;
            _previous = record.Hash;
            _expected++;
            return true;
        }

        /// <summary>Why the records read do not make the whole purge, or null.</summary>
        public string? Finish() =>
            _expected - 1 == asked.BoundaryChainIndex && string.Equals(_previous, asked.BoundaryHash, StringComparison.Ordinal)
                ? null
                : string.Create(CultureInfo.InvariantCulture,
                    $"The months to purge end at position {_expected - 1:N0}, not {asked.BoundaryChainIndex:N0} as asked, so nothing was removed.");
    }
}

/// <summary>A change of the audit trail's period, approved by a second super administrator.</summary>
internal sealed class AuditRetentionApproval(AuditRetentionService service) : IApprovalAction
{
    public string Kind => AuditRetention.ChangeKind;

    public string Title => "Audit trail retention";

    public string ApprovePermission => PlatformPermissions.SystemManage;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => service.ApplyChangeAsync(request, ct);
}

/// <summary>A purge of the months of the audit trail its period no longer keeps, approved by a second super administrator.</summary>
internal sealed class AuditPurgeApproval(AuditRetentionService service) : IApprovalAction
{
    public string Kind => AuditRetention.PurgeKind;

    public string Title => "Audit trail purge";

    public string ApprovePermission => PlatformPermissions.SystemManage;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => service.ApplyPurgeAsync(request, ct);
}

/// <summary>/api/v1/admin/audit/retention: how long the audit trail is kept, and its purge (super administrators).</summary>
internal static class AuditRetentionEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/audit/retention").WithTags("Administration").RequirePermission(PlatformPermissions.SystemManage);

        group.MapGet("/", async (AuditRetentionService service, CancellationToken ct) => TypedResults.Ok(await service.StatusAsync(ct)));

        group.MapPost("/change", async (AskAuditRetentionRequest request, AuditRetentionService service, CancellationToken ct) =>
                Accepted(await service.AskChangeAsync(request.Years, request.Reason, ct)))
            .WithValidation<AskAuditRetentionRequest>();

        group.MapPost("/purge", async (AskAuditPurgeRequest request, AuditRetentionService service, CancellationToken ct) =>
                Accepted(await service.AskPurgeAsync(request.Reason, ct)))
            .WithValidation<AskAuditPurgeRequest>();
    }

    private static IResult Accepted(Result<ApprovalRequestDto> asked) =>
        asked.ToHttpResult(request => TypedResults.Accepted($"/api/v1/approvals/{request.Id}", request));
}
