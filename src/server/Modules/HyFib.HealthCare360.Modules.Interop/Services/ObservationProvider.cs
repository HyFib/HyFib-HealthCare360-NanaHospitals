using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Platform.Identity;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// Observations: Clinical vital signs, released Diagnostics laboratory results and VirtualCare home-monitoring readings.
/// </summary>
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

            var rows = await ReleasedResultsAsync(r => r.Id == resultId, ct);
            return rows.Count == 0 ? ResourceLookup.Missing : ResourceLookup.Found(LabObservations.Map(rows[0]), state!.PatientId);
        }

        if (FhirIds.TryParse(id, FhirIds.DeviceReading, out var readingId))
        {
            // Readings are kept as they arrived: none is ever deleted.
            var reading = await db.Set<RpmReading>().AsNoTracking().Where(r => r.Id == readingId)
                .Select(r => new { r.PatientId, r.EnrollmentId, r.Type, r.MeasuredAt }).FirstOrDefaultAsync(ct);
            if (reading is null)
            {
                return ResourceLookup.Missing;
            }

            // A diastolic reading taken with a systolic one is served in that one's blood pressure panel, under its id.
            var together = reading.Type is RpmReadingType.SystolicBp or RpmReadingType.DiastolicBp
                ? db.Set<RpmReading>().Where(r => r.EnrollmentId == reading.EnrollmentId && r.MeasuredAt == reading.MeasuredAt
                                                  && (r.Type == RpmReadingType.SystolicBp || r.Type == RpmReadingType.DiastolicBp))
                : db.Set<RpmReading>().Where(r => r.Id == readingId);
            var rows = await DeviceReadingRowsAsync(together.AsNoTracking(), ct);
            var observation = DeviceReadingObservations.Map(rows).FirstOrDefault(o => o.Id == id);
            return observation is null ? ResourceLookup.Missing : ResourceLookup.Found(observation, reading.PatientId);
        }

        return ResourceLookup.Missing;
    }

    public async Task<SearchCandidates> ListForPatientAsync(Guid patientId, SearchRequest request, PatientAccess access, CancellationToken ct)
    {
        var readings = await DeviceReadingsAsync(patientId, request, access, ct);
        if (readings.Refusal is not null)
        {
            return readings;
        }

        var vitals = await Vitals(v => v.PatientId == patientId).ToListAsync(ct);
        var results = await ReleasedResultsAsync(r => r.PatientId == patientId, ct);
        return new SearchCandidates([.. vitals.SelectMany(VitalSignObservations.Map), .. results.Select(LabObservations.Map), .. readings.Resources], readings.Withheld);
    }

    /// <summary>
    /// The patient's home-monitoring readings that the search could match (by type and by measurement time) and that the
    /// consents admitting the reader cover, as observations, with the number of observations the consents leave out. Both
    /// are decided in SQL on the readings' patient and time index, so years of readings are never loaded to be filtered;
    /// a search that would still load more than <see cref="DeviceReadingObservations.MaxPerSearch"/> is refused. Only
    /// covered readings count toward that limit, so a refusal says nothing of the others.
    /// </summary>
    private async Task<SearchCandidates> DeviceReadingsAsync(Guid patientId, SearchRequest request, PatientAccess access, CancellationToken ct)
    {
        if (Readings(patientId, request, access) is not { } readings)
        {
            return new SearchCandidates([]);
        }

        var (covered, uncovered) = readings;
        if (await covered.Take(DeviceReadingObservations.MaxPerSearch + 1).CountAsync(ct) > DeviceReadingObservations.MaxPerSearch)
        {
            return SearchCandidates.Refused(FhirOutcomes.TooCostly(ResourceType));
        }

        // Counted as observations: a systolic and a diastolic reading taken together are one.
        var withheld = await uncovered
            .Select(r => new
            {
                r.EnrollmentId,
                r.MeasuredAt,
                Panel = r.Type == RpmReadingType.DiastolicBp ? RpmReadingType.SystolicBp : r.Type,
            })
            .Distinct().CountAsync(ct);
        var rows = await DeviceReadingRowsAsync(covered, ct);
        return new SearchCandidates([.. DeviceReadingObservations.Map(rows)], withheld);
    }

    /// <summary>
    /// The patient's readings the search could match, split into those the consents admitting the reader cover and those
    /// they do not; none when the search can match no reading of any type.
    /// </summary>
    internal (IQueryable<RpmReading> Covered, IQueryable<RpmReading> Uncovered)? Readings(Guid patientId, SearchRequest request, PatientAccess access)
    {
        var types = DeviceReadingObservations.TypesMatching(request);
        if (types.Count == 0)
        {
            return null;
        }

        var (from, before) = DeviceReadingObservations.MeasuredWithin(request);
        var searched = db.Set<RpmReading>().AsNoTracking().Where(r => r.PatientId == patientId && types.Contains(r.Type)
            && (from == null || r.MeasuredAt >= from) && (before == null || r.MeasuredAt < before));
        var (covered, uncovered) = Within(DeviceReadingObservations.CoveredSpans(access.Scopes ?? [], types));
        return (searched.Where(covered), searched.Where(uncovered));
    }

    /// <summary>A reading in any of the spans, and a reading in none of them, as conditions a query runs.</summary>
    internal static (Expression<Func<RpmReading, bool>> In, Expression<Func<RpmReading, bool>> Outside) Within(IReadOnlyList<ReadingSpan> spans)
    {
        var reading = Expression.Parameter(typeof(RpmReading), "r");
        Expression any = Expression.Constant(false);
        foreach (var span in spans)
        {
            var types = span.Types;
            var from = span.From;
            var before = span.Before;
            Expression<Func<RpmReading, bool>> inSpan = r => types.Contains(r.Type)
                && (from == null || r.MeasuredAt >= from) && (before == null || r.MeasuredAt < before);
            any = Expression.OrElse(any, new ReadingParameter(inSpan.Parameters[0], reading).Visit(inSpan.Body));
        }

        return (Expression.Lambda<Func<RpmReading, bool>>(any, reading), Expression.Lambda<Func<RpmReading, bool>>(Expression.Not(any), reading));
    }

    /// <summary>Home-monitoring readings as rows to map; the device and who entered a reading come from <see cref="DeviceReadingRowsAsync"/>.</summary>
    internal static IQueryable<DeviceReadingRow> DeviceReadings(IQueryable<RpmReading> readings) =>
        readings.Select(r => new DeviceReadingRow
        {
            Id = r.Id,
            PatientId = r.PatientId,
            EnrollmentId = r.EnrollmentId,
            Type = r.Type,
            Value = r.Value,
            TextValue = r.TextValue,
            MeasuredAt = r.MeasuredAt,
            ReceivedAt = r.ReceivedAt,
            Severity = r.Severity,
            Source = r.Source,
            DeviceId = r.DeviceId,
            RecordedByUserId = r.Source == ReadingSource.Manual ? r.RecordedByUserId : null,
        });

    /// <summary>
    /// The readings with the device that sent each, named even after it was retired or moved to another facility, and the
    /// name of whoever entered a reading by hand. Those are read apart: a query that ignores a filter ignores it for every
    /// table it reads, so ignoring the facility filter for the device in the readings' query would read other facilities'
    /// readings too.
    /// </summary>
    internal async Task<List<DeviceReadingRow>> DeviceReadingRowsAsync(IQueryable<RpmReading> readings, CancellationToken ct)
    {
        var rows = await DeviceReadings(readings).ToListAsync(ct);
        Guid[] deviceIds = [.. rows.Select(r => r.DeviceId).OfType<Guid>().Distinct()];
        Guid[] userIds = [.. rows.Select(r => r.RecordedByUserId).OfType<Guid>().Distinct()];
        var devices = deviceIds.Length == 0
            ? []
            : await db.Set<RpmDevice>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter, HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(d => deviceIds.Contains(d.Id))
                .Select(d => new DeviceName(d.Id, d.Type, d.Manufacturer, d.Model, d.SerialNumber))
                .ToDictionaryAsync(d => d.Id, ct);
        var users = userIds.Length == 0
            ? []
            : await db.Set<User>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => (string?)u.DisplayName, ct);
        return
        [
            .. rows.Select(r => r.DeviceId is { } id && devices.TryGetValue(id, out var device)
                ? r with
                {
                    DeviceType = device.Type,
                    DeviceManufacturer = device.Manufacturer,
                    DeviceModel = device.Model,
                    DeviceSerial = device.SerialNumber,
                    RecordedByUserName = Name(users, r.RecordedByUserId),
                }
                : r with { RecordedByUserName = Name(users, r.RecordedByUserId) }),
        ];
    }

    private static string? Name(Dictionary<Guid, string?> users, Guid? userId) => userId is { } id ? users.GetValueOrDefault(id) : null;

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
    /// Verified results of released tests only, with the accession's visit and order time and the sample collection time;
    /// the analyte's LOINC code is added by <see cref="ReleasedResultsAsync"/>.
    /// </summary>
    internal IQueryable<LabResultRow> ReleasedResults(Expression<Func<LabResult, bool>> filter) =>
        from r in db.Set<LabResult>().AsNoTracking().Where(filter)
        join i in db.Set<LabOrderItem>() on r.LabOrderItemId equals i.Id
        join o in db.Set<LabOrder>() on r.LabOrderId equals o.Id
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
            LabTestId = r.LabTestId,
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
            ReferralLabId = i.ReferralLabId,
            OrderedAt = o.OrderedAt,
            CollectedAt = s.CollectedAt,
            VerifiedAt = r.VerifiedAt,
            ReleasedAt = i.ReleasedAt,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
        };

    /// <summary>
    /// The released results with the analyte's LOINC code and name from the test master, a test since retired from it
    /// included. The test master is read apart: a query that ignores a filter ignores it for every table it reads, so
    /// ignoring the soft-delete filter for the test master in the results' query would read removed results too.
    /// </summary>
    internal async Task<List<LabResultRow>> ReleasedResultsAsync(Expression<Func<LabResult, bool>> filter, CancellationToken ct)
    {
        var rows = await ReleasedResults(filter).ToListAsync(ct);
        Guid[] testIds = [.. rows.Select(r => r.LabTestId).Distinct()];
        if (testIds.Length == 0)
        {
            return rows;
        }

        var tests = await db.Set<LabTest>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(t => testIds.Contains(t.Id)).Select(t => new { t.Id, t.Loinc, t.LoincName }).ToDictionaryAsync(t => t.Id, ct);
        return [.. rows.Select(r => tests.TryGetValue(r.LabTestId, out var test) ? r with { Loinc = test.Loinc, LoincName = test.LoincName } : r)];
    }

    /// <summary>A device as a reading names it.</summary>
    private sealed record DeviceName(Guid Id, RpmDeviceType Type, string? Manufacturer, string? Model, string SerialNumber);

    /// <summary>Puts one reading parameter in place of another, so conditions written apart make one condition.</summary>
    private sealed class ReadingParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
