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
/// <param name="Examples">Up to five of the newest records' references (receipt, refund or transaction numbers, accession numbers, UHIDs, sample barcodes), to find them by.</param>
/// <param name="Open">
/// How many of them their current state does not show corrected yet (a receipt whose money is not reversed, a patient who
/// still holds the Aadhaar number the simulator linked…), for the kinds a sign-off needs corrected; null for the kinds a
/// sign-off acknowledges.
/// </param>
/// <param name="OpenAmount">The money still recorded as moved by the open ones (₹).</param>
public sealed record SimulatorRecordFinding(
    string Code, string Title, string Severity, long Count, decimal? Amount, DateTimeOffset? First, DateTimeOffset? Last,
    IReadOnlyList<string> Examples, string Guidance, long? Open = null, decimal? OpenAmount = null);

/// <summary>A sweep of the database for the records the integrations' simulators made.</summary>
public sealed record SimulatorSweep(DateTimeOffset SweptAt, string? SweptBy, IReadOnlyList<SimulatorRecordFinding> Findings)
{
    public long Total => Findings.Sum(f => f.Count);

    /// <summary>When the newest record found was made.</summary>
    public DateTimeOffset? Newest => Findings.Max(f => f.Last);

    /// <summary>The critical records the sweep found not corrected yet: while any stands, no sign-off is taken.</summary>
    public long CriticalOpen => Findings.Where(f => f.Severity == "critical").Sum(f => f.Open ?? 0);
}

/// <summary>
/// A record a simulator made that its current state does not show corrected yet, with the record to correct it on:
/// <paramref name="Entity"/> is Receipt, Refund, PharmacySale, CanteenSale, Patient, ImagingStudy, LabOrder,
/// UnmatchedLabResult or EInvoice.
/// </summary>
public sealed record SimulatorOpenRecord(string Reference, DateTimeOffset? At, decimal? Amount, string Entity, Guid EntityId, Guid? PatientId);

/// <summary>The IT officer's sign-off: they reviewed the sweep of <paramref name="SweptAt"/>, whose records went up to <paramref name="CoversUntil"/>.</summary>
public sealed record SimulatorSignOff(DateTimeOffset SignedOffAt, string? SignedOffBy, DateTimeOffset SweptAt, DateTimeOffset? CoversUntil, long Total, string Note);

