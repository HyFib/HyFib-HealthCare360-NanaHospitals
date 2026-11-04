using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// The ward's blood on a discharge (<see cref="IDischargeHoldSource"/>): a unit being transfused is watched until it ends,
/// so the stay does not end while one runs. A unit issued but not started holds nothing: it goes back to the blood bank.
/// </summary>
internal sealed class TransfusionDischargeHolds(HospitalDbContext db) : IDischargeHoldSource
{
    public const string Code = "discharge.transfusion_running";

    public async Task<IReadOnlyList<DischargeHold>> HoldsAsync(Guid admissionId, CancellationToken cancellationToken)
    {
        var running = await (
                from t in db.Set<Transfusion>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                join r in db.Set<BloodRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on t.RequestId equals r.Id
                where r.AdmissionId == admissionId && t.Status == TransfusionStatus.InProgress
                orderby t.StartedAt
                select t.UnitNumber)
            .ToListAsync(cancellationToken);

        return
        [
            .. running.Select(unit => new DischargeHold(Code,
                $"Unit {unit} is being transfused: end it on the ward, completed or stopped, before the discharge.")),
        ];
    }
}
