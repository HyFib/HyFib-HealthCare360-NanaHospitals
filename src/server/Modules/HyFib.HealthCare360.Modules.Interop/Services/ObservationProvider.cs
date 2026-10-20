using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>Observations: Clinical vital signs and released Diagnostics laboratory results.</summary>
internal sealed class ObservationProvider(HospitalDbContext db) : IFhirResourceProvider
{
    public string ResourceType => FhirResourceTypes.Observation;

    public async Task<ResourceLookup> FindAsync(string id, CancellationToken ct)
    {
        if (FhirIds.TryParse(id, FhirIds.Vitals, out var vitalsId, out var suffix))
        {
            if (VitalSignObservations.KindOf(suffix) is null)
            {
                return ResourceLookup.Missing;
            }

            var state = await db.Set<VitalSigns>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(v => v.Id == vitalsId).Select(v => new RowState(v.PatientId, v.IsDeleted)).FirstOrDefaultAsync(ct);
            if (ResourceLookup.Unavailable(state) is { } unavailable)
            {
                return unavailable;
            }

            var row = await Vitals(v => v.Id == vitalsId).FirstOrDefaultAsync(ct);
            var observation = row is null ? null : VitalSignObservations.Map(row).FirstOrDefault(o => o.Id == id);
            return observation is null ? ResourceLookup.Missing : ResourceLookup.Found(observation, state!.PatientId);
        }

        if (FhirIds.TryParse(id, FhirIds.Lab, out var resultId))
        {
            var state = await db.Set<LabResult>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(r => r.Id == resultId).Select(r => new RowState(r.PatientId, r.IsDeleted)).FirstOrDefaultAsync(ct);
            if (ResourceLookup.Unavailable(state) is { } unavailable)
            {
                return unavailable;
            }

            var rows = await ReleasedResults(r => r.Id == resultId).ToListAsync(ct);
            return rows.Count == 0 ? ResourceLookup.Missing : ResourceLookup.Found(LabObservations.Map(rows[0]), state!.PatientId);
        }

        return ResourceLookup.Missing;
    }

    public async Task<IReadOnlyList<R4.Resource>> ListForPatientAsync(Guid patientId, CancellationToken ct)
    {
        var vitals = await Vitals(v => v.PatientId == patientId).ToListAsync(ct);
        var results = await ReleasedResults(r => r.PatientId == patientId).ToListAsync(ct);
        return [.. vitals.SelectMany(VitalSignObservations.Map), .. results.Select(LabObservations.Map)];
    }

    internal IQueryable<VitalSignsRow> Vitals(Expression<Func<VitalSigns, bool>> filter) =>
        db.Set<VitalSigns>().AsNoTracking().Where(filter).Select(v => new VitalSignsRow
        {
            Id = v.Id,
            PatientId = v.PatientId,
            EncounterId = v.EncounterId,
            RecordedAt = v.RecordedAt,
            Temperature = v.Temperature,
            TemperatureUnit = v.TemperatureUnit,
            Pulse = v.Pulse,
            RespiratoryRate = v.RespiratoryRate,
            SystolicBp = v.SystolicBp,
            DiastolicBp = v.DiastolicBp,
            SpO2 = v.SpO2,
            OnSupplementalOxygen = v.OnSupplementalOxygen,
            HeightCm = v.HeightCm,
            WeightKg = v.WeightKg,
            Bmi = v.Bmi,
            PainScore = v.PainScore,
            BloodGlucose = v.BloodGlucose,
            GlucoseTiming = v.GlucoseTiming,
            Gcs = v.Gcs,
            IsEnteredInError = v.IsEnteredInError,
            CreatedAt = v.CreatedAt,
            UpdatedAt = v.UpdatedAt,
        });

    /// <summary>
    /// Verified results of released tests only, with the analyte's LOINC code (test master), the accession's visit and
    /// order time, and the sample collection time.
    /// </summary>
    internal IQueryable<LabResultRow> ReleasedResults(Expression<Func<LabResult, bool>> filter) =>
        from r in db.Set<LabResult>().AsNoTracking().Where(filter)
        join i in db.Set<LabOrderItem>() on r.LabOrderItemId equals i.Id
        join o in db.Set<LabOrder>() on r.LabOrderId equals o.Id
        join t in db.Set<LabTest>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]) on r.LabTestId equals t.Id into tests
        from t in tests.DefaultIfEmpty()
        join s in db.Set<LabSample>() on r.SampleId equals (Guid?)s.Id into samples
        from s in samples.DefaultIfEmpty()
        where r.Status == LabResultStatus.Verified && i.Status == LabOrderItemStatus.Released
        select new LabResultRow
        {
            Id = r.Id,
            PatientId = r.PatientId,
            LabOrderId = r.LabOrderId,
            VisitId = o.VisitId,
            TestCode = r.TestCode,
            TestName = r.TestName,
            Loinc = t.Loinc,
            LoincName = t.LoincName,
            ResultType = r.ResultType,
            ValueText = r.ValueText,
            NumericValue = r.NumericValue,
            Unit = r.Unit,
            Flag = r.Flag,
            ReferenceText = r.ReferenceText,
            Method = r.Method,
            Remarks = r.Remarks,
            Organism = r.Organism,
            ColonyCount = r.ColonyCount,
            OrderedAt = o.OrderedAt,
            CollectedAt = s.CollectedAt,
            VerifiedAt = r.VerifiedAt,
            ReleasedAt = i.ReleasedAt,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
        };
}
