using System.Text.Json;
using FluentValidation;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Integrations;

/// <summary>
/// What a sweep found of one kind of record a simulator leaves behind (<see cref="SimulatorRecordSweep"/>).
/// </summary>
/// <param name="Severity">"critical" for money, identity or clinical records, "warning" for messages and exports, "info" for the rest.</param>
/// <param name="Amount">The money the records say moved, where they are payments or refunds (₹).</param>
/// <param name="Examples">Up to five of the newest records' references (order, refund or transaction numbers, accession numbers, UHIDs, sample barcodes), to find them by.</param>
public sealed record SimulatorRecordFinding(
    string Code, string Title, string Severity, long Count, decimal? Amount, DateTimeOffset? First, DateTimeOffset? Last,
    IReadOnlyList<string> Examples, string Guidance);

/// <summary>A sweep of the database for the records the integrations' simulators made.</summary>
public sealed record SimulatorSweep(DateTimeOffset SweptAt, string? SweptBy, IReadOnlyList<SimulatorRecordFinding> Findings)
{
    public long Total => Findings.Sum(f => f.Count);

    /// <summary>When the newest record found was made.</summary>
    public DateTimeOffset? Newest => Findings.Max(f => f.Last);
}

/// <summary>The IT officer's sign-off: they reviewed the sweep of <paramref name="SweptAt"/>, whose records went up to <paramref name="CoversUntil"/>.</summary>
public sealed record SimulatorSignOff(DateTimeOffset SignedOffAt, string? SignedOffBy, DateTimeOffset SweptAt, DateTimeOffset? CoversUntil, long Total, string Note);

/// <param name="Outstanding">The latest sweep found records no sign-off covers.</param>
/// <param name="LiveServer">This server may not use the simulators, so their records do not belong in its database.</param>
public sealed record SimulatorRecordsView(SimulatorSweep? Latest, SimulatorSignOff? SignedOff, bool Outstanding, bool LiveServer);

/// <param name="SweptAt">The sweep the IT officer reviewed: a newer one is reviewed first.</param>
public sealed record SignOffSimulatorRecordsRequest(DateTimeOffset SweptAt, string Note);

public sealed class SignOffSimulatorRecordsRequestValidator : AbstractValidator<SignOffSimulatorRecordsRequest>
{
    public SignOffSimulatorRecordsRequestValidator()
    {
        RuleFor(x => x.Note).NotEmpty().MinimumLength(10).MaximumLength(1000)
            .WithMessage("Say what was done about the records (10 to 1000 characters).");
    }
}