/// <param name="Outstanding">The latest sweep found critical records still open, or records no sign-off covers.</param>
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

    /// <summary>How many open records of a kind <see cref="OpenRecordsAsync"/> lists, newest first.</summary>
    public const int MaxOpenRecords = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The receipt lines of money the payment simulator took, by the payment ids it gives (<c>pay_sim…</c>, since it was
    /// first built), with what reversals have taken back of each.
    /// </summary>
    private const string SimulatorGatewayLines = """
        SELECT r.receipt_number AS reference, r.received_at AS at, p.amount, r.id AS receipt_id, r.patient_id,
               coalesce((SELECT sum(x.amount) FROM billing.receipt_reversal_lines x WHERE x.receipt_payment_id = p.id), 0) AS reversed
        FROM billing.receipt_payments p
        JOIN billing.receipts r ON r.id = p.receipt_id
        WHERE p.mode = 'PaymentGateway' AND p.external_id LIKE 'pay\_sim%' AND NOT r.is_deleted
        """;

    /// <summary>
    /// Each kind of record, and the SQL that finds it: a count, the money, the first and last time, and up to five references,
    /// in columns named as the database names properties (snake case). The critical kinds also say which of them are still
    /// open (<see cref="SimulatorRecordKind.OpenSql"/>): until their current state shows them corrected, no sign-off is taken.
    /// </summary>
    internal static readonly IReadOnlyList<SimulatorRecordKind> Kinds =
    [
        new("payments", "Online payments the payment simulator took", "critical",
            "No money was received, though each was marked paid: bill payments, IP deposits, appointment prepayments and payments kept as an advance. Record each receipt's money as not received (Billing › Receipts › Money not received; a second person approves): the bills, deposits and advances it settled owe again or hold less.",
            // The receipt lines of the money, whichever way it was recorded: appointment prepayments and payments kept as an
            // advance make no gateway order.
            $"""
            SELECT count(*) AS count, sum(g.amount) AS amount, min(g.at) AS first, max(g.at) AS last,
                   (array_agg(g.reference ORDER BY g.at DESC))[1:5] AS examples
            FROM ({SimulatorGatewayLines}) g
            """,
            // Open until reversals take all of the line's money back.
            $"""
            SELECT g.reference, g.at, g.amount - g.reversed AS amount, 'Receipt' AS entity, g.receipt_id AS entity_id, g.patient_id
            FROM ({SimulatorGatewayLines}) g
            WHERE g.reversed < g.amount
            """),
        new("refunds", "Refunds the payment simulator said it paid back", "critical",
            "Nothing was paid back: the patient may still be owed the money. Record each refund as not paid out (the refund › Not paid out; a second person approves), check with the patient, and pay it back another way with a new refund.",
            """
            SELECT count(*) AS count, sum(r.amount) AS amount, min(r.created_at) AS first, max(r.created_at) AS last,
                   (array_agg(r.refund_number ORDER BY r.created_at DESC))[1:5] AS examples
            FROM billing.refunds r
            WHERE r.gateway_refund_id LIKE 'rfnd\_sim%' AND NOT r.is_deleted
            """,
            // Open while it stands as paid out.
            """
            SELECT r.refund_number AS reference, coalesce(r.paid_at, r.created_at) AS at, r.amount, 'Refund' AS entity, r.id AS entity_id, r.patient_id
            FROM billing.refunds r
            WHERE r.gateway_refund_id LIKE 'rfnd\_sim%' AND NOT r.is_deleted AND r.status = 'Paid' AND r.payout_reversed_at IS NULL
            """),
        new("card-sales", "Card and UPI sales the terminal simulator approved", "critical",
            "No card or UPI payment was taken: record the money of each receipt the approved sales made as not received (Billing › Receipts › Money not received; a second person approves). The day-end of that day shows it. "
            + "At the pharmacy, cancel the invoice the same day or take it back in a return, so its card or UPI money is paid back in the books; at the canteen, cancel the bill on its own day. "
            + "A canteen bill of an earlier day can no longer be cancelled: correct that day's canteen accounts in Tally and say so in the sign-off note.",
            // Only the approved sales: a declined, cancelled or timed-out one made no receipt.
            """
            SELECT count(*) AS count, sum(t.amount) AS amount, min(t.created_at) AS first, max(t.created_at) AS last,
                   (array_agg(t.transaction_id ORDER BY t.created_at DESC))[1:5] AS examples
            FROM billing.pos_transactions t
            WHERE lower(trim(t.provider)) = 'simulator' AND t.status = 'Approved' AND NOT t.is_deleted
            """,
            // Open until reversals take all of the receipt line's money back; a sale whose receipt was never made recorded no money.
            // A pharmacy payment the sale made is open until refunds (a cancellation or a return) pay all of it back; a canteen
            // bill until it is cancelled, which only its own day allows: after that, its accounts are corrected outside HC360.
            """
            SELECT r.receipt_number AS reference, r.received_at AS at,
                   p.amount - coalesce((SELECT sum(x.amount) FROM billing.receipt_reversal_lines x WHERE x.receipt_payment_id = p.id), 0) AS amount,
                   'Receipt' AS entity, r.id AS entity_id, r.patient_id
            FROM billing.pos_transactions t
            JOIN billing.receipts r ON r.id = t.receipt_id AND NOT r.is_deleted
            JOIN billing.receipt_payments p ON p.receipt_id = r.id AND p.external_id = t.transaction_id
            WHERE lower(trim(t.provider)) = 'simulator' AND t.status = 'Approved' AND NOT t.is_deleted
              AND coalesce((SELECT sum(x.amount) FROM billing.receipt_reversal_lines x WHERE x.receipt_payment_id = p.id), 0) < p.amount
            UNION ALL
            SELECT s.invoice_number AS reference, p.received_at AS at,
                   p.amount - coalesce((SELECT sum(x.amount) FROM pharmacy.sale_payments x WHERE x.refund_of_payment_id = p.id AND x.is_refund), 0) AS amount,
                   'PharmacySale' AS entity, s.id AS entity_id, s.patient_id
            FROM billing.pos_transactions t
            JOIN pharmacy.sale_payments p ON p.terminal_sale_id = t.id AND NOT p.is_refund
            JOIN pharmacy.sales s ON s.id = p.pharmacy_sale_id AND NOT s.is_deleted
            WHERE lower(trim(t.provider)) = 'simulator' AND t.status = 'Approved' AND NOT t.is_deleted
              AND coalesce((SELECT sum(x.amount) FROM pharmacy.sale_payments x WHERE x.refund_of_payment_id = p.id AND x.is_refund), 0) < p.amount
            UNION ALL
            SELECT c.bill_number AS reference, c.sold_at AS at, c.net_amount AS amount, 'CanteenSale' AS entity, c.id AS entity_id, c.patient_id
            FROM billing.pos_transactions t
            JOIN supportservices.canteen_sales c ON c.terminal_sale_id = t.id AND NOT c.is_deleted
            WHERE lower(trim(t.provider)) = 'simulator' AND t.status = 'Approved' AND NOT t.is_deleted
              AND c.status <> 'Cancelled' AND c.business_date = (now() AT TIME ZONE 'Asia/Kolkata')::date
            """),
        new("aadhaar-links", "Patients linked to an Aadhaar number by the Aadhaar simulator", "critical",
            "Nobody's fingerprint was checked: remove each patient's Aadhaar number (the patient › Edit › remove the Aadhaar number), or confirm it with a real check. A wrong link blocks the number's holder from being registered with it.",
            """
            SELECT count(*) AS count, NULL::numeric AS amount, min(c.created_at) AS first, max(c.created_at) AS last,
                   (array_agg(p.uhid ORDER BY c.created_at DESC))[1:5] AS examples
            FROM patients.patient_identity_checks c JOIN patients.patients p ON p.id = c.patient_id
            WHERE (lower(trim(c.provider)) = 'simulator' OR c.response_code = 'SIMULATED') AND c.linked_aadhaar
            """,
            // Open while the record still holds the number the simulator linked (only its last four digits are kept to compare)
            // and no real check has confirmed it since: removed or replaced, or a real fingerprint match of it, closes it.
            """
            SELECT p.uhid AS reference, c.created_at AS at, NULL::numeric AS amount, 'Patient' AS entity, p.id AS entity_id, p.id AS patient_id
            FROM patients.patient_identity_checks c JOIN patients.patients p ON p.id = c.patient_id
            WHERE (lower(trim(c.provider)) = 'simulator' OR c.response_code = 'SIMULATED') AND c.linked_aadhaar AND NOT c.is_deleted
              AND NOT p.is_deleted AND p.aadhaar_index IS NOT NULL AND p.aadhaar_last4 = c.aadhaar_last4
              AND NOT EXISTS (SELECT 1 FROM patients.patient_identity_checks v
                              WHERE v.patient_id = p.id AND v.created_at > c.created_at AND NOT v.is_deleted
                                AND lower(trim(v.provider)) <> 'simulator' AND v.response_code IS DISTINCT FROM 'SIMULATED'
                                AND v.outcome = 'Verified' AND NOT v.used_virtual_id AND v.aadhaar_last4 = p.aadhaar_last4)
            """),
        new("imaging", "Imaging studies linked to the PACS simulator", "critical",
            "Their images were made up (patient SIMULATOR^PATIENT): link each study to its real images (Radiology › the study › Reconcile with PACS), or cancel it as not done; where a report was signed on them, add an addendum saying so.",
            """
            SELECT count(*) AS count, NULL::numeric AS amount, min(s.pacs_linked_at) AS first, max(s.pacs_linked_at) AS last,
                   (array_agg(s.accession_number ORDER BY s.pacs_linked_at DESC))[1:5] AS examples
            FROM diagnostics.imaging_studies s
            WHERE s.study_instance_uid LIKE '1.2.826.0.1.3680043.10.1360.%' AND NOT s.is_deleted
            """,
            // A study linked to its real images leaves the simulator's UIDs, and so the sweep. Open until cancelled, or until an
            // addendum on its report, written after the images were linked, says what was done.
            """
            SELECT s.accession_number AS reference, s.pacs_linked_at AS at, NULL::numeric AS amount, 'ImagingStudy' AS entity, s.id AS entity_id, s.patient_id
            FROM diagnostics.imaging_studies s
            WHERE s.study_instance_uid LIKE '1.2.826.0.1.3680043.10.1360.%' AND NOT s.is_deleted AND s.status <> 'Cancelled'
              AND NOT EXISTS (SELECT 1 FROM diagnostics.radiology_reports rr
                              JOIN diagnostics.radiology_addenda ad ON ad.radiology_report_id = rr.id AND NOT ad.is_deleted
                              WHERE rr.imaging_study_id = s.id AND ad.added_at > s.pacs_linked_at)
            """),
        new("lab-results", "Results typed into the training analyser", "critical",
            "These results came from no analyser. Find the samples by the barcodes listed: enter each result again from a real measurement before release, or amend the released report with it; discard the ones still waiting unmatched.",
            // Only the messages HC360 took: a rejected one gave no results.
            """
            SELECT count(*) AS count, NULL::numeric AS amount, min(l.created_at) AS first, max(l.created_at) AS last,
                   (array_agg(l.reference ORDER BY l.created_at DESC) FILTER (WHERE l.reference IS NOT NULL))[1:5] AS examples
            FROM integrations.integration_logs l
            WHERE l.provider LIKE 'simulator:%' AND l.operation = 'results' AND l.success
            """,
            // Per sample of a message: open while a result on it from that analyser still holds the analyser's value (entering
            // it again or amending it clears the analyser's fields), or while one waits unmatched. The training analyser marks
            // every result final, so a value it gave always has its status.
            """
            SELECT smp.barcode AS reference, l.created_at AS at, NULL::numeric AS amount, 'LabOrder' AS entity, smp.lab_order_id AS entity_id, smp.patient_id
            FROM integrations.integration_logs l
            JOIN diagnostics.lab_samples smp ON smp.barcode = ANY (string_to_array(l.reference, ',')) AND NOT smp.is_deleted
            WHERE l.provider LIKE 'simulator:%' AND l.operation = 'results' AND l.success
              AND EXISTS (SELECT 1 FROM diagnostics.lab_results r
                          WHERE r.sample_id = smp.id AND r.instrument_code = split_part(l.provider, ':', 2)
                            AND r.instrument_status IS NOT NULL AND NOT r.is_deleted)
            UNION ALL
            SELECT u.sample_barcode AS reference, l.created_at AS at, NULL::numeric AS amount, 'UnmatchedLabResult' AS entity, u.id AS entity_id, u.patient_id
            FROM integrations.integration_logs l
            JOIN diagnostics.lab_unmatched_results u ON u.sample_barcode = ANY (string_to_array(l.reference, ','))
                 AND u.instrument_code = split_part(l.provider, ':', 2) AND u.status = 'Open' AND NOT u.is_deleted
            WHERE l.provider LIKE 'simulator:%' AND l.operation = 'results' AND l.success
            """),
        new("einvoices", "IRNs the e-invoice portal simulator made up", "critical",
            "The Invoice Registration Portal never registered these invoices and notes, though they print an IRN and a QR code: the buyer's GSTR-2B and the hospital's GSTR-1 do not hold them. Cancel each IRN here as cancelled on the portal (the e-invoice register › the document), then get a real IRN from the portal; where the portal's 30 days have passed, settle it with the GST adviser.",
            """
            SELECT count(*) AS count, sum(e.invoice_value) AS amount, min(e.created_at) AS first, max(e.created_at) AS last,
                   (array_agg(e.document_number ORDER BY e.created_at DESC))[1:5] AS examples
            FROM billing.einvoices e
            WHERE lower(trim(e.provider)) = 'simulator' AND NOT e.is_deleted
            """,
            // Open while the made-up IRN stands as registered.
            """
            SELECT e.document_number AS reference, e.created_at AS at, e.invoice_value AS amount, 'EInvoice' AS entity, e.id AS entity_id, NULL::uuid AS patient_id
            FROM billing.einvoices e
            WHERE lower(trim(e.provider)) = 'simulator' AND NOT e.is_deleted AND e.status = 'Registered'
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
            var open = kind.OpenSql is null ? null : await db.Database.SqlQueryRaw<SweepRow>(OpenTotals(kind.OpenSql)).SingleAsync(ct);
            findings.Add(new SimulatorRecordFinding(kind.Code, kind.Title, kind.Severity, row.Count, row.Amount, row.First, row.Last,
                row.Examples ?? [], kind.Guidance, open?.Count, open?.Amount));
        }

        var sweep = new SimulatorSweep(time.GetUtcNow(), user.DisplayName, findings);
        var live = !options.Value.AllowsSimulators;
        await audit.WriteAsync(new AuditEntry(AuditCategories.Integration, SweptAction, Summary(sweep))
        {
            Details = new SweepDetails(sweep.SweptAt, sweep.SweptBy,
                [.. findings.Where(f => f.Count > 0).Select(f => new SweepCount(f.Code, f.Count, f.Amount, f.First, f.Last, f.Examples, f.Open, f.OpenAmount))]),
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
    /// The records of kind <paramref name="code"/> still open now, newest first (up to <see cref="MaxOpenRecords"/>), each with
    /// the record to correct it on; none for a kind the sign-off acknowledges.
    /// </summary>
    public async Task<Result<IReadOnlyList<SimulatorOpenRecord>>> OpenRecordsAsync(string code, CancellationToken ct)
    {
        if (Kinds.FirstOrDefault(k => k.Code == code) is not { } kind)
        {
            return Error.NotFound("Simulator record kind", code);
        }

        if (kind.OpenSql is null)
        {
            return Result<IReadOnlyList<SimulatorOpenRecord>>.Ok([]);
        }

        var rows = await db.Database.SqlQueryRaw<OpenRow>(OpenList(kind.OpenSql)).ToListAsync(ct);
        return Result<IReadOnlyList<SimulatorOpenRecord>>.Ok(
            [.. rows.Select(r => new SimulatorOpenRecord(r.Reference, r.At, r.Amount, r.Entity, r.EntityId, r.PatientId))]);
    }

    /// <summary>
    /// Records that the IT officer reviewed the sweep of <see cref="SignOffSimulatorRecordsRequest.SweptAt"/> and dealt with
    /// what it found, as their note says: it covers the records up to the newest that sweep found, and a later one brings the
    /// warning back. Refused while a critical record is still open, now or in that sweep: the sign-off rests on the records
    /// showing them corrected, not on the note.
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

        if (SignOffRefusal(latest, await OpenNowAsync(ct)) is { } refused)
        {
            return refused;
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
    /// Why the sweep <paramref name="latest"/> may not be signed off while <paramref name="openNow"/> critical records (by kind
    /// code) are open now, or null: each must be corrected first, and the sweep signed off must show them corrected.
    /// </summary>
    public static Error? SignOffRefusal(SimulatorSweep latest, IReadOnlyDictionary<string, long> openNow)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(openNow);
        var open = Kinds.Where(k => k.Severity == "critical" && openNow.GetValueOrDefault(k.Code) > 0).ToList();
        if (open.Count > 0)
        {
            return Error.Rule("simulators.critical_open",
                $"{open.Sum(k => openNow[k.Code])} record(s) the simulators made are not corrected yet: "
                + $"{string.Join("; ", open.Select(k => $"{k.Title.ToLowerInvariant()} ({openNow[k.Code]})"))}. "
                + "Correct each as its kind says, sweep again, and sign off that sweep.");
        }

        if (latest.CriticalOpen > 0)
        {
            return Error.Rule("simulators.critical_open",
                $"The sweep of {latest.SweptAt:dd MMM yyyy HH:mm} UTC found {latest.CriticalOpen} record(s) not corrected, corrected since: sweep again, and sign off that sweep.");
        }

        return null;
    }

    /// <summary>
    /// System health's warning on a live server, while critical records are still open or the latest sweep found records no
    /// sign-off covers: critical while any critical record is open, otherwise as serious as the most serious kind not covered
    /// (critical records corrected wait only for the sign-off).
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

        var open = latest!.Findings.Where(f => f.Severity == "critical" && f.Open > 0).ToList();
        if (open.Count > 0)
        {
            return new HealthWarning("simulators.records", "critical",
                $"This live server's database holds records the integrations' simulators made, as if real, not corrected yet: {string.Join("; ", open.Select(f => $"{f.Title.ToLowerInvariant()} ({f.Open} of {f.Count} still open)"))}. "
                + "Correct each as Administration › Simulator records says, sweep again, and sign them off.");
        }

        var standing = latest.Findings.Where(f => f.Count > 0 && !(f.Last <= signedOff?.CoversUntil)).ToList();
        var severity = standing.Select(f => f.Severity == "critical" && f.Open == 0 ? "warning" : f.Severity).MaxBy(s => Array.IndexOf(SeverityOrder, s)) ?? "warning";
        return new HealthWarning("simulators.records", severity == "info" ? "warning" : severity,
            $"This live server's database holds records the integrations' simulators made, as if real: {string.Join("; ", standing.Select(f => $"{f.Title.ToLowerInvariant()} ({f.Count})"))}. "
            + "Review them under Administration › Simulator records, deal with each, and sign them off.");
    }

    private static bool IsOutstanding(SimulatorSweep? latest, SimulatorSignOff? signedOff) =>
        latest is { } sweep && (sweep.CriticalOpen > 0 || (sweep.Total > 0 && !(sweep.Newest <= signedOff?.CoversUntil)));

    /// <summary>The critical records open now, by kind code.</summary>
    private async Task<IReadOnlyDictionary<string, long>> OpenNowAsync(CancellationToken ct)
    {
        var open = new Dictionary<string, long>();
        foreach (var kind in Kinds.Where(k => k.Severity == "critical" && k.OpenSql is not null))
        {
            open[kind.Code] = (await db.Database.SqlQueryRaw<SweepRow>(OpenTotals(kind.OpenSql!)).SingleAsync(ct)).Count;
        }

        return open;
    }

    /// <summary>The open records of a kind, newest first (the kinds' own SQL only: nothing from a request goes into it).</summary>
    private static string OpenList(string openSql) =>
        $"SELECT o.reference, o.at, o.amount, o.entity, o.entity_id, o.patient_id FROM ({openSql}) o ORDER BY o.at DESC NULLS LAST LIMIT {MaxOpenRecords}";

    /// <summary>How many records the open query of a kind lists, and their money.</summary>
    private static string OpenTotals(string openSql) =>
        $"SELECT count(*) AS count, sum(o.amount) AS amount, min(o.at) AS first, max(o.at) AS last, NULL::text[] AS examples FROM ({openSql}) o";

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
                ? new SimulatorRecordFinding(kind.Code, kind.Title, kind.Severity, found.Count, found.Amount, found.First, found.Last, found.Examples, kind.Guidance,
                    found.Open, found.OpenAmount)
                : new SimulatorRecordFinding(kind.Code, kind.Title, kind.Severity, 0, null, null, null, [], kind.Guidance, kind.OpenSql is null ? null : 0L)),
        ]);

    private static string Summary(SimulatorSweep sweep) =>
        sweep.Total == 0
            ? "Swept the database for records made by simulators: none."
            : $"Swept the database for records made by simulators: {string.Join(", ", sweep.Findings.Where(f => f.Count > 0).Select(f => f.Open > 0 ? $"{f.Count} {f.Code} ({f.Open} still open)" : $"{f.Count} {f.Code}"))}.";

    private sealed class SweepRow
    {
        public long Count { get; init; }

        public decimal? Amount { get; init; }

        public DateTimeOffset? First { get; init; }

        public DateTimeOffset? Last { get; init; }

        public string[]? Examples { get; init; }
    }

    private sealed class OpenRow
    {
        public string Reference { get; init; } = string.Empty;

        public DateTimeOffset? At { get; init; }

        public decimal? Amount { get; init; }

        public string Entity { get; init; } = string.Empty;

        public Guid EntityId { get; init; }

        public Guid? PatientId { get; init; }
    }

    private sealed record RecordedEvent(string? Details, string? UserName, DateTimeOffset OccurredAt);

    private sealed record SweepDetails(DateTimeOffset SweptAt, string? SweptBy, IReadOnlyList<SweepCount> Found);

    private sealed record SweepCount(
        string Code, long Count, decimal? Amount, DateTimeOffset? First, DateTimeOffset? Last, IReadOnlyList<string> Examples, long? Open = null, decimal? OpenAmount = null);

    private sealed record SignOffDetails(DateTimeOffset SweptAt, DateTimeOffset? CoversUntil, long Total, string Note);
}

/// <summary>
/// One kind of record a simulator leaves behind, and the SQL that finds it; for a critical kind, also the SQL that lists those
/// still open (reference, at, amount, entity, entity_id, patient_id): the ones whose current state does not show them corrected.
/// </summary>
internal sealed record SimulatorRecordKind(string Code, string Title, string Severity, string Guidance, string Sql, string? OpenSql = null);

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
