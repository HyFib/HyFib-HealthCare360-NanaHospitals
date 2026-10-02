using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// Read models for the EMR banner and summary. Patient-level clinical facts are shown from every facility of the
/// hospital group (the EHR is longitudinal), so facility filters are bypassed deliberately here.
/// </summary>
public sealed class PatientRecordQueries(HospitalDbContext db, TimeProvider time)
{
    public async Task<PatientClinicalSummary?> SummaryAsync(Guid patientId, CancellationToken ct)
    {
        var today = time.Today();
        var patients = await PatientQueries.SummariesAsync(db, [patientId], today, ct);
        if (!patients.TryGetValue(patientId, out var patient))
        {
            return null;
        }

        var allergies = await db.Set<Allergy>().AsNoTracking().Where(a => a.PatientId == patientId).ToListAsync(ct);
        var flags = await FlagsOfAsync(patientId, allergies.Where(a => a.IsActive), today, ct);
        var problems = await db.Set<Diagnosis>().AsNoTracking()
            .Where(d => d.PatientId == patientId && d.Type != DiagnosisType.Differential && (d.Status == DiagnosisStatus.Active || d.Status == DiagnosisStatus.Chronic))
            .OrderByDescending(d => d.RecordedAt)
            .Take(20)
            .Select(d => new ProblemSummary(d.Id, d.IcdCode, d.Title, d.Type, d.Status, d.OnsetDate))
            .ToListAsync(ct);
        var vitals = await db.Set<VitalSigns>().AsNoTracking()
            .Where(v => v.PatientId == patientId && !v.IsEnteredInError)
            .OrderByDescending(v => v.RecordedAt)
            .FirstOrDefaultAsync(ct);
        var pendingResults = await db.Set<ResultReview>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .CountAsync(r => r.PatientId == patientId && r.ReviewedAt == null, ct);

        return new PatientClinicalSummary(
            patient,
            allergies.Any(a => a.Status != AllergyStatus.EnteredInError),
            flags.AllergyStatus,
            flags.Allergies,
            flags.Alerts,
            problems,
            await CurrentMedicationsAsync(patientId, ct),
            vitals is null ? null : Snapshot(vitals, patient.DateOfBirth, today),
            await RecentEncountersAsync(patientId, 5, ct),
            pendingResults);
    }

    /// <summary>The banner's safety flags (EHR-03). The caller has checked that the patient exists (the access guard does).</summary>
    public async Task<PatientFlags> FlagsAsync(Guid patientId, CancellationToken ct)
    {
        var active = await db.Set<Allergy>().AsNoTracking().Where(a => a.PatientId == patientId && a.Status == AllergyStatus.Active).ToListAsync(ct);
        return await FlagsOfAsync(patientId, active, time.Today(), ct);
    }

    /// <summary>
    /// One rule for the summary and the flags: active allergies most severe first, "no known allergies" only when a clinician
    /// recorded it and no active allergy contradicts it, and the alerts in force today, most severe first.
    /// </summary>
    private async Task<PatientFlags> FlagsOfAsync(Guid patientId, IEnumerable<Allergy> activeAllergies, DateOnly today, CancellationToken ct)
    {
        var active = activeAllergies.ToList();
        var noKnownAllergies = await db.Set<NoKnownAllergiesStatement>().AsNoTracking().AnyAsync(s => s.PatientId == patientId, ct);
        var alerts = await db.Set<PatientAlert>().AsNoTracking().Where(a => a.PatientId == patientId && a.IsActive).ToListAsync(ct);

        return new PatientFlags(
            NoKnownAllergiesStatement.StatusOf(active.Count > 0, noKnownAllergies),
            active.OrderByDescending(a => a.Severity)
                .Select(a => new AllergySummary(a.Id, a.Allergen, a.Type, a.Severity, a.Reaction))
                .ToList(),
            alerts.Where(a => a.IsCurrent(today))
                .OrderByDescending(a => a.Severity)
                .Select(a => new AlertSummary(a.Id, a.Type, a.Title, a.Detail, a.Severity, a.ExpiresOn))
                .ToList());
    }

