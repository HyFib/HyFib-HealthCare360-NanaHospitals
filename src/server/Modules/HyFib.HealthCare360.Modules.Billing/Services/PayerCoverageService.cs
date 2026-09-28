using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Works out the payer terms of a bill (IPB-06, INS): tariff plan, the patient's valid policy, co-pay, the approved
/// pre-authorisation that caps the payer share, and which services the payer never pays for.
/// </summary>
public sealed class PayerCoverageService(HospitalDbContext db, TariffService tariffs, TimeProvider time)
{
    public Task<BillPayerTerms> ForVisitAsync(Visit visit, CancellationToken ct) =>
        BuildAsync(visit.PayerType, visit.PayerId, null, null, visit.PatientId, null, visit.Id, visit.FacilityId, ct);

    public Task<BillPayerTerms> ForAccountAsync(IpAccount account, CancellationToken ct) =>
        BuildAsync(account.PayerType, account.PayerId, account.TariffPlanId, account.PolicyId, account.PatientId, account.AdmissionId, null, account.FacilityId, ct);

    public async Task<BillPayerTerms> SelfPayAsync(Guid facilityId, CancellationToken ct) =>
        BillPayerTerms.SelfPay(PayerType.Self, (await tariffs.ResolvePlanAsync(PayerType.Self, null, facilityId, ct))?.Id);

    /// <summary>Predicate telling whether a service is non-payable for the payer (IRDAI non-payables).</summary>
    public async Task<Func<Guid, ServiceCategory, bool>> NonPayableMatcherAsync(Guid? payerId, CancellationToken ct)
    {
        if (payerId is null)
        {
            return static (_, _) => false;
        }

        var rules = await db.Set<NonPayableItem>().AsNoTracking().Where(n => n.PayerId == null || n.PayerId == payerId).ToListAsync(ct);
        return (serviceItemId, category) => rules.Any(r => r.Matches(payerId, serviceItemId, category));
    }

    private async Task<BillPayerTerms> BuildAsync(
        PayerType payerType, Guid? payerId, Guid? planId, Guid? policyId, Guid patientId, Guid? admissionId, Guid? visitId, Guid facilityId, CancellationToken ct)
    {
        planId ??= (await tariffs.ResolvePlanAsync(payerType, payerId, facilityId, ct))?.Id;
        var terms = new BillPayerTerms(payerType, payerId, planId, null, null, 0m, null);
        if (!terms.HasPayer)
        {
            return terms;
        }

        var today = BillingTime.Today(time);
        var defaultCoPay = await db.Set<Payer>().Where(p => p.Id == payerId).Select(p => (decimal?)p.DefaultCoPayPercent).FirstOrDefaultAsync(ct);
        var policy = policyId is { } id
            ? await db.Set<PatientPolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
            : await db.Set<PatientPolicy>().AsNoTracking()
                .Where(p => p.PatientId == patientId && p.PayerId == payerId && p.IsActive && p.ValidFrom <= today && p.ValidTo >= today)
                .OrderByDescending(p => p.ValidTo)
                .FirstOrDefaultAsync(ct);

        var preAuth = admissionId is null && visitId is null
            ? null
            : await db.Set<PreAuthorization>().AsNoTracking().Include(p => p.Enhancements)
                .Where(p => (admissionId != null && p.AdmissionId == admissionId) || (visitId != null && p.VisitId == visitId))
                .Where(p => p.Status == PreAuthStatus.Approved || p.Status == PreAuthStatus.PartiallyApproved)
                .OrderByDescending(p => p.ApprovedAt)
                .FirstOrDefaultAsync(ct);

        return terms with
        {
            PolicyId = policy?.Id,
            PreAuthorizationId = preAuth?.Id,
            CoPayPercent = policy?.CoPayPercent ?? defaultCoPay ?? 0m,
            CoverageLimit = PayerCoverage.CoverageLimit(payerType, preAuth?.EffectiveApprovedAmount),
        };
    }
}
