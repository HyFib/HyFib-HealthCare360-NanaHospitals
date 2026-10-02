using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A study's images from PACS (IMG-02, INT-PACS) through the whole server, with the PACS simulator: the viewer link by
/// accession number before the study is linked, the worklist the modalities would read, linking on the schedule (by
/// accession number, or by study UID for an accession number longer than DICOM allows), then the series, thumbnails and
/// images a doctor sees, who may see them, and the PHI access audit.
/// </summary>
public sealed class RadiologyImagesTests(HospitalServer server)
{
    private const string PacsKey = "integrations.pacs";

    [Fact]
    public async Task A_doctor_sees_the_images_of_a_study_once_it_is_linked_with_pacs()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        await using var pacs = await PacsSimulatorAsync(admin);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var facilityId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();
        var patient = await RegisterPatientAsync();
        var digits = Random.Shared.Next(1_000_000, 9_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var fits = await AddStudyAsync(facilityId, patient.Id, $"NHRDT{digits}");
        var tooLong = await AddStudyAsync(facilityId, patient.Id, $"NHRDXYZT{digits}00");

        // Before linking, a viewer link built from the accession number already opens the study in PACS.
        var link = await doctor.GetAsync<ViewerLink>($"/api/v1/radiology/studies/{fits.Id}/viewer");
        link.Url.ShouldBe($"https://pacs.example.org/viewer?acc={fits.AccessionNumber}");
        using (var refused = await doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/radiology/studies/{tooLong.Id}/viewer"))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("pacs.accession_too_long");
        }

        using (var notLinked = await doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/radiology/studies/{fits.Id}/images"))
        {
            notLinked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        // The modalities' worklist (as the DICOM worklist server reads it) offers both, the long accession number unshortened.
        var worklist = await WorklistTodayAsync(facilityId);
        worklist.Single(i => i.StudyId == fits.Id).RequestedProcedureId.ShouldBe(fits.AccessionNumber);
        var longEntry = worklist.Single(i => i.StudyId == tooLong.Id);
        longEntry.AccessionNumber.ShouldBe(tooLong.AccessionNumber);
        longEntry.AccessionFitsDicom.ShouldBeFalse();
        longEntry.RequestedProcedureId.ShouldBe(DicomConventions.StudyCode(tooLong.Id));

        // The scheduled job links both: one by accession number, the other by the study UID the worklist sent.
        await server.Services.GetServices<IHostedService>().OfType<PacsLinkJob>().Single().RunOnceAsync(TestContext.Current.CancellationToken);
        var linked = await doctor.GetAsync<ImagingStudyDetail>($"/api/v1/radiology/studies/{fits.Id}");
        linked.PacsLinkedAt.ShouldNotBeNull();
        linked.Status.ShouldBe(ImagingStudyStatus.Acquired, "Images in PACS show the scan was done.");
        var linkedByUid = await doctor.GetAsync<ImagingStudyDetail>($"/api/v1/radiology/studies/{tooLong.Id}");
        linkedByUid.PacsLinkedAt.ShouldNotBeNull();
        linkedByUid.StudyInstanceUid.ShouldBe(tooLong.StudyInstanceUid);
        linkedByUid.AccessionFitsDicom.ShouldBeFalse();

        // The doctor opens the images: the series, their thumbnails, a series' images and one image.
        var images = await doctor.GetAsync<StudyImagesDto>($"/api/v1/radiology/studies/{fits.Id}/images");
        images.Series.Select(s => s.Description).ShouldBe(["Localiser", "Images", "Cine loop"]);
        foreach (var series in images.Series)
        {
            await ShouldBePngAsync(doctor, $"/api/v1/radiology/studies/{fits.Id}/images/series/{series.SeriesInstanceUid}/thumbnail");
        }

        var imageSeries = await doctor.GetAsync<ImageSeriesDetail>($"/api/v1/radiology/studies/{fits.Id}/images/series/{images.Series[1].SeriesInstanceUid}");
        imageSeries.Instances.Count.ShouldBe(8);
        var first = imageSeries.Instances[0];
        await ShouldBePngAsync(doctor,
            $"/api/v1/radiology/studies/{fits.Id}/images/series/{images.Series[1].SeriesInstanceUid}/instances/{first.SopInstanceUid}/rendered?size=512");

        // UIDs that are not this study's, as PACS lists it, reach nothing.
        var otherStudy = await doctor.GetAsync<StudyImagesDto>($"/api/v1/radiology/studies/{tooLong.Id}/images");
        await ShouldAnswerAsync(doctor, HttpStatusCode.NotFound,
            $"/api/v1/radiology/studies/{fits.Id}/images/series/{otherStudy.Series[0].SeriesInstanceUid}/thumbnail");
        await ShouldAnswerAsync(doctor, HttpStatusCode.NotFound,
            $"/api/v1/radiology/studies/{fits.Id}/images/series/{images.Series[1].SeriesInstanceUid}/instances/1.2.3.4/rendered");
        await ShouldAnswerAsync(doctor, HttpStatusCode.BadRequest,
            $"/api/v1/radiology/studies/{fits.Id}/images/series/{images.Series[1].SeriesInstanceUid}/instances/{first.SopInstanceUid}/rendered?size=100000");

        // Nurses read the report, but not the images.
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        await ShouldAnswerAsync(nurse, HttpStatusCode.Forbidden, $"/api/v1/radiology/studies/{fits.Id}/images");

        // One audit entry for opening the images; the thumbnails and the image that followed did not add more.
        await using var scope = server.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patient.Id && e.Summary == $"Viewed the images of {fits.AccessionNumber}")
            .CountAsync(TestContext.Current.CancellationToken);
        audit.ShouldBe(1);
    }

    private async Task<PatientCreated> RegisterPatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var surname = "Imaging" + new string(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        var phone = "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: "Anita", MiddleName: null, LastName: surname, Gender: Gender.Female,
            DateOfBirth: new DateOnly(1975, 6, 15), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
    }

    /// <summary>A chest X-ray ordered an hour ago, as a clinician's order would create it.</summary>
    private async Task<ImagingStudy> AddStudyAsync(Guid facilityId, Guid patientId, string accession)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var study = new ImagingStudy
        {
            FacilityId = facilityId,
            PatientId = patientId,
            AccessionNumber = accession,
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return study;
    }

    private async Task<IReadOnlyList<ModalityWorklistItem>> WorklistTodayAsync(Guid facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var today = DiagnosticsTime.Today(TimeProvider.System);
        return await scope.ServiceProvider.GetRequiredService<IModalityWorklistSource>()
            .FindAsync(new ModalityWorklistQuery(facilityId, today.AddDays(-1), today, ["CR"]), TestContext.Current.CancellationToken);
    }

    private static async Task ShouldBePngAsync(ApiClient client, string path)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, path);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("image/png");
        response.Headers.CacheControl?.NoStore.ShouldBe(true);
        var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        bytes.Take(4).ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
    }

    private static async Task ShouldAnswerAsync(ApiClient client, HttpStatusCode status, string path)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, path);
        response.StatusCode.ShouldBe(status, path);
    }

    /// <summary>
    /// Turns on the PACS simulator with a viewer link by accession number for the rest of the test, then puts the setting
    /// back: the server is shared by every test of the run.
    /// </summary>
    private static async Task<IAsyncDisposable> PacsSimulatorAsync(ApiClient admin)
    {
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={PacsKey}"))
            .FirstOrDefault(s => s.Key == PacsKey && s.FacilityId is null)?.Value;
        await admin.PutAsync($"/api/v1/admin/settings/{PacsKey}", new SaveSettingRequest(
            new JsonObject { ["enabled"] = true, ["provider"] = "simulator", ["viewerUrlTemplate"] = "https://pacs.example.org/viewer?acc={accession}" },
            null, false, null));
        return new Restore(() => admin.PutAsync($"/api/v1/admin/settings/{PacsKey}",
            new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null)));
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
