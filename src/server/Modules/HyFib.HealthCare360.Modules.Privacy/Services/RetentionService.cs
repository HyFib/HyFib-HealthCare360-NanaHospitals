using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Privacy.Services;

/// <summary>A line of the retention schedule with the period in force and what the last clean-up removed.</summary>
public sealed record RetentionRuleDto(
    string Key,
    string Title,
    string Description,
    string Basis,
    RetentionAction Action,
    RetentionUnit Unit,
    int? Value,
    int? DefaultValue,
    int? MinimumValue,
    int MaximumValue,
    bool Editable,
    int? LastRemoved,
    DateTimeOffset? LastRunAt,
    uint? Version);

public sealed record RetentionRunDto(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    RetentionRunTrigger Trigger,
    Guid? StartedByUserId,
    string? StartedByName,
    int TotalRemoved,
    IReadOnlyList<RetentionRunResult> Results,
    string? Error);

/// <summary>What is past its period and waits for a decision: charts for the medical records team, and erasures on hold.</summary>
public sealed record RetentionReview(int OutpatientChartsDue, int InpatientChartsDue, IReadOnlyList<ErasureHold> ErasureHolds);

/// <summary>An erasure request whose remaining records the law required to be kept, and until when.</summary>
public sealed record ErasureHold(
    Guid RequestId,
    string RequestNumber,
    Guid? PatientId,
    string? Uhid,
    string PrincipalName,
    DateOnly? RetainedUntil,
    bool RetainedPermanently,
    bool IsDue);

