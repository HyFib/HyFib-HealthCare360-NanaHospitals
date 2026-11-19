using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// The prescribing safety check for a medicine the ward puts on a patient's chart without an e-prescription
/// (<see cref="IMedicationSafetyCheck"/>): the same checks, tables and current medicines as an e-prescription's signing
/// (<see cref="PrescriptionSafetyService"/>), so a ward order finds what an e-prescription line of the same medicine would. A
/// medicine picked from the drug master is named, and its form read, as the master has them. The controlled-drug notice is
/// left out: it asks the prescriber to confirm the register entry an e-prescription makes, which a ward order does not.
/// </summary>
internal sealed class WardMedicationSafety(HospitalDbContext db, PrescriptionSafetyService safety) : IMedicationSafetyCheck
{
    public async Task<MedicationSafetyResult> CheckAsync(Guid patientId, WardMedicine medicine, CancellationToken cancellationToken)
    {
        var master = medicine.DrugItemId is { } itemId
            ? await db.Set<Item>().AsNoTracking().Where(i => i.Id == itemId).Select(i => new { i.GenericName, i.DosageForm }).FirstOrDefaultAsync(cancellationToken)
            : null;
        var drug = PrescriptionSafetyService.ToSafetyDrug(medicine, genericName: master?.GenericName, dosageForm: master?.DosageForm);
        var input = await safety.BuildInputAsync(patientId, [drug], [], cancellationToken);
        return new MedicationSafetyResult(
        [
            .. PrescriptionSafetyChecker.Check(input).Alerts
                .Where(a => a.Kind != SafetyAlertKind.ControlledDrug)
                .Select(a => new MedicationSafetyAlert(a.Key, a.Kind.ToString(), a.Severity.ToString(), a.IsBlocking, a.Message, a.Detail)),
        ]);
    }
}