/// <summary>
/// Every integration has a simulator for training (<see cref="TrainingSimulators"/>), and each leaves records that look
/// real: payments marked received, refunds marked paid back, card sales approved, messages marked sent, Aadhaar numbers
/// linked, images and results that came from nowhere. A live server can no longer choose a simulator, but a database used
/// for training before it went live, or set up before HC360 refused them, still holds what they made. The sweep finds those
/// records by the marks each simulator leaves (docs/deployment/DEPLOYMENT.md, "Going live on a database used for training"); an IT
/// officer reviews them and signs off, and until then System health warns on a live server.
/// </summary>
public sealed class SimulatorRecordSweep(HospitalDbContext db, IAuditLogger audit, ICurrentUser user, IOptions<PlatformOptions> options, TimeProvider time)
{
    public const string SweptAction = "simulators.swept";
    public const string SignedOffAction = "simulators.signed_off";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Each kind of record, and the SQL that finds it: a count, the money, the first and last time, and up to five references,
    /// in columns named as the database names properties (snake case).
    /// </summary>
    internal static readonly IReadOnlyList<SimulatorRecordKind> Kinds =
    [
        new("payments", "Online payments the payment simulator took", "critical",
            "No money was received: each was marked paid as it was made. Cancel or correct the receipts they made, and the bills, deposits and appointments they settled.",
            """
            SELECT count(*) AS count, sum(o.paid_amount) AS amount, min(o.created_at) AS first, max(o.created_at) AS last,
                   (array_agg(o.order_id ORDER BY o.created_at DESC))[1:5] AS examples
            FROM billing.gateway_orders o
            WHERE lower(trim(o.provider)) = 'simulator' AND NOT o.is_deleted
            """),
        new("refunds", "Refunds the payment simulator said it paid back", "critical",
            "Nothing was paid back: the patient may still be owed the money. Check each with the patient, and pay it back another way.",
            """
            SELECT count(*) AS count, sum(r.amount) AS amount, min(r.created_at) AS first, max(r.created_at) AS last,
                   (array_agg(r.refund_number ORDER BY r.created_at DESC))[1:5] AS examples
            FROM billing.refunds r
            WHERE r.gateway_refund_id LIKE 'rfnd\_sim%' AND NOT r.is_deleted
            """),
        new("card-sales", "Card and UPI sales the terminal simulator approved", "critical",
            "No card or UPI payment was taken: correct the receipts the approved sales made, and the cashier shifts they were counted in.",
            """
            SELECT count(*) AS count, sum(t.amount) FILTER (WHERE t.status = 'Approved') AS amount, min(t.created_at) AS first,
                   max(t.created_at) AS last, (array_agg(t.transaction_id ORDER BY t.created_at DESC))[1:5] AS examples
            FROM billing.pos_transactions t
            WHERE lower(trim(t.provider)) = 'simulator' AND NOT t.is_deleted
            """),
        new("aadhaar-links", "Patients linked to an Aadhaar number by the Aadhaar simulator", "critical",
            "Nobody's fingerprint was checked: confirm each patient's Aadhaar number again with a real check, or remove it. A wrong link blocks the number's holder from being registered with it.",
            """
            SELECT count(*) AS count, NULL::numeric AS amount, min(c.created_at) AS first, max(c.created_at) AS last,
                   (array_agg(p.uhid ORDER BY c.created_at DESC))[1:5] AS examples
            FROM patients.patient_identity_checks c JOIN patients.patients p ON p.id = c.patient_id
            WHERE (lower(trim(c.provider)) = 'simulator' OR c.response_code = 'SIMULATED') AND c.linked_aadhaar
            """),
        new("imaging", "Imaging studies linked to the PACS simulator", "critical",
            "Their images were made up (patient SIMULATOR^PATIENT): review any report written on them, and link each study to its real images or mark it not done.",
            """
            SELECT count(*) AS count, NULL::numeric AS amount, min(s.pacs_linked_at) AS first, max(s.pacs_linked_at) AS last,
                   (array_agg(s.accession_number ORDER BY s.pacs_linked_at DESC))[1:5] AS examples
            FROM diagnostics.imaging_studies s
            WHERE s.study_instance_uid LIKE '1.2.826.0.1.3680043.10.1360.%' AND NOT s.is_deleted
            """),
        new("lab-results", "Results typed into the training analyser", "critical",
            "These results came from no analyser. Find the samples by the barcodes listed, and correct or withdraw any report released with them.",
            """
            SELECT count(*) AS count, NULL::numeric AS amount, min(l.created_at) AS first, max(l.created_at) AS last,
                   (array_agg(l.reference ORDER BY l.created_at DESC) FILTER (WHERE l.reference IS NOT NULL))[1:5] AS examples
            FROM integrations.integration_logs l
            WHERE l.provider LIKE 'simulator:%' AND l.operation = 'results'
            """),
        new("gateway-settlements", "Settlements the payment simulator made up", "warning",
            "No money reached the bank: reverse the bank-credit and gateway-charge entries they made in the day book.",
            """
            SELECT count(*) AS count, sum(s.amount) AS amount, min(s.fetched_at) AS first, max(s.fetched_at) AS last,
                   (array_agg(s.settlement_id ORDER BY s.fetched_at DESC))[1:5] AS examples
            FROM billing.gateway_settlements s
            WHERE lower(trim(s.provider)) = 'simulator'
            """),
        new("card-settlements", "Card settlement lines imported from the training file", "warning",
            "No bank settled them: delete the import, and reverse the charges and settlement entries it made in the day book.",
            """
            SELECT count(*) AS count, sum(l.amount) AS amount, min(l.transaction_at) AS first, max(l.transaction_at) AS last,
                   (array_agg(l.utr ORDER BY l.transaction_at DESC))[1:5] AS examples
            FROM billing.pos_settlement_lines l
            WHERE l.utr LIKE 'SIMUTR%'
            """),
        new("messages", "Messages the simulators marked sent", "warning",
            "None reached anyone: sign-in and consent codes, receipts, payment links, critical-value alerts, IT alerts. Sign-ins and family links made with a code the simulator kept were not proved by the patient's phone: review them.",
            """
            SELECT count(*) AS count, NULL::numeric AS amount, min(n.created_at) AS first, max(n.created_at) AS last,
                   (SELECT array_agg(k.template_key) FROM (SELECT DISTINCT m.template_key FROM platform.notification_logs m
                    WHERE m.provider_message_id LIKE 'SIM-%' ORDER BY m.template_key LIMIT 5) k) AS examples
            FROM platform.notification_logs n
            WHERE n.provider_message_id LIKE 'SIM-%'
            """),
        new("tally", "Day-book vouchers posted to the Tally simulator", "warning",
            "The hospital's own Tally does not have them: post those days again once Tally is set up.",
            """
            SELECT count(*) AS count, sum(v.amount) AS amount, min(v.created_at) AS first, max(v.created_at) AS last,
                   (array_agg(v.number ORDER BY v.created_at DESC))[1:5] AS examples
            FROM integrations.simulator_tally_vouchers v
            """),
        new("attendance", "Attendance punches from the terminal simulator", "warning",
            "Nobody punched in: check the attendance of those days, and the payroll runs that counted it.",
            """
            SELECT count(*) AS count, NULL::numeric AS amount, min(t.created_at) AS first, max(t.created_at) AS last,
                   (SELECT array_agg(d.terminal_id) FROM (SELECT DISTINCT u.terminal_id FROM integrations.simulator_transactions u
                    WHERE u.kind = 'TerminalPunch' AND u.terminal_id IS NOT NULL ORDER BY u.terminal_id LIMIT 5) d) AS examples
            FROM integrations.simulator_transactions t
            WHERE t.kind = 'TerminalPunch'
            """),
        new("cardiology-files", "Cardiology files sent to the PACS simulator", "info",
            "They were kept nowhere: HC360 sends them again once a real PACS is set up.",
            """
            SELECT count(*) AS count, NULL::numeric AS amount, min(o.created_at) AS first, max(o.created_at) AS last,
                   (array_agg(o.sop_instance_uid ORDER BY o.created_at DESC))[1:5] AS examples
            FROM diagnostics.cardiology_pacs_objects o
            WHERE o.destination = 'simulator' AND NOT o.is_deleted
            """),
        new("labels", "Labels the label simulator did not print", "info",
            "Nothing was printed: wristbands, tube and dispensing labels made then were never stuck on.",
            """
            SELECT count(*) AS count, NULL::numeric AS amount, min(m.created_at) AS first, max(m.created_at) AS last,
                   (array_agg(m.subject ORDER BY m.created_at DESC) FILTER (WHERE m.subject IS NOT NULL))[1:5] AS examples
            FROM integrations.simulator_messages m
            WHERE m.channel = 'Labels'
            """),
    ];

