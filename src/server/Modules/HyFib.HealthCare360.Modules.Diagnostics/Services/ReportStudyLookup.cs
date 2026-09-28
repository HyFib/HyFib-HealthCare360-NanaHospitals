using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Report id → study id for radiology and cardiology. The report is joined to its study so both entities' query filters
/// apply: a report resolves only when its study is visible in the signed-in facility and not deleted.
/// </summary>
internal static class ReportStudyLookup
{
    public static IQueryable<ReportStudyRef> Radiology(IQueryable<RadiologyReport> reports, IQueryable<ImagingStudy> studies, Guid reportId) =>
        from r in reports
        join s in studies on r.ImagingStudyId equals s.Id
        where r.Id == reportId
        select new ReportStudyRef(s.Id, r.Id, r.Status);

    public static IQueryable<ReportStudyRef> Cardiology(IQueryable<CardiologyReport> reports, IQueryable<CardiologyStudy> studies, Guid reportId) =>
        from r in reports
        join s in studies on r.CardiologyStudyId equals s.Id
        where r.Id == reportId
        select new ReportStudyRef(s.Id, r.Id, r.Status);
}