    /// <summary>Medicines the patient should currently be taking: signed prescriptions whose course has not ended.</summary>
    public async Task<IReadOnlyList<CurrentMedication>> CurrentMedicationsAsync(Guid patientId, CancellationToken ct)
    {
        var today = time.Today();
        var since = time.GetUtcNow().AddDays(-366);
        var prescriptions = await db.Set<Prescription>().AsNoTracking()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Include(p => p.Items)
            .Where(p => p.PatientId == patientId && p.Status == PrescriptionStatus.Signed && p.SignedAt >= since)
            .OrderByDescending(p => p.SignedAt)
            .ToListAsync(ct);
        var names = await PrintQueries.PractitionerNamesAsync(db, prescriptions.Select(p => (Guid?)p.PractitionerId), ct);

        return prescriptions
            .SelectMany(p =>
            {
                var start = ClinicalClock.LocalDate(p.SignedAt!.Value);
                return p.Items
                    .Select(i => (Item: i, EndsOn: start.AddDays(Math.Max(i.DurationDays, 1) - 1)))
                    .Where(x => x.EndsOn >= today)
                    .Select(x => new CurrentMedication(p.Id, p.PrescriptionNumber, x.Item.Id, x.Item.DrugName, x.Item.GenericName, x.Item.Strength,
                        x.Item.Dose, x.Item.FrequencyText, x.Item.DurationDays, start, x.EndsOn, p.PractitionerId, names.GetValueOrDefault(p.PractitionerId),
                        x.Item.IsControlledDrug));
            })
            .ToList();
    }

    public async Task<IReadOnlyList<RecentEncounter>> RecentEncountersAsync(Guid patientId, int count, CancellationToken ct)
    {
        var encounters = await db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(e => e.PatientId == patientId && !e.IsDeleted)
            .OrderByDescending(e => e.StartedAt)
            .Take(count)
            .Select(e => new { e.Id, e.Type, e.Status, e.StartedAt, e.PractitionerId, e.DepartmentId, e.ChiefComplaint })
            .ToListAsync(ct);
        var ids = encounters.Select(e => (Guid?)e.Id).ToList();
        var diagnoses = await db.Set<Diagnosis>().AsNoTracking()
            .Where(d => ids.Contains(d.EncounterId))
            .OrderByDescending(d => d.IsPrimary)
            .Select(d => new { d.EncounterId, Label = d.IcdCode == null ? d.Title : d.IcdCode + " " + d.Title })
            .ToListAsync(ct);
        var practitioners = await PrintQueries.PractitionerNamesAsync(db, encounters.Select(e => (Guid?)e.PractitionerId), ct);
        var departments = await PrintQueries.DepartmentNamesAsync(db, encounters.Select(e => e.DepartmentId), ct);

        return encounters.Select(e => new RecentEncounter(e.Id, e.Type, e.Status, e.StartedAt, practitioners.GetValueOrDefault(e.PractitionerId),
                e.DepartmentId is { } d ? departments.GetValueOrDefault(d) : null, e.ChiefComplaint,
                diagnoses.Where(x => x.EncounterId == e.Id).Select(x => x.Label).ToList()))
            .ToList();
    }

    public static VitalsSnapshot Snapshot(VitalSigns v, DateOnly? dateOfBirth, DateOnly today) => new(
        v.Id, v.RecordedAt, v.TemperatureCelsius, v.Pulse, v.RespiratoryRate, v.SystolicBp, v.DiastolicBp, v.SpO2, v.WeightKg, v.Bmi, v.PainScore,
        v.BloodGlucose, v.Gcs, VitalSignsAssessor.Assess(v.ToReading(), dateOfBirth, today));
}