/// <summary>
/// The retention schedule (<see cref="RetentionSchedule"/>): the periods in force, changes to them, the clean-up of
/// operational records past their period, and the review list for what is never deleted automatically. The clean-up
/// deletes in small batches with set-based statements, and records each run (with an audit entry) because such
/// deletes bypass the per-row audit.
/// </summary>
public sealed class RetentionService(
    HospitalDbContext db,
    ISettingsService settings,
    IAuditLogger audit,
    ICurrentUser currentUser,
    IOptions<PlatformOptions> options,
    TimeProvider time)
{
    /// <summary>Set when an administrator asks for a clean-up now; the job picks it up within minutes.</summary>
    public const string RunRequestedSetting = "privacy.retention.run_requested";

    private const int Batch = 1000;

    public async Task<IReadOnlyList<RetentionRuleDto>> RulesAsync(CancellationToken ct)
    {
        var lastRun = await db.Set<RetentionRun>().AsNoTracking()
            .Where(r => r.FinishedAt != null)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);
        var rules = new List<RetentionRuleDto>();
        foreach (var rule in RetentionSchedule.Rules)
        {
            var removed = lastRun?.Results.FirstOrDefault(r => r.Key == rule.Key)?.Removed;
            rules.Add(new RetentionRuleDto(rule.Key, rule.Title, rule.Description, rule.Basis, rule.Action, rule.Unit, await ValueAsync(rule, ct),
                rule.DefaultValue, rule.MinimumValue, rule.MaximumValue, rule.Editable, removed, removed is null ? null : lastRun!.FinishedAt,
                await SettingVersions.OfAsync(db, rule.SettingKey, null, ct)));
        }

        return rules;
    }

    /// <summary>The period in force for <paramref name="rule"/>; null when it is kept permanently.</summary>
    public async Task<int?> ValueAsync(RetentionRule rule, CancellationToken ct) => rule.Key == RetentionSchedule.Backups
        ? Math.Max(options.Value.Backup.RetentionDays, 1)
        : rule.Effective(ReadInt(await settings.GetAsync<JsonElement?>(rule.SettingKey, null, ct)));

    /// <summary>
    /// Changes a period, within the range the law allows. Changing a medical records period also moves the retention
    /// date of every chart already completed (Clinical does that in the same transaction).
    /// </summary>
    public async Task<Result> UpdateAsync(string key, int value, uint? version, CancellationToken ct)
    {
        if (RetentionSchedule.Find(key) is not { } rule)
        {
            return Error.NotFound("Retention rule", key);
        }

        if (!rule.Editable || rule.DefaultValue is null)
        {
            return Error.Rule("privacy.retention_fixed", $"The period for {rule.Title.ToLowerInvariant()} cannot be changed here.");
        }

        var minimum = rule.MinimumValue ?? 1;
        if (value < minimum || value > rule.MaximumValue)
        {
            return Error.Field("value", string.Create(CultureInfo.InvariantCulture,
                $"Choose between {minimum} and {rule.MaximumValue} {(rule.Unit == RetentionUnit.Years ? "years" : "days")}."));
        }

        var previous = await ValueAsync(rule, ct) ?? rule.DefaultValue.Value;
        if (previous == value)
        {
            return Result.Success();
        }

        // One transaction: Clinical's handler moves the chart dates with a set-based update while the change is being
        // saved, before the save's own transaction would start. The dates, the setting, the change and its audit entry
        // commit together or not at all, so a failed save never leaves the charts moved and a retry never moves them twice.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Add(RetentionChange.Record(rule, previous, value, currentUser.UserId, time.GetUtcNow()));
        audit.Add(new AuditEntry(AuditCategories.Admin, "privacy.retention.changed",
            string.Create(CultureInfo.InvariantCulture, $"Retention for {rule.Title}: {previous} → {value} {rule.Unit.ToString().ToLowerInvariant()}"))
        {
            EntityType = nameof(RetentionRule),
            EntityId = rule.Key,
            Details = new { rule.Key, previous, value },
        });

        await settings.SetAsync(rule.SettingKey, value, null, false, version, ct);
        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<RetentionRunDto>> RunsAsync(int take, CancellationToken ct)
    {
        var runs = await db.Set<RetentionRun>().AsNoTracking().OrderByDescending(r => r.StartedAt).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
        var starters = runs.Select(r => r.StartedByUserId).OfType<Guid>().Distinct().ToList();
        var names = await db.Set<User>().IgnoreQueryFilters().Where(u => starters.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return [.. runs.Select(r => new RetentionRunDto(r.Id, r.StartedAt, r.FinishedAt, r.Trigger, r.StartedByUserId,
            r.StartedByUserId is { } id ? names.GetValueOrDefault(id) : null, r.TotalRemoved, r.Results, r.Error))];
    }

    /// <summary>How many records the clean-up would remove now, without removing anything.</summary>
    public async Task<IReadOnlyList<RetentionRunResult>> PreviewAsync(CancellationToken ct)
    {
        var results = new List<RetentionRunResult>();
        foreach (var (rule, before) in await DueRulesAsync(ct))
        {
            results.Add(new RetentionRunResult { Key = rule.Key, Before = before, Removed = await PurgeAsync(rule.Key, before, countOnly: true, ct) });
        }

        return results;
    }

    /// <summary>
    /// Asks for a clean-up now. It runs in the background (on a server that runs jobs), because a first clean-up of a
    /// year of records can take minutes; the runs list shows it when it is done.
    /// </summary>
    public async Task RequestRunAsync(CancellationToken ct)
    {
        await settings.SetAsync(RunRequestedSetting, new RunRequest(currentUser.UserId, time.GetUtcNow()), null, false, ct);
        audit.Add(new AuditEntry(AuditCategories.Admin, "privacy.retention.requested", "Retention clean-up requested"));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The pending request for a clean-up now, if any, taking it off the queue.</summary>
    public async Task<RunRequest?> TakeRunRequestAsync(CancellationToken ct)
    {
        var request = await settings.GetAsync<RunRequest?>(RunRequestedSetting, null, ct);
        if (request is not null)
        {
            await settings.SetAsync<RunRequest?>(RunRequestedSetting, null, null, false, ct);
        }

        return request;
    }

    /// <summary>
    /// Removes the operational records past their period. Each kind is done separately, so a failure part-way keeps what
    /// was already done; the run records the error and the next run carries on.
    /// </summary>
    public async Task<RetentionRun> RunAsync(RetentionRunTrigger trigger, Guid? startedByUserId, CancellationToken ct)
    {
        var run = new RetentionRun
        {
            StartedAt = time.GetUtcNow(),
            Trigger = trigger,
            StartedByUserId = startedByUserId,
        };
        db.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            foreach (var (rule, before) in await DueRulesAsync(ct))
            {
                var removed = await PurgeAsync(rule.Key, before, countOnly: false, ct);
                run.Results = [.. run.Results, new RetentionRunResult { Key = rule.Key, Before = before, Removed = removed }];
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
        }

        run.FinishedAt = time.GetUtcNow();
        audit.Add(new AuditEntry(AuditCategories.Data, "privacy.retention.run",
            string.Create(CultureInfo.InvariantCulture, $"Retention clean-up removed {run.TotalRemoved} record(s)"))
        {
            EntityType = nameof(RetentionRun),
            EntityId = run.Id.ToString(),
            Details = new { trigger = trigger.ToString(), results = run.Results, run.Error },
            Severity = run.Error is null ? AuditSeverity.Info : AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>
    /// What waits for a decision: archived charts past their retention date (never medico-legal ones), and erasure
    /// requests whose records had to be kept. The charts themselves are reviewed in Medical Records.
    /// </summary>
    public async Task<RetentionReview> ReviewAsync(CancellationToken ct)
    {
        var today = time.Today();
        var due = await db.Set<MedicalRecord>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(m => m.Status == MedicalRecordStatus.Archived && !m.IsMlc && m.RetainUntil != null && m.RetainUntil < today)
            .GroupBy(m => m.AdmissionId != null)
            .Select(g => new { Inpatient = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var holds = await (
                from r in db.Set<DataRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                join p in db.Set<Patient>() on r.PatientId equals p.Id into patients
                from p in patients.DefaultIfEmpty()
                where r.Type == DataRequestType.Erasure
                      && (r.Status == DataRequestStatus.Completed || r.Status == DataRequestStatus.PartlyCompleted)
                      && (r.RetainedUntil != null || r.RetainedPermanently)
                orderby r.RetainedPermanently, r.RetainedUntil
                select new { r.Id, r.RequestNumber, r.PatientId, Uhid = p != null ? p.Uhid : null, r.PrincipalName, r.RetainedUntil, r.RetainedPermanently })
            .Take(500)
            .ToListAsync(ct);

        return new RetentionReview(
            due.Where(d => !d.Inpatient).Sum(d => d.Count),
            due.Where(d => d.Inpatient).Sum(d => d.Count),
            [.. holds.Select(h => new ErasureHold(h.Id, h.RequestNumber, h.PatientId, h.Uhid, h.PrincipalName, h.RetainedUntil, h.RetainedPermanently,
                !h.RetainedPermanently && h.RetainedUntil <= today))]);
    }

    /// <summary>The periods the data summary quotes, as numbers.</summary>
    public async Task<RetentionPeriods> PeriodsAsync(CancellationToken ct) => new(
        await ValueAsync(RetentionSchedule.Get(RetentionSchedule.OutpatientRecords), ct) ?? RecordRetention.MinimumOutpatientYears,
        await ValueAsync(RetentionSchedule.Get(RetentionSchedule.InpatientRecords), ct) ?? RecordRetention.MinimumInpatientYears,
        await ValueAsync(RetentionSchedule.Get(RetentionSchedule.Finance), ct) ?? 8,
        await ValueAsync(RetentionSchedule.Get(RetentionSchedule.MessageLog), ct) ?? RetentionSchedule.LogDays,
        await ValueAsync(RetentionSchedule.Get(RetentionSchedule.Audit), ct) ?? 10);

    /// <summary>
    /// "How long we keep it" for each group of the data summary, in words, with the periods in force.
    /// </summary>
    public async Task<IReadOnlyDictionary<PersonalDataRetention, string>> DescribeAsync(CancellationToken ct)
    {
        var (outpatient, inpatient, finance, messages, auditYears) = await PeriodsAsync(ct);
        return new Dictionary<PersonalDataRetention, string>
        {
            [PersonalDataRetention.MedicalRecords] = string.Create(CultureInfo.InvariantCulture,
                $"At least {outpatient} years after an out-patient visit and {inpatient} years after a hospital stay; medico-legal records permanently."),
            [PersonalDataRetention.Finance] = string.Create(CultureInfo.InvariantCulture, $"{finance} years from the bill date, as tax law requires."),
            [PersonalDataRetention.MessageLog] = string.Create(CultureInfo.InvariantCulture,
                $"The message log for {messages} days; feedback, complaints and follow-up calls with your medical records."),
            [PersonalDataRetention.Audit] = string.Create(CultureInfo.InvariantCulture, $"{auditYears} years."),
            [PersonalDataRetention.WhileInUse] = "While the account is open.",
            [PersonalDataRetention.WithRecords] = "As long as the records they relate to.",
        };
    }

    private async Task<List<(RetentionRule Rule, DateTimeOffset Before)>> DueRulesAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = new List<(RetentionRule, DateTimeOffset)>();
        foreach (var rule in RetentionSchedule.Rules.Where(r => r.Action == RetentionAction.Delete && r.Key != RetentionSchedule.Backups))
        {
            if (await ValueAsync(rule, ct) is { } value)
            {
                due.Add((rule, rule.Cutoff(now, value)));
            }
        }

        return due;
    }

    private Task<int> PurgeAsync(string key, DateTimeOffset before, bool countOnly, CancellationToken ct) => key switch
    {
        // Sent (or failed, or expired) messages only: a pending one may still be delivered.
        RetentionSchedule.Outbox => DeleteAsync(
            db.Set<OutboxMessage>().IgnoreQueryFilters().Where(m => m.Status != OutboxStatus.Pending && (m.ProcessedAt ?? m.CreatedAt) < before),
            m => m.Id, countOnly, ct),
        RetentionSchedule.MessageLog => DeleteAsync(db.Set<NotificationLog>().IgnoreQueryFilters().Where(l => l.CreatedAt < before), l => l.Id, countOnly, ct),
        RetentionSchedule.IntegrationLog => PurgeIntegrationLogsAsync(before, countOnly, ct),
        RetentionSchedule.SignInLog => PurgeSignInsAsync(before, countOnly, ct),
        _ => Task.FromResult(0),
    };

    /// <summary>The integration log and the payment gateway's webhook events (event and gateway ids only), kept alike.</summary>
    private async Task<int> PurgeIntegrationLogsAsync(DateTimeOffset before, bool countOnly, CancellationToken ct) =>
        await DeleteAsync(db.Set<IntegrationLog>().IgnoreQueryFilters().Where(l => l.CreatedAt < before), l => l.Id, countOnly, ct)
        + await DeleteAsync(db.Set<Billing.Domain.GatewayWebhookEvent>().Where(e => e.ReceivedAt < before), e => e.Id, countOnly, ct);

    /// <summary>
    /// Old sign-in code requests, sessions that ended before the cut-off, refresh tokens past their expiry, and
    /// break-the-glass grants that ended before it (the audit trail keeps every emergency access).
    /// </summary>
    private async Task<int> PurgeSignInsAsync(DateTimeOffset before, bool countOnly, CancellationToken ct)
    {
        var removed = await DeleteAsync(db.Set<AuthChallenge>().IgnoreQueryFilters().Where(c => c.CreatedAt < before), c => c.Id, countOnly, ct)
            + await DeleteAsync(db.Set<BreakGlassAccess>().Where(g => g.ExpiresAt < before), g => g.Id, countOnly, ct);
        var ended = db.Set<UserSession>().IgnoreQueryFilters().Where(s => (s.RevokedAt ?? s.ExpiresAt) < before);
        var expiredTokens = db.Set<RefreshToken>().IgnoreQueryFilters().Where(t => t.ExpiresAt < before);
        if (countOnly)
        {
            return removed + await ended.CountAsync(ct) + await expiredTokens.CountAsync(ct);
        }

        while (true)
        {
            var sessions = await ended.OrderBy(s => s.Id).Select(s => s.Id).Take(Batch).ToListAsync(ct);
            if (sessions.Count == 0)
            {
                break;
            }

            // A session's refresh tokens go with it (there is no foreign key to cascade).
            removed += await db.Set<RefreshToken>().IgnoreQueryFilters().Where(t => sessions.Contains(t.SessionId)).ExecuteDeleteAsync(ct);
            removed += await db.Set<UserSession>().IgnoreQueryFilters().Where(s => sessions.Contains(s.Id)).ExecuteDeleteAsync(ct);
        }

        return removed + await DeleteAsync(expiredTokens, t => t.Id, countOnly: false, ct);
    }

    private static async Task<int> DeleteAsync<T>(IQueryable<T> rows, Expression<Func<T, Guid>> id, bool countOnly, CancellationToken ct)
        where T : class
    {
        if (countOnly)
        {
            return await rows.CountAsync(ct);
        }

        var removed = 0;
        while (true)
        {
            var ids = await rows.OrderBy(id).Select(id).Take(Batch).ToListAsync(ct);
            if (ids.Count == 0)
            {
                return removed;
            }

            removed += await rows.Where(In(id, ids)).ExecuteDeleteAsync(ct);
        }
    }

    // row => ids.Contains(id(row))
    private static Expression<Func<T, bool>> In<T>(Expression<Func<T, Guid>> id, List<Guid> ids) =>
        Expression.Lambda<Func<T, bool>>(
            Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(Guid)], Expression.Constant(ids), id.Body),
            id.Parameters);

    private static int? ReadInt(JsonElement? json) => json switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var v) => v,
        { ValueKind: JsonValueKind.String } s when int.TryParse(s.GetString(), CultureInfo.InvariantCulture, out var v) => v,
        _ => null,
    };

    /// <summary>A request for a clean-up now: who asked, and when.</summary>
    public sealed record RunRequest(Guid? UserId, DateTimeOffset RequestedAt);
}
