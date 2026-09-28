using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;

/// <summary>
/// Re-points every billing record of a merged (duplicate) patient to the surviving UHID, in all facilities and including
/// soft-deleted rows, inside the merge transaction — so dues, deposits, policies and claims follow the patient.
/// </summary>
internal sealed class PatientsMergedHandler(HospitalDbContext db) : IDomainEventHandler<PatientsMerged>
{
    public async Task HandleAsync(PatientsMerged merged, CancellationToken cancellationToken)
    {
        var from = merged.SourcePatientId;
        var to = merged.TargetPatientId;
        await RepointAsync<Charge>(c => c.PatientId == from, c => c.PatientId = to, cancellationToken);
        await RepointAsync<Bill>(b => b.PatientId == from, b => b.PatientId = to, cancellationToken);
        await RepointAsync<Receipt>(r => r.PatientId == from, r => r.PatientId = to, cancellationToken);
        await RepointAsync<PatientAdvance>(a => a.PatientId == from, a => a.PatientId = to, cancellationToken);
        await RepointAsync<Refund>(r => r.PatientId == from, r => r.PatientId = to, cancellationToken);
        await RepointAsync<CreditNote>(c => c.PatientId == from, c => c.PatientId = to, cancellationToken);
        await RepointAsync<DiscountApproval>(a => a.PatientId == from, a => a.PatientId = to, cancellationToken);
        await RepointAsync<IpAccount>(a => a.PatientId == from, a => a.PatientId = to, cancellationToken);
        await RepointAsync<PatientPolicy>(p => p.PatientId == from, p => p.PatientId = to, cancellationToken);
        await RepointAsync<PreAuthorization>(p => p.PatientId == from, p => p.PatientId = to, cancellationToken);
        await RepointAsync<InsuranceClaim>(c => c.PatientId == from, c => c.PatientId = to, cancellationToken);
        await RepointAsync<GatewayOrder>(o => o.PatientId == from, o => o.PatientId = to, cancellationToken);
        await RepointAsync<PosTransaction>(t => t.PatientId == from, t => t.PatientId = to, cancellationToken);
        await RepointAsync<DoctorFeeAccrual>(a => a.PatientId == from, a => a.PatientId = to, cancellationToken);
    }

    private async Task RepointAsync<T>(Expression<Func<T, bool>> ofSource, Action<T> repoint, CancellationToken ct)
        where T : class
    {
        foreach (var row in await db.Set<T>().IgnoreQueryFilters().Where(ofSource).ToListAsync(ct))
        {
            repoint(row);
        }
    }
}