    private static readonly string[] SeverityOrder = ["info", "warning", "critical"];

    /// <summary>Sweeps the database now, and writes what it found to the audit trail, where the warning reads it.</summary>
    public async Task<SimulatorSweep> SweepAsync(CancellationToken ct)
    {
        var findings = new List<SimulatorRecordFinding>();
        foreach (var kind in Kinds)
        {
            var row = await db.Database.SqlQueryRaw<SweepRow>(kind.Sql).SingleAsync(ct);
            findings.Add(new SimulatorRecordFinding(kind.Code, kind.Title, kind.Severity, row.Count, row.Amount, row.First, row.Last,
                row.Examples ?? [], kind.Guidance));
        }

        var sweep = new SimulatorSweep(time.GetUtcNow(), user.DisplayName, findings);
        var live = !options.Value.AllowsSimulators;
        await audit.WriteAsync(new AuditEntry(AuditCategories.Integration, SweptAction, Summary(sweep))
        {
            Details = new SweepDetails(sweep.SweptAt, sweep.SweptBy,
                [.. findings.Where(f => f.Count > 0).Select(f => new SweepCount(f.Code, f.Count, f.Amount, f.First, f.Last, f.Examples))]),
            Severity = live && sweep.Total > 0 ? AuditSeverity.Warning : AuditSeverity.Info,
        }, ct);
        return sweep;
    }

