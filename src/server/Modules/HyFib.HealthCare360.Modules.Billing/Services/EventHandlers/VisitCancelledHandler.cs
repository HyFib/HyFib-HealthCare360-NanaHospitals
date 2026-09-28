using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;

/// <summary>
/// A visit was cancelled — an OP visit opened in error or abandoned, or the in-patient visit of a cancelled admission
/// (Inpatient raises no admission event of its own; it cancels the admission's visit and reverses its own charges in the
/// same transaction). Billing withdraws what it created for the encounter: its own unbilled charges and the drafts they
/// sat on. Charges posted by other modules belong to them, and anything already billed stays for the billing desk to
/// credit or refund. The registration fee belongs to the patient, not the visit: it stays due and moves to the next visit.
/// </summary>
internal sealed class VisitCancelledHandler(
    HospitalDbContext db,
    ChargeService charges,
    BillService bills,
    INotificationService notifications,
    TimeProvider time) : IDomainEventHandler<VisitCancelled>
{
    /// <summary>Charges Billing itself creates for an encounter (the registration fee is handled separately).</summary>
    private static readonly HashSet<string> OwnSources =
        [ChargeService.ConsultationSource, ChargeService.OrderSource, ChargeService.AdhocSource, ChargeService.ManualSource, ChargeService.PackageSource];

    public async Task HandleAsync(VisitCancelled cancelled, CancellationToken cancellationToken)
    {
        var account = db.Set<IpAccount>().Local.FirstOrDefault(a => a.VisitId == cancelled.VisitId)
            ?? await db.Set<IpAccount>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(a => a.VisitId == cancelled.VisitId, cancellationToken);
        if (account is not null)
        {
            await CancelAdmissionAsync(account, cancellationToken);
        }
        else
        {
            await CancelVisitAsync(cancelled.VisitId, cancellationToken);
        }
    }

    private async Task CancelVisitAsync(Guid visitId, CancellationToken ct)
    {
        var drafts = await DraftsAsync(b => b.VisitId == visitId && b.AdmissionId == null, ct);
        var visitCharges = await db.Set<Charge>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(c => c.VisitId == visitId && c.AdmissionId == null)
            .ToListAsync(ct);
        visitCharges.AddRange(db.Set<Charge>().Local.Where(c => c.VisitId == visitId && c.AdmissionId == null && !visitCharges.Contains(c)));

        foreach (var charge in visitCharges.Where(c => c.Status == ChargeStatus.Unbilled))
        {
            if (charge.SourceModule == ChargeService.RegistrationSource)
            {
                if ((await charges.ReleaseFromDraftAsync(charge, ct)).IsSuccess)
                {
                    charge.VisitId = null;
                }
            }
            else if (OwnSources.Contains(charge.SourceModule))
            {
                await charges.CancelChargeAsync(charge, "Visit cancelled", ct);
            }
        }

        // A draft left without services is discarded (it would otherwise block a new draft for nothing).
        foreach (var draft in drafts.Where(d => !visitCharges.Any(c => c.BillId == d.Id)))
        {
            await bills.StageDiscardAsync(draft, "Visit cancelled", ct);
        }
    }

    private async Task CancelAdmissionAsync(IpAccount account, CancellationToken ct)
    {
        if (account.Status is IpAccountStatus.Closed or IpAccountStatus.Cancelled)
        {
            return;
        }

        var reason = $"Admission {account.AdmissionNumber} cancelled";

        // Drafts first: their charges return to the pool, so Billing's own ones can then be cancelled.
        foreach (var draft in await DraftsAsync(b => b.AdmissionId == account.AdmissionId, ct))
        {
            await bills.StageDiscardAsync(draft, reason, ct);
        }

        var admissionCharges = await db.Set<Charge>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(c => c.AdmissionId == account.AdmissionId)
            .ToListAsync(ct);
        admissionCharges.AddRange(db.Set<Charge>().Local.Where(c => c.AdmissionId == account.AdmissionId && !admissionCharges.Contains(c)));
        foreach (var charge in admissionCharges.Where(c => c.Status == ChargeStatus.Unbilled && OwnSources.Contains(c.SourceModule)))
        {
            await charges.CancelChargeAsync(charge, reason, ct);
        }

        account.CancelAdmission(reason, time.GetUtcNow());

        // Whatever other modules charged, anything already billed and any advance held need a person.
        var remaining = admissionCharges.Where(c => c.Status != ChargeStatus.Cancelled).ToList();
        var billed = await db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AnyAsync(
            b => b.AdmissionId == account.AdmissionId && b.Status != BillStatus.Draft && b.Status != BillStatus.Cancelled && b.Status != BillStatus.Refunded, ct);
        if (remaining.Count == 0 && !billed && account.DepositBalance <= 0)
        {
            return;
        }

        var followUp = new List<string>();
        if (remaining.Count > 0)
        {
            followUp.Add($"{remaining.Count} service(s) worth ₹{Money.FormatInr(remaining.Sum(c => c.NetAmount))} remain charged");
        }

        if (billed)
        {
            followUp.Add("bills were already finalized");
        }

        if (account.DepositBalance > 0)
        {
            followUp.Add($"an advance of ₹{Money.FormatInr(account.DepositBalance)} is to be refunded");
        }

        await notifications.NotifyPermissionHoldersAsync(
            BillingPermissions.IpManage,
            account.FacilityId,
            "Admission cancelled — billing follow-up",
            $"Admission {account.AdmissionNumber} was cancelled: {string.Join("; ", followUp)}.",
            $"/billing/ip-accounts/{account.Id}",
            "billing",
            "warning",
            ct);
    }

    /// <summary>Draft bills matching <paramref name="filter"/> with their lines, including drafts staged in this unit of work.</summary>
    private async Task<List<Bill>> DraftsAsync(Expression<Func<Bill, bool>> filter, CancellationToken ct)
    {
        var drafts = await db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(b => b.Status == BillStatus.Draft)
            .Where(filter)
            .Include(b => b.Lines)
            .ToListAsync(ct);
        var matches = filter.Compile();
        drafts.AddRange(db.Set<Bill>().Local.Where(b => b.Status == BillStatus.Draft && matches(b) && !drafts.Contains(b)));
        return drafts;
    }
}
