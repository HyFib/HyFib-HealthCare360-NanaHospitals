using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>Per-facility reconciliation settings (<c>billing.gateway.reconciliation</c>).</summary>
public sealed record GatewayReconciliationSettings
{
    public const string Key = "billing.gateway.reconciliation";

    /// <summary>Fetch settlements and list exceptions. Off: nothing is fetched (payments are still re-checked).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>An online receipt not settled after this many days is an exception (Razorpay settles in 2 working days).</summary>
    public int SettlementDays { get; init; } = 3;

    /// <summary>A portal checkout nobody paid stops being checked and is marked expired after this many hours.</summary>
    public int CheckoutExpiryHours { get; init; } = 48;

    public int EffectiveSettlementDays => Math.Clamp(SettlementDays, 1, 30);

    public int EffectiveCheckoutExpiryHours => Math.Clamp(CheckoutExpiryHours, 1, 24 * 30);

    public static async Task<GatewayReconciliationSettings> ReadAsync(ISettingsService settings, Guid facilityId, CancellationToken ct)
    {
        try
        {
            return await settings.GetAsync<GatewayReconciliationSettings>(Key, facilityId, ct) ?? new GatewayReconciliationSettings();
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed setting must not stop reconciliation: the defaults apply until it is corrected.
            return new GatewayReconciliationSettings();
        }
    }
}