    /// <summary>The latest sweep, the latest sign-off, and whether records stand that no sign-off covers.</summary>
    public async Task<SimulatorRecordsView> ViewAsync(CancellationToken ct)
    {
        var (latest, signedOff) = await LatestAsync(db, ct);
        return new SimulatorRecordsView(latest, signedOff, IsOutstanding(latest, signedOff), !options.Value.AllowsSimulators);
    }

    /// <summary>
    /// Records that the IT officer reviewed the sweep of <see cref="SignOffSimulatorRecordsRequest.SweptAt"/> and dealt with
    /// what it found, as their note says: it covers the records up to the newest that sweep found, and a later one brings the
    /// warning back.
    /// </summary>
    public async Task<Result<SimulatorSignOff>> SignOffAsync(SignOffSimulatorRecordsRequest request, CancellationToken ct)
    {
        var (latest, _) = await LatestAsync(db, ct);
        if (latest is null || latest.Total == 0)
        {
            return Error.Rule("simulators.nothing_to_sign_off", "The latest sweep found no records made by a simulator: there is nothing to sign off.");
        }

        if (latest.SweptAt != request.SweptAt)
        {
            return Error.Conflict("simulators.newer_sweep",
                $"A newer sweep, on {latest.SweptAt:dd MMM yyyy HH:mm} UTC, found {latest.Total} record(s): review it before signing off.");
        }

        var signOff = new SimulatorSignOff(time.GetUtcNow(), user.DisplayName, latest.SweptAt, latest.Newest, latest.Total, request.Note.Trim());
        await audit.WriteAsync(new AuditEntry(AuditCategories.Integration, SignedOffAction,
            $"Signed off {latest.Total} record(s) made by simulators, found by the sweep of {latest.SweptAt:dd MMM yyyy HH:mm} UTC: {signOff.Note}")
        {
            Details = new SignOffDetails(signOff.SweptAt, signOff.CoversUntil, signOff.Total, signOff.Note),
        }, ct);
        return signOff;
    }

    /// <summary>
    /// System health's warning on a live server, while the latest sweep found records no sign-off covers: as serious as the
    /// most serious kind of record not covered.
    /// </summary>
    public static async Task<HealthWarning?> WarningAsync(HospitalDbContext db, PlatformOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AllowsSimulators)
        {
            return null;
        }

