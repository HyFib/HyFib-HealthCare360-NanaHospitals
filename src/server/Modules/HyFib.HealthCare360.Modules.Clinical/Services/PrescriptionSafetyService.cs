using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Loads the context for the pure <see cref="PrescriptionSafetyChecker"/>: active allergies, current medications from
/// every facility (the patient takes them all: signed prescriptions, and what the ward gives that no prescription carries,
/// <see cref="IWardMedicines"/>), and the configurable interaction and dose tables.
/// </summary>
public sealed class PrescriptionSafetyService(HospitalDbContext db, TimeProvider time, IEnumerable<IWardMedicines> wardMedicines)
{
    /// <summary>Longest course a prescription line may have (validator limit), used to bound the current-medication query.</summary>
    private const int MaxCourseDays = 365;

    /// <summary>Where a current medicine the ward gives without a prescription is said to be prescribed, in an alert.</summary>
    private const string WardChart = "the ward chart";

    public async Task<SafetyCheckResult> CheckAsync(Prescription prescription, CancellationToken ct)
    {
        var input = await BuildInputAsync(prescription.PatientId, prescription.Items, [prescription.Id, prescription.RefillOfPrescriptionId], ct);
        return PrescriptionSafetyChecker.Check(input);
    }

    public Task<SafetyCheckInput> BuildInputAsync(Guid patientId, IEnumerable<PrescriptionItem> items, IReadOnlyCollection<Guid?> excludePrescriptionIds, CancellationToken ct) =>
        BuildInputAsync(patientId, [.. items.OrderBy(i => i.Sequence).Select(ToSafetyDrug)], excludePrescriptionIds, ct);

    /// <summary>The checks' input for <paramref name="items"/>, the medicines being started, for the patient.</summary>
    public async Task<SafetyCheckInput> BuildInputAsync(Guid patientId, IReadOnlyList<SafetyDrug> items, IReadOnlyCollection<Guid?> excludePrescriptionIds, CancellationToken ct)
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
        return new SafetyCheckInput(items, current, allergies, interactions, limits);
    }

    /// <summary>
    /// The patient's current medicines: the lines of signed prescriptions whose course has not ended yet (signing day counts
    /// as day 1), and the medicines the ward gives that no signed prescription carries (<see cref="IWardMedicines"/>: ward
    /// orders, and the pharmacy's supplies without a prescription line).
    /// </summary>
    public async Task<IReadOnlyList<SafetyDrug>> CurrentMedicationsAsync(Guid patientId, IReadOnlyCollection<Guid?> excludePrescriptionIds, CancellationToken ct)
    {
        var today = time.Today();
        var since = time.GetUtcNow().AddDays(-(MaxCourseDays + 1));
        var prescriptions = await db.Set<Prescription>().AsNoTracking()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Include(p => p.Items)
            .Where(p => p.PatientId == patientId && p.Status == PrescriptionStatus.Signed && p.SignedAt >= since)
            .ToListAsync(ct);

        var current = prescriptions
            .Where(p => !excludePrescriptionIds.Contains(p.Id))
            .SelectMany(p => p.Items
                .Where(i => ClinicalClock.LocalDate(p.SignedAt!.Value).AddDays(Math.Max(i.DurationDays, 1) - 1) >= today)
                .Select(i => ToSafetyDrug(i) with { IsCurrentMedication = true, PrescriptionNumber = p.PrescriptionNumber }))
            .ToList();
        foreach (var ward in wardMedicines)
        {
            current.AddRange((await ward.ActiveAsync(patientId, ct)).Select(m => ToSafetyDrug(m, isCurrent: true)));
        }

        return current;
    }

    public static SafetyDrug ToSafetyDrug(PrescriptionItem i) => new(
        i.Id, i.DrugName, i.GenericName, i.Strength, i.Route, i.Dose, i.Frequency, i.CustomFrequency, i.DosesPerDay, i.IsControlledDrug,
        DosageForm: i.DosageForm, ControlledSchedule: i.ControlledSchedule);

    /// <summary>
    /// A medicine of the ward's chart as the checks see it, its frequency read as a prescription line's
    /// (<see cref="WardFrequency"/>); <paramref name="genericName"/> and <paramref name="dosageForm"/> are the drug master's
    /// when it has them. A current one is said to be on <see cref="WardChart"/>.
    /// </summary>
    public static SafetyDrug ToSafetyDrug(WardMedicine m, bool isCurrent = false, string? genericName = null, string? dosageForm = null)
    {
        var (frequency, custom) = WardFrequency.Parse(m.Frequency);
        return new SafetyDrug(m.Id, m.DrugName, string.IsNullOrWhiteSpace(genericName) ? m.GenericName : genericName.Trim(), m.Strength, m.Route, m.Dose,
            frequency, custom, null, IsControlledDrug: false, IsCurrentMedication: isCurrent, PrescriptionNumber: isCurrent ? WardChart : null,
            DosageForm: string.IsNullOrWhiteSpace(dosageForm) ? null : dosageForm.Trim());
    }
}