/// <summary>
/// The exceptions of the payment-gateway reconciliation (<see cref="GatewayDiscrepancy"/>): opened or refreshed by
/// whoever finds the cause (the settlement match, the payment re-check, the refund follow-up), cleared when the cause
/// goes away, listed worst first, and resolved by an accountant with a note that is audited.
/// </summary>
public sealed class GatewayDiscrepancyService(
    HospitalDbContext db,
    IAuditLogger audit,
    ICurrentUser currentUser,
    ISettingsService settings,
    TimeProvider time)
{
    /// <summary>
    /// Opens the exception, or refreshes it when it is already known (one per kind and gateway id). Details are filled in
    /// while it is open; one a person has resolved stays resolved. Staged in the caller's unit of work.
    /// </summary>
    internal async Task<GatewayDiscrepancy> RaiseAsync(
        DiscrepancyKind kind, string key, Guid facilityId, string summary, Action<GatewayDiscrepancy> details, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var discrepancy = await FindAsync(kind, key, ct);
        if (discrepancy is null)
        {
            discrepancy = GatewayDiscrepancy.Open(facilityId, kind, key, summary, now);
            db.Add(discrepancy);
        }
        else
        {
            discrepancy.SeenAgain(summary, now);
        }

        if (discrepancy.Status == DiscrepancyStatus.Open)
        {
            details(discrepancy);
        }

        return discrepancy;
    }

    /// <summary>Clears an open exception whose cause went away (staged in the caller's unit of work).</summary>
    internal async Task ClearAsync(DiscrepancyKind kind, string key, string why, CancellationToken ct)
    {
        if (await FindAsync(kind, key, ct) is { Status: DiscrepancyStatus.Open } open)
        {
            open.Clear(why, time.GetUtcNow());
        }
    }

    public async Task<PagedResult<GatewayDiscrepancyDto>> ListAsync(PageRequest page, DiscrepancyStatus? status, DiscrepancyKind? kind, CancellationToken ct)
    {
        var rows = db.Set<GatewayDiscrepancy>().AsNoTracking().Where(d => d.Status == (status ?? DiscrepancyStatus.Open));
        if (kind is not null)
        {
            rows = rows.Where(d => d.Kind == kind);
        }

        // Worst first: the kind's rank, then the most money, then the oldest.
        var ordered = status is DiscrepancyStatus.Resolved
            ? rows.OrderByDescending(d => d.ResolvedAt)
            : rows.OrderBy(d => d.Rank).ThenByDescending(d => d.GatewayAmount ?? d.RecordedAmount).ThenBy(d => d.DetectedAt);
        var result = await ordered.ToPagedResultAsync(page, ct);
        var dtos = await ToDtosAsync(result.Items, ct);
        return new PagedResult<GatewayDiscrepancyDto>(dtos, result.Page, result.PageSize, result.TotalCount);
    }

    public async Task<GatewayDiscrepancyDto?> GetAsync(Guid id, CancellationToken ct)
    {
        var discrepancy = await db.Set<GatewayDiscrepancy>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        return discrepancy is null ? null : (await ToDtosAsync([discrepancy], ct))[0];
    }

    /// <summary>Closes an exception with what was done about it (for example "Refunded by NEFT, UTR …"). Audited.</summary>
    public async Task<Result<GatewayDiscrepancyDto>> ResolveAsync(Guid id, ResolveDiscrepancyRequest request, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return BillingDb.SignInRequired;
        }

        var discrepancy = await db.Set<GatewayDiscrepancy>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (discrepancy is null)
        {
            return Error.NotFound(nameof(GatewayDiscrepancy), id);
        }

        db.SetExpectedVersion(discrepancy, request.Version);
        var resolved = discrepancy.Resolve(userId, currentUser.DisplayName ?? currentUser.UserName, request.Note, time.GetUtcNow());
        if (resolved.IsFailure)
        {
            return resolved.Error!;
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "billing.gateway.exception_resolved", $"Resolved {Describe(discrepancy.Kind)} {discrepancy.Key}: {discrepancy.Resolution}")
        {
            EntityType = nameof(GatewayDiscrepancy),
            EntityId = discrepancy.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([discrepancy], ct))[0];
    }

    public async Task<ReconciliationSummaryDto> SummaryAsync(Guid facilityId, CancellationToken ct)
    {
        var reconciliation = await GatewayReconciliationSettings.ReadAsync(settings, facilityId, ct);
        var open = await db.Set<GatewayDiscrepancy>().AsNoTracking()
            .Where(d => d.FacilityId == facilityId && d.Status == DiscrepancyStatus.Open)
            .GroupBy(d => d.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count(), Amount = g.Sum(d => d.GatewayAmount ?? d.RecordedAmount ?? 0m) })
            .ToListAsync(ct);
        var latest = await db.Set<GatewaySettlement>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(s => s.FacilityId == facilityId || s.Lines.Any(l => l.FacilityId == facilityId))
            .OrderByDescending(s => s.SettledAt)
            .Select(s => new { s.SettledOn, s.FetchedAt })
            .FirstOrDefaultAsync(ct);
        return new ReconciliationSummaryDto(
            reconciliation.Enabled,
            reconciliation.EffectiveSettlementDays,
            open.OrderBy(o => GatewayDiscrepancy.RankOf(o.Kind)).Select(o => new DiscrepancyCountDto(o.Kind, o.Count, o.Amount)).ToList(),
            latest?.SettledOn,
            latest?.FetchedAt);
    }

    internal static string Describe(DiscrepancyKind kind) => kind switch
    {
        DiscrepancyKind.UnrecordedPayment => "payment without a receipt",
        DiscrepancyKind.UnsettledReceipt => "receipt not settled",
        DiscrepancyKind.AmountMismatch => "amount difference",
        _ => "refund difference",
    };

    internal async Task<IReadOnlyList<GatewayDiscrepancyDto>> ToDtosAsync(IReadOnlyList<GatewayDiscrepancy> items, CancellationToken ct)
    {
        var billIds = items.Where(d => d.BillId is not null).Select(d => d.BillId!.Value).Distinct().ToList();
        var receiptIds = items.Where(d => d.ReceiptId is not null).Select(d => d.ReceiptId!.Value).Distinct().ToList();
        var refundIds = items.Where(d => d.RefundId is not null).Select(d => d.RefundId!.Value).Distinct().ToList();
        var appointmentIds = items.Where(d => d.AppointmentId is not null).Select(d => d.AppointmentId!.Value).Distinct().ToList();

        var bills = billIds.Count == 0 ? [] : await db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(b => billIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.BillNumber, ct);
        var receipts = receiptIds.Count == 0 ? [] : await db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.ReceiptNumber, ct);
        var refunds = refundIds.Count == 0 ? [] : await db.Set<Refund>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => refundIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.RefundNumber, ct);
        var appointments = appointmentIds.Count == 0 ? [] : await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(a => appointmentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Number, ct);

        return items.Select(d => new GatewayDiscrepancyDto(
                d.Id, d.Kind, d.Rank, d.Status, d.Key, d.OrderId, d.Summary, d.RecordedAmount, d.GatewayAmount, d.GatewayAt,
                d.BillId, d.BillId is { } bill ? bills.GetValueOrDefault(bill) : null,
                d.IpAccountId,
                d.ReceiptId, d.ReceiptId is { } receipt ? receipts.GetValueOrDefault(receipt) : null,
                d.RefundId, d.RefundId is { } refund ? refunds.GetValueOrDefault(refund) : null,
                d.AppointmentId, d.AppointmentId is { } appointment ? appointments.GetValueOrDefault(appointment) : null,
                d.GatewayOrderRecordId,
                d is { Kind: DiscrepancyKind.UnrecordedPayment, Status: DiscrepancyStatus.Open } && (d.GatewayOrderRecordId is not null || d.AppointmentId is not null),
                d.DetectedAt, d.LastSeenAt, d.ResolvedAt, d.ResolvedByName, d.Resolution, d.ResolvedAutomatically, d.Version))
            .ToList();
    }

    private async Task<GatewayDiscrepancy?> FindAsync(DiscrepancyKind kind, string key, CancellationToken ct) =>
        db.Set<GatewayDiscrepancy>().Local.FirstOrDefault(d => d.Kind == kind && d.Key == key)
        ?? await db.Set<GatewayDiscrepancy>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(d => d.Kind == kind && d.Key == key, ct);
}
