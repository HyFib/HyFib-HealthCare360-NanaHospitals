using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// The medicines the ward gives a patient that no signed e-prescription carries (<see cref="IWardMedicines"/>), for the
/// prescribing safety check: active chart lines written on the ward or issued by the pharmacy without a prescription line,
/// not ended, of a stay still in its bed, in any facility (the patient takes them all). An e-prescription's own lines are
/// counted from the prescription.
/// </summary>
internal sealed class WardMedicines(HospitalDbContext db, TimeProvider time) : IWardMedicines
{
    public async Task<IReadOnlyList<WardMedicine>> ActiveAsync(Guid patientId, CancellationToken cancellationToken) =>
        await Active(patientId, time.GetUtcNow()).ToListAsync(cancellationToken);

    internal IQueryable<WardMedicine> Active(Guid patientId, DateTimeOffset now) =>
        from o in db.Set<MedicationOrder>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
        join a in db.Set<Admission>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on o.AdmissionId equals a.Id
        where o.PatientId == patientId && o.Status == MedicationOrderStatus.Active && o.Source != MedicationOrderSource.Prescription
              && (o.EndAt == null || o.EndAt > now)
              && (a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated)
        orderby o.StartAt
        select new WardMedicine(o.Id, o.DrugItemId, o.DrugName, o.GenericName, o.Strength, o.Route, o.Dose, o.Frequency);
}
