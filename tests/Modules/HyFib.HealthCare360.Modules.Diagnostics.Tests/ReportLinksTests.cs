using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests;

/// <summary>
/// Links around released reports: the absolute QR link of a printed report, and report id → study id for the doctor's
/// worklist (the department screens route by study).
/// </summary>
public sealed class ReportLinksTests
{
    private const string Code = "ABCDEFGHJKLMNPQRSTUV";

    [Theory]
    [InlineData(DiagnosticKind.Laboratory, "https://care.nanahospitals.in/portal/verify/lab/" + Code)]
    [InlineData(DiagnosticKind.Radiology, "https://care.nanahospitals.in/portal/verify/radiology/" + Code)]
    [InlineData(DiagnosticKind.Cardiology, "https://care.nanahospitals.in/portal/verify/cardiology/" + Code)]
    public void The_qr_link_is_the_portal_verification_page_of_the_report_kind(DiagnosticKind kind, string expected) =>
        ReportVerificationLinks.Url("https://care.nanahospitals.in", kind, Code).ShouldBe(expected);

    [Fact]
    public void The_portal_page_uses_the_same_kind_names_as_the_verification_api()
    {
        LabReportBuilder.VerificationPathPrefix.ShouldBe($"/api/v1/{ReportVerificationLinks.Segment(DiagnosticKind.Laboratory)}/reports/verify/");
        RadiologyReportService.VerificationPathPrefix.ShouldBe($"/api/v1/{ReportVerificationLinks.Segment(DiagnosticKind.Radiology)}/reports/verify/");
        CardiologyService.VerificationPathPrefix.ShouldBe($"/api/v1/{ReportVerificationLinks.Segment(DiagnosticKind.Cardiology)}/reports/verify/");
    }

    [Fact]
    public void There_is_no_qr_link_without_a_public_address_or_before_release()
    {
        ReportVerificationLinks.Url(null, DiagnosticKind.Laboratory, Code).ShouldBeNull();
        ReportVerificationLinks.Url("https://care.nanahospitals.in", DiagnosticKind.Radiology, null).ShouldBeNull();
    }

    [Fact]
    public void A_radiology_report_id_resolves_to_its_study_and_status()
    {
        var study = new ImagingStudy { Id = Guid.CreateVersion7() };
        var report = new RadiologyReport { ImagingStudyId = study.Id, Status = DiagnosticReportStatus.Released };
        var other = new RadiologyReport { ImagingStudyId = Guid.CreateVersion7(), Status = DiagnosticReportStatus.Draft };

        var found = ReportStudyLookup.Radiology(new[] { other, report }.AsQueryable(), new[] { study }.AsQueryable(), report.Id).SingleOrDefault();

        found.ShouldBe(new ReportStudyRef(study.Id, report.Id, DiagnosticReportStatus.Released));
    }

    [Fact]
    public void A_cardiology_report_id_resolves_to_its_study_and_status()
    {
        var study = new CardiologyStudy { Id = Guid.CreateVersion7() };
        var report = new CardiologyReport { CardiologyStudyId = study.Id, Status = DiagnosticReportStatus.Verified };

        var found = ReportStudyLookup.Cardiology(new[] { report }.AsQueryable(), new[] { study }.AsQueryable(), report.Id).SingleOrDefault();

        found.ShouldBe(new ReportStudyRef(study.Id, report.Id, DiagnosticReportStatus.Verified));
    }

    [Fact]
    public void Unknown_reports_and_reports_of_studies_that_are_not_visible_do_not_resolve()
    {
        var study = new ImagingStudy { Id = Guid.CreateVersion7() };
        var report = new RadiologyReport { ImagingStudyId = study.Id };

        // The study set stands for what the facility and soft-delete filters let through.
        ReportStudyLookup.Radiology(new[] { report }.AsQueryable(), Array.Empty<ImagingStudy>().AsQueryable(), report.Id).ShouldBeEmpty();
        ReportStudyLookup.Radiology(new[] { report }.AsQueryable(), new[] { study }.AsQueryable(), Guid.CreateVersion7()).ShouldBeEmpty();
    }

    [Fact]
    public void The_lookups_apply_the_facility_and_soft_delete_filters_of_report_and_study()
    {
        using var db = ModelOnlyContext();

        foreach (var sql in new[]
                 {
                     ReportStudyLookup.Radiology(db.Set<RadiologyReport>(), db.Set<ImagingStudy>(), Guid.CreateVersion7()).ToQueryString(),
                     ReportStudyLookup.Cardiology(db.Set<CardiologyReport>(), db.Set<CardiologyStudy>(), Guid.CreateVersion7()).ToQueryString(),
                 })
        {
            CountOf(sql, "facility_id").ShouldBeGreaterThanOrEqualTo(2);
            CountOf(sql, "is_deleted").ShouldBeGreaterThanOrEqualTo(2);
        }
    }

    [Fact]
    public void Report_lookups_use_the_study_read_permissions()
    {
        RadiologyEndpoints.StudyReaders.ShouldContain(DiagnosticsPermissions.RadiologyRead);
        CardiologyEndpoints.StudyReaders.ShouldContain(DiagnosticsPermissions.CardiologyRead);
    }

    private static int CountOf(string text, string value) => (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    /// <summary>
    /// Same module set as <c>DiagnosticsModelTests</c> (EF caches one model per context type), signed in to a facility
    /// so the facility filter appears in the SQL.
    /// </summary>
    private static HospitalDbContext ModelOnlyContext()
    {
        var options = new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only")
            .UseSnakeCaseNamingConvention()
            .Options;
        var user = Substitute.For<ICurrentUser>();
        user.FacilityId.Returns(Guid.CreateVersion7());
        return new HospitalDbContext(options, new ModuleCatalog([new PatientsModule(), new DiagnosticsModule()]), user);
    }
}
