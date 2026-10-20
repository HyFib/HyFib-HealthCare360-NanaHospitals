using System.Globalization;
using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Released reports on the patient's record (IMG-02, IMG-03) over the real API and PostgreSQL: a result on the timeline
/// names its report and the department that made it, which is what the chart needs to open the report, and an imaging
/// study's images, through the department's own endpoints. Those keep their permissions and the restricted-record
/// guard, so the chart reaches nothing the user could not open in the department.
/// </summary>
public sealed class ChartResultTests(HospitalServer server)
{
    private const string Impression = "No active lung lesion.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_released_imaging_report_on_the_timeline_leads_to_its_study_report_and_images()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await RegisterPatientAsync(isVip: false);

        // The doctor orders a chest X-ray from the chart; radiology gets the study in the same step.
        var xray = (await doctor.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?category=Radiology&search=RAD-XR-CHEST"))
            .Single(s => s.Code == "RAD-XR-CHEST");
        var order = await doctor.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, OrderPriority.Routine, "Cough for two weeks",
            [new OrderLineRequest(xray.Id, Notes: null)], OrderSetId: null, ConfirmDuplicates: false));
        var studyId = (await doctor.GetAsync<PagedResult<ImagingStudyListItem>>($"/api/v1/radiology/studies?patientId={patient.Id}"))
            .Items.ShouldHaveSingleItem().Id;

        // PACS links the images and the radiologist signs and releases the report.
        var report = await ReleaseImagingReportAsync(studyId, linkWithPacs: true);

        var entry = (await doctor.GetAsync<PagedResult<TimelineEntry>>($"/api/v1/emr/patients/{patient.Id}/timeline?types=Result"))
            .Items.ShouldHaveSingleItem();
        entry.Id.ShouldBe(report.Id, "A result is named by its report.");
        entry.ReportKind.ShouldBe(DiagnosticKind.Radiology);
        entry.Title.ShouldBe(xray.Name);
        entry.Status.ShouldBe("Awaiting review");
        (await doctor.GetAsync<OrderDto>($"/api/v1/clinical/orders/{order.Id}")).Items.ShouldHaveSingleItem()
            .ResultReference.ShouldBe(report.Id.ToString(), "The order line names the report too, for the chart's orders.");

        // What the chart then asks the radiology endpoints for, with the ids the entry carries.
        var reference = await doctor.GetAsync<ReportStudyRef>($"/api/v1/radiology/reports/{entry.Id}");
        reference.StudyId.ShouldBe(studyId);
        reference.Status.ShouldBe(DiagnosticReportStatus.Released);
        (await doctor.GetAsync<ImagingStudyDetail>($"/api/v1/radiology/studies/{studyId}")).PacsLinkedAt
            .ShouldNotBeNull("The chart offers the images of a study linked with PACS.");
        (await doctor.GetAsync<RadiologyReportDto>($"/api/v1/radiology/studies/{studyId}/report")).Impression.ShouldBe(Impression);

        // A nurse reads the result on the chart, but the images stay with those who may see them.
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        (await nurse.GetAsync<PagedResult<TimelineEntry>>($"/api/v1/emr/patients/{patient.Id}/timeline?types=Result"))
            .Items.ShouldHaveSingleItem().ReportKind.ShouldBe(DiagnosticKind.Radiology);
        (await nurse.GetAsync<RadiologyReportDto>($"/api/v1/radiology/studies/{studyId}/report")).Impression.ShouldBe(Impression);
        await ShouldAnswerAsync(nurse, HttpStatusCode.Forbidden, $"/api/v1/radiology/studies/{studyId}/images");
        await ShouldAnswerAsync(nurse, HttpStatusCode.Forbidden, $"/api/v1/radiology/studies/{studyId}/viewer");
    }

    [Fact]
    public async Task A_released_cardiology_report_on_the_timeline_leads_to_its_study()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var facilityId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();
        var patient = await RegisterPatientAsync(isVip: false);
        var (study, report) = await ReleaseEcgReportAsync(facilityId, patient.Id);

        var entry = (await doctor.GetAsync<PagedResult<TimelineEntry>>($"/api/v1/emr/patients/{patient.Id}/timeline?types=Result"))
            .Items.ShouldHaveSingleItem();
        entry.Id.ShouldBe(report.Id);
        entry.ReportKind.ShouldBe(DiagnosticKind.Cardiology);
        entry.Title.ShouldBe("ECG (12-lead)");

        var reference = await doctor.GetAsync<ReportStudyRef>($"/api/v1/cardiology/reports/{entry.Id}");
        reference.StudyId.ShouldBe(study.Id);
        var opened = await doctor.GetAsync<CardiologyStudyDetail>($"/api/v1/cardiology/studies/{reference.StudyId}");
        opened.Report.ShouldNotBeNull().Conclusion.ShouldBe("Normal ECG.");

        // Entries that are not results name no report.
        (await doctor.GetAsync<PagedResult<TimelineEntry>>($"/api/v1/emr/patients/{patient.Id}/timeline")).Items
            .Where(e => e.Type != TimelineEntryType.Result).ShouldAllBe(e => e.ReportKind == null);
    }

    [Fact]
    public async Task A_restricted_patients_results_stay_behind_the_guard_on_the_way_to_the_report()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var facilityId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();
        var patient = await RegisterPatientAsync(isVip: true);
        var studyId = await AddChestXrayAsync(facilityId, patient.Id);
        await ReleaseImagingReportAsync(studyId, linkWithPacs: true);
        var timeline = $"/api/v1/emr/patients/{patient.Id}/timeline?types=Result";

        await ShouldAnswerAsync(doctor, HttpStatusCode.Forbidden, timeline, "patient.restricted");
        await ShouldAnswerAsync(doctor, HttpStatusCode.Forbidden, $"/api/v1/radiology/studies/{studyId}", "patient.restricted");
        await ShouldAnswerAsync(doctor, HttpStatusCode.Forbidden, $"/api/v1/radiology/studies/{studyId}/report", "patient.restricted");
        await ShouldAnswerAsync(doctor, HttpStatusCode.Forbidden, $"/api/v1/radiology/studies/{studyId}/images", "patient.restricted");
        await ShouldAnswerAsync(doctor, HttpStatusCode.Forbidden, $"/api/v1/radiology/studies/{studyId}/viewer", "patient.restricted");
        await ShouldAnswerAsync(doctor, HttpStatusCode.Forbidden, $"/api/v1/radiology/studies/{studyId}/report/document", "patient.restricted");

        // The doctor's audited emergency access opens the chart, and the report with it.
        await doctor.PostAsync($"/api/v1/patients/{patient.Id}/break-glass", new BreakGlassRequest("Collapsed in casualty, needs the chest X-ray report"));
        (await doctor.GetAsync<PagedResult<TimelineEntry>>(timeline)).Items.ShouldHaveSingleItem().ReportKind.ShouldBe(DiagnosticKind.Radiology);
        (await doctor.GetAsync<RadiologyReportDto>($"/api/v1/radiology/studies/{studyId}/report")).Impression.ShouldBe(Impression);
    }

    private async Task<PatientCreated> RegisterPatientAsync(bool isVip)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: "Shobha", MiddleName: null, LastName: "Chart" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1965, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: isVip, Notes: null,
            ConfirmedNotDuplicate: false));
    }

    /// <summary>A chest X-ray ordered an hour ago, for a patient the doctor may not order for (a restricted record).</summary>
    private async Task<Guid> AddChestXrayAsync(Guid facilityId, Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var study = new ImagingStudy
        {
            FacilityId = facilityId,
            PatientId = patientId,
            AccessionNumber = "NHRDC" + Digits(7),
            ServiceCode = "RAD-XR-CHEST",
            StudyName = "X-ray chest PA",
            Modality = Modality.XR,
            BodyPart = "Chest",
            Priority = OrderPriority.Routine,
            OrderedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Status = ImagingStudyStatus.Ordered,
            StudyInstanceUid = DicomConventions.NewUid(),
        };
        db.Add(study);
        await db.SaveChangesAsync(Ct);
        return study.Id;
    }

    /// <summary>
    /// What the radiology screens do with a study, through its domain rules: PACS links the images (as the linking job
    /// does), and the radiologist reports, signs and releases. Releasing announces the report to the EHR.
    /// </summary>
    private async Task<RadiologyReport> ReleaseImagingReportAsync(Guid studyId, bool linkWithPacs)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = DateTimeOffset.UtcNow;
        var study = await db.Set<ImagingStudy>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).SingleAsync(s => s.Id == studyId, Ct);
        if (linkWithPacs)
        {
            study.LinkPacs(study.StudyInstanceUid, seriesCount: 2, instanceCount: 3, now).IsSuccess.ShouldBeTrue();
        }

        var report = new RadiologyReport { FacilityId = study.FacilityId, ImagingStudyId = study.Id, PatientId = study.PatientId };
        report.Save(new RadiologyReportContent(null, "PA view", "Cough for two weeks", null, "Both lung fields are clear. Heart size is normal.",
            Impression, IsCriticalFinding: false, CriticalFindingNote: null)).IsSuccess.ShouldBeTrue();
        report.Verify(null, now).IsSuccess.ShouldBeTrue();
        study.MarkVerified(now);
        report.Release(study, null, now).IsSuccess.ShouldBeTrue();
        db.Add(report);
        await db.SaveChangesAsync(Ct);
        return report;
    }

    /// <summary>An ECG recorded, reported, signed and released by the cardiologist.</summary>
    private async Task<(CardiologyStudy Study, CardiologyReport Report)> ReleaseEcgReportAsync(Guid facilityId, Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = DateTimeOffset.UtcNow;
        var study = new CardiologyStudy
        {
            FacilityId = facilityId,
            PatientId = patientId,
            AccessionNumber = "NHCDC" + Digits(7),
            ServiceCode = "CARD-ECG",
            StudyName = "ECG (12-lead)",
            StudyType = CardiologyStudyType.Ecg,
            Priority = OrderPriority.Routine,
            OrderedAt = now.AddHours(-1),
            Status = CardiologyStudyStatus.Acquired,
            StartedAt = now.AddMinutes(-30),
            AcquiredAt = now.AddMinutes(-25),
        };
        var report = new CardiologyReport { FacilityId = facilityId, CardiologyStudyId = study.Id, PatientId = patientId, StudyType = study.StudyType };
        report.Save(new CardiologyReportContent(null, null, null, null, null, "Normal ECG.", null, IsCriticalFinding: false, CriticalFindingNote: null))
            .IsSuccess.ShouldBeTrue();
        report.Verify(null, now).IsSuccess.ShouldBeTrue();
        study.MarkVerified(now);
        report.Release(study, null, now).IsSuccess.ShouldBeTrue();
        db.AddRange(study, report);
        await db.SaveChangesAsync(Ct);
        return (study, report);
    }

    private static async Task ShouldAnswerAsync(ApiClient client, HttpStatusCode status, string path, string? code = null)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, path);
        response.StatusCode.ShouldBe(status, path);
        if (code is not null)
        {
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(code, Case.Sensitive, path);
        }
    }

    private static string Digits(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(_ => Random.Shared.Next(10).ToString(CultureInfo.InvariantCulture)));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
