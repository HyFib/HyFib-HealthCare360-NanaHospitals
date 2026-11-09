using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Privacy.Services;

/// <summary>What a replay of the erasure ledger found and did.</summary>
/// <param name="Entries">Sealed entries in the ledger.</param>
/// <param name="Rejected">Lines that did not read, or whose seal did not verify: never applied.</param>
/// <param name="Reapplied">Entries the database did not have, applied again now.</param>
/// <param name="Backfilled">Erasures the database had and the ledger did not (made before the ledger, or whose write failed), now written to it.</param>
public sealed record ErasureReplayOutcome(int Entries, int Rejected, IReadOnlyList<ErasureReplayItem> Reapplied, int Backfilled, int MirrorCompleted);

public sealed record ErasureReplayItem(Guid LedgerEntryId, Guid RequestId, string RequestNumber, Guid PatientId, IReadOnlyList<string> Applied, IReadOnlyList<string> HeldBack, bool PatientMissing, DateTimeOffset ErasedAt);

/// <summary>The ledger as the privacy screens show it: where it is, how many erasures it holds, and the erasures applied again.</summary>
public sealed record ErasureLedgerStatus(string Path, string? MirrorPath, int Entries, int Rejected, int BackupRetentionDays, IReadOnlyList<ErasureReplayItem> Replays);

