using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Loads the context for the pure <see cref="PrescriptionSafetyChecker"/>: active allergies, current medications from
/// every facility (the patient takes them all), and the configurable interaction and dose tables.
/// </summary>
public sealed class PrescriptionSafetyService(HospitalDbContext db, TimeProvider time)
{
    /// <summary>Longest course a prescription line may have (validator limit), used to bound the current-medication query.</summary>
    private const int MaxCourseDays = 365;

    public async Task<SafetyCheckResult> CheckAsync(Prescription prescription, CancellationToken ct)
    {
        var input = await BuildInputAsync(prescription.PatientId, prescription.Items, [prescription.Id, prescription.RefillOfPrescriptionId], ct);
        return PrescriptionSafetyChecker.Check(input);
    }

    public async Task<SafetyCheckInput> BuildInputAsync(Guid patientId, IEnumerable<PrescriptionItem> items, IReadOnlyCollection<Guid?> excludePrescriptionIds, CancellationToken ct)
    {
        var allergies = await db.Set<Allergy>().AsNoTracking()
            .Where(a => a.PatientId == patientId && a.Status == AllergyStatus.Active)
            .Select(a => new SafetyAllergy(a.Allergen, a.Type, a.Severity, a.Reaction))
            .ToListAsync(ct);
        var interactions = await db.Set<DrugInteraction>().AsNoTracking()
            .Where(i => i.IsActive)
            .Select(i => new SafetyInteractionRule(i.DrugA, i.DrugB, i.Severity, i.Effect, i.Management))
            .ToListAsync(ct);
        var limits = await db.Set<DrugDoseLimit>().AsNoTracking()
            .Where(l => l.IsActive)
            .Select(l => new SafetyDoseLimit(l.GenericName, l.MaxDailyDoseMg, l.Route))
            .ToListAsync(ct);
        var current = await CurrentMedicationsAsync(patientId, excludePrescriptionIds, ct);
        return new SafetyCheckInput(items.OrderBy(i => i.Sequence).Select(ToSafetyDrug).ToList(), current, allergies, interactions, limits);
    }

    /// <summary>Lines of signed prescriptions whose course has not ended yet (signing day counts as day 1).</summary>
    public async Task<IReadOnlyList<SafetyDrug>> CurrentMedicationsAsync(Guid patientId, IReadOnlyCollection<Guid?> excludePrescriptionIds, CancellationToken ct)
    {
        var today = time.Today();
        var since = time.GetUtcNow().AddDays(-(MaxCourseDays + 1));
        var prescriptions = await db.Set<Prescription>().AsNoTracking()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Include(p => p.Items)
            .Where(p => p.PatientId == patientId && p.Status == PrescriptionStatus.Signed && p.SignedAt >= since)
            .ToListAsync(ct);

        return prescriptions
            .Where(p => !excludePrescriptionIds.Contains(p.Id))
            .SelectMany(p => p.Items
                .Where(i => ClinicalClock.LocalDate(p.SignedAt!.Value).AddDays(Math.Max(i.DurationDays, 1) - 1) >= today)
                .Select(i => ToSafetyDrug(i) with { IsCurrentMedication = true, PrescriptionNumber = p.PrescriptionNumber }))
            .ToList();
    }

    public static SafetyDrug ToSafetyDrug(PrescriptionItem i) => new(
        i.Id, i.DrugName, i.GenericName, i.Strength, i.Route, i.Dose, i.Frequency, i.CustomFrequency, i.DosesPerDay, i.IsControlledDrug,
        DosageForm: i.DosageForm);
}
