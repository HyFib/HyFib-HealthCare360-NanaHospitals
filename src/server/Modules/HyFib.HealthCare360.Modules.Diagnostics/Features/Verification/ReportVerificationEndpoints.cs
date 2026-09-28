using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Verification;

/// <summary>
/// Public QR verification of printed reports (LIS-07): <c>GET /api/v1/{lab|radiology|cardiology}/reports/verify/{code}</c>.
/// Anyone holding a printed report (an insurer, another hospital, the patient) can confirm it is genuine and
/// unaltered. The answer shows the report number, facility, release date, patient initials and test names, and never
/// any result or identifier. The endpoints are anonymous and rate-limited; the codes carry 100 bits of entropy and
/// cannot be enumerated.
/// </summary>
internal static class ReportVerificationEndpoints
{
    private const string PublicRateLimit = RateLimitPolicies.Public;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/lab/reports/verify/{code}", VerifyLab).WithTags("Report verification").AllowAnonymous().RequireRateLimiting(PublicRateLimit).WithSensitivePath();
        app.MapGet("/api/v1/radiology/reports/verify/{code}", VerifyRadiology).WithTags("Report verification").AllowAnonymous().RequireRateLimiting(PublicRateLimit).WithSensitivePath();
        app.MapGet("/api/v1/cardiology/reports/verify/{code}", VerifyCardiology).WithTags("Report verification").AllowAnonymous().RequireRateLimiting(PublicRateLimit).WithSensitivePath();
    }

    private static async Task<IResult> VerifyLab(string code, HospitalDbContext db, CancellationToken ct)
    {
        if (!VerificationCode.IsWellFormed(code))
        {
            return NotGenuine();
        }

        var report = await db.Set<LabReport>().AsNoTracking()
            .Where(r => r.VerificationCode == code && (r.Status == LabReportStatus.Released || r.Status == LabReportStatus.Amended))
            .Select(r => new { r.LabOrderId, r.ReportNumber, r.FacilityId, r.PatientId, r.ReleasedAt, r.Revision })
            .FirstOrDefaultAsync(ct);
        if (report is null)
        {
            return NotGenuine();
        }

        var tests = await db.Set<LabOrderItem>().AsNoTracking()
            .Where(i => i.LabOrderId == report.LabOrderId && i.Status == LabOrderItemStatus.Released)
            .OrderBy(i => i.TestName)
            .Select(i => i.TestName)
            .ToListAsync(ct);
        return await AnswerAsync(db, DiagnosticKind.Laboratory, report.ReportNumber, report.FacilityId, report.PatientId, report.ReleasedAt, tests, report.Revision, ct);
    }

    private static async Task<IResult> VerifyRadiology(string code, HospitalDbContext db, CancellationToken ct)
    {
        if (!VerificationCode.IsWellFormed(code))
        {
            return NotGenuine();
        }

        var report = await (
                from r in db.Set<RadiologyReport>().AsNoTracking()
                join s in db.Set<ImagingStudy>() on r.ImagingStudyId equals s.Id
                where r.VerificationCode == code && r.Status == DiagnosticReportStatus.Released
                select new { s.AccessionNumber, s.StudyName, r.FacilityId, r.PatientId, r.ReleasedAt, Addenda = r.Addenda.Count })
            .FirstOrDefaultAsync(ct);
        return report is null
            ? NotGenuine()
            : await AnswerAsync(db, DiagnosticKind.Radiology, report.AccessionNumber, report.FacilityId, report.PatientId, report.ReleasedAt, [report.StudyName], report.Addenda, ct);
    }

    private static async Task<IResult> VerifyCardiology(string code, HospitalDbContext db, CancellationToken ct)
    {
        if (!VerificationCode.IsWellFormed(code))
        {
            return NotGenuine();
        }

        var report = await (
                from r in db.Set<CardiologyReport>().AsNoTracking()
                join s in db.Set<CardiologyStudy>() on r.CardiologyStudyId equals s.Id
                where r.VerificationCode == code && r.Status == DiagnosticReportStatus.Released
                select new { s.AccessionNumber, s.StudyName, r.FacilityId, r.PatientId, r.ReleasedAt })
            .FirstOrDefaultAsync(ct);
        return report is null
            ? NotGenuine()
            : await AnswerAsync(db, DiagnosticKind.Cardiology, report.AccessionNumber, report.FacilityId, report.PatientId, report.ReleasedAt, [report.StudyName], 0, ct);
    }

    private static async Task<IResult> AnswerAsync(
        HospitalDbContext db, DiagnosticKind kind, string reportNumber, Guid facilityId, Guid patientId, DateTimeOffset? releasedAt,
        IReadOnlyList<string> tests, int revision, CancellationToken ct)
    {
        var facility = await db.Set<Facility>().AsNoTracking().Where(f => f.Id == facilityId).Select(f => f.Name).FirstOrDefaultAsync(ct);
        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == patientId).Select(p => new { p.FirstName, p.LastName }).FirstOrDefaultAsync(ct);
        return TypedResults.Ok(new ReportAuthenticity(
            true, kind, reportNumber, facility ?? string.Empty, ReportAccess.Initials(patient?.FirstName, patient?.LastName), releasedAt, tests, revision));
    }

    /// <summary>The same answer for malformed, unknown and unreleased codes, so the endpoint reveals nothing.</summary>
    private static IResult NotGenuine() =>
        Error.NotFound("Report", null).ToProblem();
}