/// <summary>
/// Applies the erasure ledger to the database (DPDP Act 2023 s8(7)): after a restore from a backup made before an
/// erasure, the erasure is carried out again before the data is used. Runs when the database is brought up to date at
/// start-up (a restore always ends with a start), every hour on the nodes that run jobs, and when the grievance officer asks.
/// <list type="bullet">
/// <item>An entry is in the database when its request records every item it erased, or a replay of it is recorded.</item>
/// <item>Otherwise its items are applied again for the patient, in one transaction with an <see cref="ErasureReplay"/> row
/// and a critical audit entry (<c>privacy.erasure_reapplied</c>), and the grievance officers are told.</item>
/// <item>The other way round, an erasure the database has and the ledger does not is written to the ledger, so the
/// ledger also covers erasures made before it existed.</item>
/// <item>A PostgreSQL lock keeps two nodes from replaying at once.</item>
/// </list>
/// </summary>
public sealed class ErasureLedgerReplay(
    HospitalDbContext db,
    ErasureLedger ledger,
    ErasureService erasure,
    ISessionValidator sessions,
    INotificationService notifications,
    IAuditLogger audit,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<ErasureLedgerReplay> logger)
{
    internal const long LockKey = 4_247_602;
    public const string ReappliedAction = "privacy.erasure_reapplied";

    public async Task<ErasureLedgerStatus> StatusAsync(CancellationToken ct)
    {
        var (entries, rejected) = ledger.Read();
        var replays = await db.Set<ErasureReplay>().AsNoTracking().OrderByDescending(r => r.ReplayedAt).Take(100).ToListAsync(ct);
        return new ErasureLedgerStatus(ledger.Path, ledger.MirrorPath, entries.Count, rejected, options.Value.Backup.RetentionDays, [.. replays.Select(Item)]);
    }

    /// <summary>Replays the ledger, unless another node is doing it (then nothing is done and null is returned).</summary>
    public async Task<ErasureReplayOutcome?> RunAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").SingleAsync(ct))
            {
                return null;
            }

            try
            {
                return await ReplayAsync(ct);
            }
            finally
            {
                await db.Database.SqlQuery<bool>($"SELECT pg_advisory_unlock({LockKey}) AS \"Value\"").SingleAsync(CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<ErasureReplayOutcome> ReplayAsync(CancellationToken ct)
    {
        var (entries, rejected) = ledger.Read();
        if (rejected > 0)
        {
            logger.LogWarning("{Count} line(s) of the erasure ledger did not read or were not sealed with this server's audit seal key; they were not applied", rejected);
        }

        var requestIds = entries.Select(e => e.RequestId).Distinct().ToList();
        var recorded = await db.Set<DataRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => requestIds.Contains(r.Id))
            .Select(r => new { r.Id, r.ErasedItems })
            .ToDictionaryAsync(r => r.Id, r => r.ErasedItems, ct);
        var entryIds = entries.Select(e => e.Id).ToList();
        var replayed = (await db.Set<ErasureReplay>().AsNoTracking().Where(r => entryIds.Contains(r.LedgerEntryId)).Select(r => r.LedgerEntryId).ToListAsync(ct)).ToHashSet();

        var reapplied = new List<ErasureReplayItem>();
        foreach (var entry in entries)
        {
            var inDatabase = replayed.Contains(entry.Id)
                || (recorded.TryGetValue(entry.RequestId, out var erased) && entry.Items.All(i => erased.Contains(i, StringComparer.Ordinal)));
            if (!inDatabase)
            {
                reapplied.Add(await ReapplyAsync(entry, ct));
            }
        }

        var backfilled = await BackfillAsync(entries, ct);
        var mirrored = 0;
        try
        {
            mirrored = ledger.CompleteMirror(ledger.Read().Entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The erasure ledger's copy could not be completed");
        }

        if (reapplied.Count > 0)
        {
            await notifications.NotifyPermissionHoldersAsync(PrivacyPermissions.RequestsManage, null,
                $"{reapplied.Count} erasure(s) applied again after a restore",
                "The database was restored from a backup made before these erasures. They were carried out again; check the requests named in Privacy › Retention.",
                "/privacy/retention", "privacy", "warning", ct);
        }

        return new ErasureReplayOutcome(entries.Count, rejected, reapplied, backfilled, mirrored);
    }

    private async Task<ErasureReplayItem> ReapplyAsync(ErasureLedgerEntry entry, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var patientExists = await db.Set<Patient>().IgnoreQueryFilters().AnyAsync(p => p.Id == entry.PatientId, ct);
        List<string> applied = [];
        List<string> heldBack = [];
        List<Guid> closedSessions = [];
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (patientExists)
        {
            (applied, heldBack) = await erasure.SplitAsync(entry.PatientId, entry.Items, ct);
            var result = await erasure.ApplyAsync(entry.PatientId, applied, $"privacy request {entry.RequestNumber} (applied again after a restore)", now, ct);
            if (result.IsFailure)
            {
                throw new InvalidOperationException($"The erasure for {entry.RequestNumber} could not be applied again: {result.Error!.Message}");
            }

            closedSessions = result.Value!;
        }

        var replay = new ErasureReplay
        {
            LedgerEntryId = entry.Id,
            RequestId = entry.RequestId,
            RequestNumber = entry.RequestNumber,
            PatientId = entry.PatientId,
            ErasedAt = entry.ErasedAt,
            ReplayedAt = now,
            Applied = applied,
            HeldBack = heldBack,
            PatientMissing = !patientExists,
        };
        db.Add(replay);
        audit.Add(new AuditEntry(AuditCategories.Data, ReappliedAction,
            $"Erasure for privacy request {entry.RequestNumber}, made {entry.ErasedAt:yyyy-MM-dd HH:mm} UTC, applied again after a restore: "
            + (patientExists ? string.Join(", ", applied) + (heldBack.Count > 0 ? $" (held back: {string.Join(", ", heldBack)})" : string.Empty) : "the patient is not in this database"))
        {
            PatientId = patientExists ? entry.PatientId : null,
            EntityType = nameof(DataRequest),
            EntityId = entry.RequestId.ToString(),
            Severity = AuditSeverity.Critical,
            Details = new { ledgerEntry = entry.Id, items = entry.Items, applied, heldBack, erasedAt = entry.ErasedAt },
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        foreach (var session in closedSessions)
        {
            sessions.Invalidate(session);
        }

        logger.LogWarning("Erasure {Entry} for privacy request {Request} applied again after a restore", entry.Id, entry.RequestNumber);
        return Item(replay);
    }

    /// <summary>Writes to the ledger the erasures the database records and the ledger does not.</summary>
    private async Task<int> BackfillAsync(IReadOnlyList<ErasureLedgerEntry> entries, CancellationToken ct)
    {
        var inLedger = entries.GroupBy(e => e.RequestId).ToDictionary(g => g.Key, g => g.SelectMany(e => e.Items).ToHashSet(StringComparer.Ordinal));
        var erasedRequests = await db.Set<DataRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => r.Type == DataRequestType.Erasure && r.PatientId != null && r.ErasedItems.Count > 0)
            .Select(r => new
            {
                r.Id,
                r.RequestNumber,
                PatientId = r.PatientId!.Value,
                r.ErasedItems,
                ErasedAt = r.Events.Where(e => e.Kind == DataRequestEventKind.Erased).Max(e => (DateTimeOffset?)e.At),
            })
            .ToListAsync(ct);
        var written = 0;
        foreach (var request in erasedRequests)
        {
            var missing = request.ErasedItems.Where(i => !inLedger.TryGetValue(request.Id, out var items) || !items.Contains(i)).ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            ledger.Append(new ErasureLedgerEntry(Guid.NewGuid(), request.Id, request.RequestNumber, request.PatientId, missing, request.ErasedAt ?? time.GetUtcNow()));
            written++;
        }

        return written;
    }

    private static ErasureReplayItem Item(ErasureReplay r) =>
        new(r.LedgerEntryId, r.RequestId, r.RequestNumber, r.PatientId, r.Applied, r.HeldBack, r.PatientMissing, r.ErasedAt);
}

/// <summary>Replays the erasure ledger as the database is brought up to date, before the web server takes requests.</summary>
internal sealed class ErasureLedgerSeeder(ErasureLedgerReplay replay, ILogger<ErasureLedgerSeeder> logger) : ISeeder
{
    /// <summary>After every other seeder: the data is all there.</summary>
    public int Order => 10_000;

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            if (await replay.RunAsync(cancellationToken) is { Reapplied.Count: > 0 } outcome)
            {
                logger.LogWarning("{Count} erasure(s) from the erasure ledger applied again at start-up", outcome.Reapplied.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Start-up goes on; the hourly job tries again, and the failure is in the server log.
            logger.LogError(ex, "The erasure ledger could not be replayed at start-up; it will be tried again within the hour");
        }
    }
}

/// <summary>Replays the erasure ledger every hour on the nodes that run jobs (a restore made with the server running is caught too).</summary>
internal sealed class ErasureLedgerJob(IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, TimeProvider time, ILogger<ErasureLedgerJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ErasureLedgerReplay>().RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The erasure ledger replay failed; it will be tried again");
            }
        }
    }
}
