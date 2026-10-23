using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>A released result of a laboratory accession (for the report's result list).</summary>
internal sealed record ReleasedResult(Guid Id, Guid LabOrderId, string TestName, DateTimeOffset ItemCreatedAt, int DisplayOrder);

/// <summary>The earliest sample collection of a laboratory accession.</summary>
internal sealed record AccessionCollection(Guid LabOrderId, DateTimeOffset? CollectedAt);

/// <summary>The study facts a cardiology report is shown with.</summary>
internal sealed record CardiologyStudyFacts(Guid Id, Guid? VisitId, string AccessionNumber, string ServiceCode, string StudyName, DateTimeOffset OrderedAt, DateTimeOffset? AcquiredAt);

/// <summary>DiagnosticReport: released laboratory, radiology and cardiology reports (Diagnostics module).</summary>
internal sealed class DiagnosticReportProvider(HospitalDbContext db) : IFhirResourceProvider
{
    public string ResourceType => FhirResourceTypes.DiagnosticReport;

    public async Task<ResourceLookup> FindAsync(string id, CancellationToken ct)
    {
        if (FhirIds.TryParse(id, FhirIds.Lab, out var labId))
        {
            var state = await db.Set<LabReport>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(r => r.Id == labId).Select(r => new RowState(r.PatientId, r.IsDeleted)).FirstOrDefaultAsync(ct);
            return ResourceLookup.Unavailable(state) ?? Found(await LabAsync(r => r.Id == labId, ct), state!);
        }

        if (FhirIds.TryParse(id, FhirIds.Radiology, out var radiologyId))
        {
            var state = await db.Set<RadiologyReport>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(r => r.Id == radiologyId).Select(r => new RowState(r.PatientId, r.IsDeleted)).FirstOrDefaultAsync(ct);
            return ResourceLookup.Unavailable(state) ?? Found(await RadiologyAsync(r => r.Id == radiologyId, ct), state!);
        }

        if (FhirIds.TryParse(id, FhirIds.Cardiology, out var cardiologyId))
        {
            var state = await db.Set<CardiologyReport>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(r => r.Id == cardiologyId).Select(r => new RowState(r.PatientId, r.IsDeleted)).FirstOrDefaultAsync(ct);
            return ResourceLookup.Unavailable(state) ?? Found(await CardiologyAsync(r => r.Id == cardiologyId, ct), state!);
        }

        return ResourceLookup.Missing;
    }

    public async Task<IReadOnlyList<R4.Resource>> ListForPatientAsync(Guid patientId, CancellationToken ct) =>
    [
        .. await LabAsync(r => r.PatientId == patientId, ct),
        .. await RadiologyAsync(r => r.PatientId == patientId, ct),
        .. await CardiologyAsync(r => r.PatientId == patientId, ct),
    ];

    /// <summary>Released (or amended) laboratory reports.</summary>
    internal IQueryable<LabReportRow> LabReports(Expression<Func<LabReport, bool>> filter) =>
        from r in db.Set<LabReport>().AsNoTracking().Where(filter)
        join o in db.Set<LabOrder>() on r.LabOrderId equals o.Id
        where r.Status == LabReportStatus.Released || r.Status == LabReportStatus.Amended
        select new LabReportRow
        {
            Id = r.Id,
            PatientId = r.PatientId,
            LabOrderId = r.LabOrderId,
            VisitId = o.VisitId,
            ReportNumber = r.ReportNumber,
            Status = r.Status,
            IsPartial = r.IsPartial,
            OrderedAt = o.OrderedAt,
            FirstReleasedAt = r.FirstReleasedAt,
            ReleasedAt = r.ReleasedAt,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
        };

    /// <summary>Verified results of released tests of the accessions.</summary>
    internal IQueryable<ReleasedResult> ReleasedResults(Guid[] labOrderIds) =>
        from result in db.Set<LabResult>().AsNoTracking()
        join item in db.Set<LabOrderItem>() on result.LabOrderItemId equals item.Id
        where labOrderIds.Contains(result.LabOrderId) && result.Status == LabResultStatus.Verified && item.Status == LabOrderItemStatus.Released
        select new ReleasedResult(result.Id, result.LabOrderId, item.TestName, item.CreatedAt, result.DisplayOrder);

    internal IQueryable<AccessionCollection> Collections(Guid[] labOrderIds) =>
        db.Set<LabSample>().AsNoTracking()
            .Where(s => labOrderIds.Contains(s.LabOrderId) && s.CollectedAt != null)
            .GroupBy(s => s.LabOrderId)
            .Select(g => new AccessionCollection(g.Key, g.Min(s => s.CollectedAt)));

    /// <summary>Released radiology reports with their imaging study.</summary>
    internal IQueryable<RadiologyReportRow> RadiologyReports(Expression<Func<RadiologyReport, bool>> filter) =>
        from r in db.Set<RadiologyReport>().AsNoTracking().Where(filter)
        join s in db.Set<ImagingStudy>() on r.ImagingStudyId equals s.Id
        where r.Status == DiagnosticReportStatus.Released
        select new RadiologyReportRow
        {
            Id = r.Id,
            PatientId = r.PatientId,
            ImagingStudyId = s.Id,
            VisitId = s.VisitId,
            AccessionNumber = s.AccessionNumber,
            ServiceCode = s.ServiceCode,
            StudyName = s.StudyName,
            Modality = s.Modality,
            BodyPart = s.BodyPart,
            OrderedAt = s.OrderedAt,
            StartedAt = s.StartedAt,
            AcquiredAt = s.AcquiredAt,
            StudyInstanceUid = s.PacsLinkedAt != null ? s.StudyInstanceUid : null,
            SeriesCount = s.SeriesCount,
            InstanceCount = s.InstanceCount,
            ReportedBy = r.ReportedBy,
            VerifiedBy = r.VerifiedBy,
            VerifiedAt = r.VerifiedAt,
            ReleasedAt = r.ReleasedAt,
            Technique = r.Technique,
            ClinicalHistory = r.ClinicalHistory,
            Comparison = r.Comparison,
            Findings = r.Findings,
            Impression = r.Impression,
            IsCriticalFinding = r.IsCriticalFinding,
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt,
        };

    internal IQueryable<RadiologyAddendum> Addenda(Guid[] reportIds) =>
        db.Set<RadiologyAddendum>().AsNoTracking().Where(a => reportIds.Contains(a.RadiologyReportId));

    /// <summary>The reports' addenda by report, in the order they were added, each with the user who added it.</summary>
    internal async Task<Dictionary<Guid, List<RadiologyAddendumRow>>> AddendumRowsAsync(Guid[] reportIds, CancellationToken ct) =>
        (await Addenda(reportIds).ToListAsync(ct))
            .GroupBy(a => a.RadiologyReportId)
            .ToDictionary(g => g.Key, g => g.OrderBy(a => a.AddedAt).ThenBy(a => a.Id).Select(a => new RadiologyAddendumRow(a.Text, a.Reason, a.AddedAt, a.AddedBy)).ToList());

    /// <summary>Released cardiology reports (the measurements are complex properties, so whole rows are loaded).</summary>
    internal IQueryable<CardiologyReport> CardiologyReports(Expression<Func<CardiologyReport, bool>> filter) =>
        db.Set<CardiologyReport>().AsNoTracking().Where(filter).Where(r => r.Status == DiagnosticReportStatus.Released);

    internal IQueryable<CardiologyStudyFacts> CardiologyStudies(Guid[] studyIds) =>
        db.Set<CardiologyStudy>().AsNoTracking()
            .Where(s => studyIds.Contains(s.Id))
            .Select(s => new CardiologyStudyFacts(s.Id, s.VisitId, s.AccessionNumber, s.ServiceCode, s.StudyName, s.OrderedAt, s.AcquiredAt));

    private static ResourceLookup Found(List<R4.Resource> resources, RowState state) =>
        resources.Count == 0 ? ResourceLookup.Missing : ResourceLookup.Found(resources[0], state.PatientId);

    private async Task<List<R4.Resource>> LabAsync(Expression<Func<LabReport, bool>> filter, CancellationToken ct) =>
        [.. (await CompleteAsync(await LabReports(filter).ToListAsync(ct), ct)).Select(r => (R4.Resource)DiagnosticReportMapper.FromLab(r))];

    /// <summary>
    /// The reports with what they are mapped from besides their own row: the released results (ids in report order,
    /// test names) and the first sample collection of each accession.
    /// </summary>
    internal async Task<List<LabReportRow>> CompleteAsync(List<LabReportRow> reports, CancellationToken ct)
    {
        if (reports.Count == 0)
        {
            return reports;
        }

        Guid[] orderIds = [.. reports.Select(r => r.LabOrderId).Distinct()];
        var results = await ReleasedResults(orderIds).ToListAsync(ct);
        var collections = await Collections(orderIds).ToListAsync(ct);
        var resultsByOrder = results.GroupBy(r => r.LabOrderId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.ItemCreatedAt).ThenBy(r => r.DisplayOrder).ThenBy(r => r.Id).ToList());
        var collectedByOrder = collections.ToDictionary(c => c.LabOrderId, c => c.CollectedAt);

        return
        [
            .. reports.Select(r =>
            {
                var released = resultsByOrder.GetValueOrDefault(r.LabOrderId) ?? [];
                return r with
                {
                    ResultIds = [.. released.Select(x => x.Id)],
                    TestNames = [.. released.Select(x => x.TestName).Distinct(StringComparer.Ordinal)],
                    CollectedAt = collectedByOrder.GetValueOrDefault(r.LabOrderId),
                };
            }),
        ];
    }

    private async Task<List<R4.Resource>> RadiologyAsync(Expression<Func<RadiologyReport, bool>> filter, CancellationToken ct)
    {
        var reports = await RadiologyReports(filter).ToListAsync(ct);
        if (reports.Count == 0)
        {
            return [];
        }

        var byReport = await AddendumRowsAsync([.. reports.Select(r => r.Id)], ct);
        return [.. reports.Select(r => (R4.Resource)DiagnosticReportMapper.FromRadiology(r with { Addenda = byReport.GetValueOrDefault(r.Id) ?? [] }))];
    }

    private async Task<List<R4.Resource>> CardiologyAsync(Expression<Func<CardiologyReport, bool>> filter, CancellationToken ct)
    {
        var reports = await CardiologyReports(filter).ToListAsync(ct);
        if (reports.Count == 0)
        {
            return [];
        }

        var studies = (await CardiologyStudies([.. reports.Select(r => r.CardiologyStudyId).Distinct()]).ToListAsync(ct)).ToDictionary(s => s.Id);
        return
        [
            .. reports.Where(r => studies.ContainsKey(r.CardiologyStudyId)).Select(r =>
            {
                var study = studies[r.CardiologyStudyId];
                return (R4.Resource)DiagnosticReportMapper.FromCardiology(new CardiologyReportRow
                {
                    Id = r.Id,
                    PatientId = r.PatientId,
                    VisitId = study.VisitId,
                    AccessionNumber = study.AccessionNumber,
                    ServiceCode = study.ServiceCode,
                    StudyName = study.StudyName,
                    StudyType = r.StudyType,
                    OrderedAt = study.OrderedAt,
                    AcquiredAt = study.AcquiredAt,
                    ReleasedAt = r.ReleasedAt,
                    Ecg = r.Ecg,
                    Echo = r.Echo,
                    Tmt = r.Tmt,
                    Holter = r.Holter,
                    Abpm = r.Abpm,
                    Conclusion = r.Conclusion,
                    Recommendations = r.Recommendations,
                    IsCriticalFinding = r.IsCriticalFinding,
                    CreatedAt = r.CreatedAt,
                    UpdatedAt = r.UpdatedAt,
                });
            }),
        ];
    }
}
