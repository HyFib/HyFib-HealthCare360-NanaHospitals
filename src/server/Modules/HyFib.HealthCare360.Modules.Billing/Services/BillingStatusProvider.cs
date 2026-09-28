using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Read-only billing facts for lower modules (<see cref="IBillingStatusProvider"/>): whether an order line is paid
/// (for example "collect the sample once paid"), a patient's outstanding dues and IP discharge clearance.
/// </summary>
internal sealed class BillingStatusProvider(HospitalDbContext db) : IBillingStatusProvider
{
    /// <summary>
    /// State of each source line (any source key, for example "supportservices.diet" with "{dietOrderId}:{date}"), in every
    /// facility. Charges posted or cancelled earlier in the caller's unit of work count, so a job that checks and posts in
    /// one pass (a mid-day diet change) never charges the same day twice. The result is keyed by the caller's strings.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ChargeBillingState>> GetStatesAsync(
        string sourceModule, IReadOnlyCollection<string> sourceReferences, CancellationToken cancellationToken)
    {
        var module = sourceModule.Trim();
        var requested = sourceReferences.Where(r => r is not null).Distinct(StringComparer.Ordinal).ToList();
        var references = requested.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var rows = await (
                from c in db.Set<Charge>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                where c.SourceModule == module && references.Contains(c.SourceReference)
                join b in db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on c.BillId equals (Guid?)b.Id into bills
                from b in bills.DefaultIfEmpty()
                select new { c.SourceReference, c.Status, BillStatus = b == null ? (BillStatus?)null : b.Status })
            .ToListAsync(cancellationToken);

        var states = rows.ToDictionary(r => r.SourceReference, r => ToState(r.Status, r.BillStatus), StringComparer.Ordinal);
        var wanted = references.ToHashSet(StringComparer.Ordinal);
        foreach (var staged in db.Set<Charge>().Local.Where(c => c.SourceModule == module && wanted.Contains(c.SourceReference)))
        {
            var billStatus = staged.BillId is { } billId
                ? db.Set<Bill>().Local.FirstOrDefault(b => b.Id == billId)?.Status ?? rows.FirstOrDefault(r => r.SourceReference == staged.SourceReference)?.BillStatus
                : null;
            states[staged.SourceReference] = ToState(staged.Status, billStatus);
        }

        return requested.ToDictionary(r => r, r => states.GetValueOrDefault(r.Trim(), ChargeBillingState.NotFound), StringComparer.Ordinal);
    }

    /// <summary>Patient's unpaid share of finalized bills across all facilities of the group (UHIDs are shared).</summary>
    public Task<decimal> GetOutstandingAsync(Guid patientId, CancellationToken cancellationToken) =>
        db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(b => b.PatientId == patientId && (b.Status == BillStatus.Finalized || b.Status == BillStatus.PartiallyPaid))
            .SumAsync(b => b.DueAmount, cancellationToken);

    public async Task<DischargeClearance> GetDischargeClearanceAsync(Guid admissionId, CancellationToken cancellationToken) =>
        DischargeClearanceRules.Evaluate(await PositionAsync(db, admissionId, cancellationToken));

    /// <summary>
    /// Unbilled services, final bill and patient dues of an admission. A charge reserved by a draft bill still counts as
    /// unbilled: only a finalized bill clears a service.
    /// </summary>
    internal static async Task<AdmissionBillingPosition> PositionAsync(HospitalDbContext db, Guid admissionId, CancellationToken ct)
    {
        var charges = db.Set<Charge>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(c => c.AdmissionId == admissionId && c.Status != ChargeStatus.Cancelled);
        var hasCharges = await charges.AnyAsync(ct);
        var unbilledCount = await charges.CountAsync(c => c.Status == ChargeStatus.Unbilled, ct);
        var unbilledAmount = await charges.Where(c => c.Status == ChargeStatus.Unbilled).SumAsync(c => c.NetAmount, ct);

        var bills = await db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(b => b.AdmissionId == admissionId && b.Status != BillStatus.Draft && b.Status != BillStatus.Cancelled && b.Status != BillStatus.Refunded)
            .Select(b => new { b.IsFinalBill, b.DueAmount })
            .ToListAsync(ct);

        var account = await db.Set<IpAccount>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(a => a.AdmissionId == admissionId)
            .Select(a => new { Balance = a.DepositReceived - a.DepositAdjusted - a.DepositRefunded, a.DischargeWithDuesApproved })
            .FirstOrDefaultAsync(ct);

        return new AdmissionBillingPosition(
            unbilledCount, unbilledAmount, hasCharges, bills.Any(b => b.IsFinalBill), bills.Sum(b => b.DueAmount),
            account?.Balance ?? 0m, account?.DischargeWithDuesApproved ?? false);
    }

    internal static ChargeBillingState ToState(ChargeStatus status, BillStatus? billStatus) => status switch
    {
        ChargeStatus.Cancelled => ChargeBillingState.Cancelled,
        ChargeStatus.Unbilled => ChargeBillingState.Unbilled,
        _ => billStatus == BillStatus.Paid ? ChargeBillingState.Paid : ChargeBillingState.Billed,
    };
}
