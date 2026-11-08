using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// The theatre's word on a discharge (<see cref="IDischargeHoldSource"/>): a patient received in pre-op, on the table or in
/// the recovery room (from the WHO Sign-out until discharged from recovery) is the theatre's, so the stay does not end,
/// however it is to end. A case still waiting for its day holds nothing: the discharge cancels it.
/// </summary>
internal sealed class TheatreDischargeHolds(HospitalDbContext db) : IDischargeHoldSource
{
    public const string Code = "discharge.in_theatre";

    public const string RecoveryCode = "discharge.in_recovery";

    public async Task<IReadOnlyList<DischargeHold>> HoldsAsync(Guid admissionId, CancellationToken cancellationToken)
    {
        var cases = await db.Set<SurgeryBooking>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(b => b.AdmissionId == admissionId
                        && (b.Status == SurgeryStatus.InPreOp || b.Status == SurgeryStatus.InProgress
                            || (b.Status == SurgeryStatus.Completed && b.SignOutCompletedAt != null && b.RecoveryDischargedAt == null)))
            .OrderBy(b => b.BookingNumber)
            .Select(b => new { b.BookingNumber, b.ProcedureName, b.Status, InRecovery = b.SignOutCompletedAt != null && b.RecoveryDischargedAt == null })
            .ToListAsync(cancellationToken);

        return
        [
            .. cases.Select(c => c.InRecovery
                ? new DischargeHold(RecoveryCode,
                    $"The patient is in recovery after surgery {c.BookingNumber} ({c.ProcedureName}): discharge them from recovery before the discharge.")
                : new DischargeHold(Code, c.Status == SurgeryStatus.InProgress
                    ? $"Surgery {c.BookingNumber} ({c.ProcedureName}) is in progress: the patient can be discharged once it is completed in the theatre."
                    : $"The patient is in pre-op for surgery {c.BookingNumber} ({c.ProcedureName}): complete, postpone or cancel it before the discharge.")),
        ];
    }
}