        var (latest, signedOff) = await LatestAsync(db, ct);
        return Warning(latest, signedOff);
    }

    /// <summary>The warning for the latest sweep and sign-off, or null when nothing stands.</summary>
    public static HealthWarning? Warning(SimulatorSweep? latest, SimulatorSignOff? signedOff)
    {
        if (!IsOutstanding(latest, signedOff))
        {
            return null;
        }

        var standing = latest!.Findings.Where(f => f.Count > 0 && !(f.Last <= signedOff?.CoversUntil)).ToList();
        var severity = standing.Select(f => f.Severity).MaxBy(s => Array.IndexOf(SeverityOrder, s)) ?? "warning";
        return new HealthWarning("simulators.records", severity == "info" ? "warning" : severity,
            $"This live server's database holds records the integrations' simulators made, as if real: {string.Join("; ", standing.Select(f => $"{f.Title.ToLowerInvariant()} ({f.Count})"))}. "
            + "Review them under Administration › Simulator records, deal with each, and sign them off.");
    }

    private static bool IsOutstanding(SimulatorSweep? latest, SimulatorSignOff? signedOff) =>
        latest is { Total: > 0 } && !(latest.Newest <= signedOff?.CoversUntil);

    private static async Task<(SimulatorSweep? Latest, SimulatorSignOff? SignedOff)> LatestAsync(HospitalDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var swept = await LatestEventAsync(db, SweptAction, ct);
        var signed = await LatestEventAsync(db, SignedOffAction, ct);
        return (
            swept?.Details is { } sweepJson && JsonSerializer.Deserialize<SweepDetails>(sweepJson, Json) is { } sweep ? Read(sweep) : null,
            signed?.Details is { } signJson && JsonSerializer.Deserialize<SignOffDetails>(signJson, Json) is { } off
                ? new SimulatorSignOff(signed.OccurredAt, signed.UserName, off.SweptAt, off.CoversUntil, off.Total, off.Note)
                : null);
    }

    private static Task<RecordedEvent?> LatestEventAsync(HospitalDbContext db, string action, CancellationToken ct) =>
        db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Category == AuditCategories.Integration && e.Action == action)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Select(e => new RecordedEvent(e.Details, e.UserName, e.OccurredAt))
            .FirstOrDefaultAsync(ct);

    /// <summary>A recorded sweep, with every kind: those it found nothing of are kept out of the audit trail.</summary>
    private static SimulatorSweep Read(SweepDetails details) =>
        new(details.SweptAt, details.SweptBy,
        [
            .. Kinds.Select(kind => details.Found.FirstOrDefault(f => f.Code == kind.Code) is { } found
                ? new SimulatorRecordFinding(kind.Code, kind.Title, kind.Severity, found.Count, found.Amount, found.First, found.Last, found.Examples, kind.Guidance)
                : new SimulatorRecordFinding(kind.Code, kind.Title, kind.Severity, 0, null, null, null, [], kind.Guidance)),
        ]);

    private static string Summary(SimulatorSweep sweep) =>
        sweep.Total == 0
            ? "Swept the database for records made by simulators: none."
            : $"Swept the database for records made by simulators: {string.Join(", ", sweep.Findings.Where(f => f.Count > 0).Select(f => $"{f.Count} {f.Code}"))}.";

    private sealed class SweepRow
    {
        public long Count { get; init; }

        public decimal? Amount { get; init; }

        public DateTimeOffset? First { get; init; }

        public DateTimeOffset? Last { get; init; }

        public string[]? Examples { get; init; }
    }

    private sealed record RecordedEvent(string? Details, string? UserName, DateTimeOffset OccurredAt);

    private sealed record SweepDetails(DateTimeOffset SweptAt, string? SweptBy, IReadOnlyList<SweepCount> Found);

    private sealed record SweepCount(string Code, long Count, decimal? Amount, DateTimeOffset? First, DateTimeOffset? Last, IReadOnlyList<string> Examples);

    private sealed record SignOffDetails(DateTimeOffset SweptAt, DateTimeOffset? CoversUntil, long Total, string Note);
}

/// <summary>One kind of record a simulator leaves behind, and the SQL that finds it.</summary>
internal sealed record SimulatorRecordKind(string Code, string Title, string Severity, string Guidance, string Sql);

/// <summary>
/// Sweeps a live server's database for records the simulators made, ten minutes after it starts and once a day: one that
/// went live on a database used for training finds them without anyone asking, and System health warns until they are
/// signed off.
/// </summary>
internal sealed class SimulatorSweepJob(IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, TimeProvider time, ILogger<SimulatorSweepJob> logger)
    : BackgroundService
{
    public static readonly TimeSpan FirstAfter = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs || options.Value.AllowsSimulators)
        {
            return;
        }

        try
        {
            await Task.Delay(FirstAfter, time, stoppingToken);
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var sweep = await scope.ServiceProvider.GetRequiredService<SimulatorRecordSweep>().SweepAsync(stoppingToken);
                    if (sweep.Total > 0)
                    {
                        logger.LogWarning("The database holds {Count} record(s) made by simulators; System health says which", sweep.Total);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The database could not be swept for records made by simulators");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
