using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FellowOakDicom;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// An echo's DICOM files sent to the PACS (INT-PACS, STOW-RS) through the whole server, with the PACS simulator: what is
/// sent and what stays in HC360, the study UID the files go under, the viewer link, who may send, and the audit.
/// </summary>
public sealed class CardiologyPacsTests(HospitalServer server)
{
    private const string PacsKey = "integrations.pacs";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_technician_sends_an_echos_dicom_files_to_the_pacs_and_a_doctor_opens_them_in_its_viewer()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var technician = await server.SignInAsync(HospitalServer.Users.CardiologyTechnician);
        var facilityId = (await technician.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();
        var patient = await RegisterPatientAsync();
        var study = await AddEchoAsync(facilityId, patient.Id);
        var loops = new[]
        {
            await UploadAsync(technician, patient.Id, EchoLoop(), "loop-1.dcm"),
            await UploadAsync(technician, patient.Id, EchoLoop(), "loop-2.dcm"),
        };
        var report = await UploadAsync(technician, patient.Id, "%PDF-1.4\n1 0 obj<<>>endobj\n%%EOF"u8.ToArray(), "echo-report.pdf");
        await technician.PostAsync<CardiologyStudyDetail>($"/api/v1/cardiology/studies/{study.Id}/files", new AttachDeviceFilesRequest([.. loops, report]));

        // With the PACS integration switched off, nothing is sent and the technician is told why.
        await SetPacsAsync(admin, new JsonObject { ["enabled"] = false });
        await RefusedAsync(technician, $"/api/v1/cardiology/studies/{study.Id}/pacs", "pacs.unavailable");

        await using var pacs = await PacsSimulatorAsync(admin);
        var sent = await technician.PostAsync<CardiologyStudyDetail>($"/api/v1/cardiology/studies/{study.Id}/pacs");

        foreach (var loop in loops)
        {
            var file = sent.Files.Single(f => f.Id == loop);
            file.Pacs.ShouldNotBeNull().Status.ShouldBe(CardiologyPacsStatus.Stored);
            file.Pacs.Message.ShouldBeNull();
        }

        sent.Files.Single(f => f.Id == report).Pacs.ShouldBeNull("a PACS stores DICOM only; the PDF stays in HC360");

        // Every DICOM file is in the PACS, so there is nothing more to send.
        await RefusedAsync(technician, $"/api/v1/cardiology/studies/{study.Id}/pacs", "cardiology.in_pacs");

        // The files went under a study UID made from the study's id, and the viewer opens that study.
        var studyUid = DicomConventions.UidFrom(study.Id);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<CardiologyStudy>().AsNoTracking().SingleAsync(s => s.Id == study.Id, Ct)).StudyInstanceUid.ShouldBe(studyUid);
            var objects = await db.Set<CardiologyPacsObject>().AsNoTracking().Where(o => o.CardiologyStudyId == study.Id).ToListAsync(Ct);
            objects.Select(o => o.FileId).ShouldBe(loops, ignoreOrder: true);
            objects.ShouldAllBe(o => o.SopInstanceUid != null && o.FacilityId == facilityId);
        }

        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var link = await doctor.GetAsync<ViewerLink>($"/api/v1/cardiology/studies/{study.Id}/viewer");
        link.StudyInstanceUid.ShouldBe(studyUid);
        link.Url.ShouldBe($"https://pacs.example.org/viewer?study={studyUid}&acc={study.AccessionNumber}");

        // Doctors open cardiology studies, but sending them is the technician's.
        using (var forbidden = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/cardiology/studies/{study.Id}/pacs"))
        {
            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // The patient's data left HC360 once, and their images were opened once.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var events = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
                .Where(e => e.PatientId == patient.Id)
                .Select(e => new { e.Action, e.Summary })
                .ToListAsync(Ct);
            events.Where(e => e.Action == "cardiology.pacs.sent").ShouldHaveSingleItem().Summary
                .ShouldBe($"Sent 2D Echo ({study.AccessionNumber}) to the PACS: 2 of 2 DICOM files stored");
            events.Count(e => e.Summary == $"Opened images of {study.AccessionNumber}").ShouldBe(1);
        }
    }

    [Fact]
    public async Task Files_stored_in_the_simulator_are_not_taken_as_in_the_pacs_set_up_after_it()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var technician = await server.SignInAsync(HospitalServer.Users.CardiologyTechnician);
        var facilityId = (await technician.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();
        var patient = await RegisterPatientAsync();
        var study = await AddEchoAsync(facilityId, patient.Id);
        var loop = await UploadAsync(technician, patient.Id, EchoLoop(), "loop.dcm");
        await technician.PostAsync<CardiologyStudyDetail>($"/api/v1/cardiology/studies/{study.Id}/files", new AttachDeviceFilesRequest([loop]));

        await using var pacs = await PacsSimulatorAsync(admin);
        (await technician.PostAsync<CardiologyStudyDetail>($"/api/v1/cardiology/studies/{study.Id}/pacs"))
            .Files.Single(f => f.Id == loop).Pacs.ShouldNotBeNull().Status.ShouldBe(CardiologyPacsStatus.Stored);

        // Training is over and the hospital's PACS is set up (here at an address where nothing answers).
        await SetPacsAsync(admin, new JsonObject
        {
            ["enabled"] = true,
            ["provider"] = "dicomweb",
            ["qidoUrl"] = "http://127.0.0.1:9/dicom-web",
            ["viewerUrlTemplate"] = "https://pacs.example.org/viewer?study={studyUid}",
        });

        // The simulator's "stored" says nothing about the new PACS: the file shows as not sent, the viewer is not offered,
        // and sending tries the new PACS instead of answering that every file is already there.
        (await technician.GetAsync<CardiologyStudyDetail>($"/api/v1/cardiology/studies/{study.Id}")).Files.Single(f => f.Id == loop).Pacs.ShouldBeNull();
        using (var viewer = await technician.SendRawAsync(HttpMethod.Get, $"/api/v1/cardiology/studies/{study.Id}/viewer"))
        {
            viewer.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await viewer.Content.ReadAsStringAsync(Ct)).ShouldContain("cardiology.not_in_pacs");
        }

        var resent = await technician.PostAsync<CardiologyStudyDetail>($"/api/v1/cardiology/studies/{study.Id}/pacs");
        var outcome = resent.Files.Single(f => f.Id == loop).Pacs.ShouldNotBeNull();
        outcome.Status.ShouldBe(CardiologyPacsStatus.Failed);
        outcome.Message.ShouldNotBeNullOrWhiteSpace();

        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<CardiologyPacsObject>().AsNoTracking()
            .SingleAsync(o => o.CardiologyStudyId == study.Id, Ct)).Destination.ShouldBe("dicomweb http://127.0.0.1:9/dicom-web");
    }

    [Fact]
    public async Task A_study_without_dicom_files_has_nothing_to_send_or_open()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        await using var pacs = await PacsSimulatorAsync(admin);
        var technician = await server.SignInAsync(HospitalServer.Users.CardiologyTechnician);
        var facilityId = (await technician.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();
        var patient = await RegisterPatientAsync();
        var study = await AddEchoAsync(facilityId, patient.Id);
        var tracing = await UploadAsync(technician, patient.Id, "%PDF-1.4\n%%EOF"u8.ToArray(), "ecg.pdf");
        await technician.PostAsync<CardiologyStudyDetail>($"/api/v1/cardiology/studies/{study.Id}/files", new AttachDeviceFilesRequest([tracing]));

        await RefusedAsync(technician, $"/api/v1/cardiology/studies/{study.Id}/pacs", "cardiology.no_dicom_files");
        using var viewer = await technician.SendRawAsync(HttpMethod.Get, $"/api/v1/cardiology/studies/{study.Id}/viewer");
        viewer.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await viewer.Content.ReadAsStringAsync(Ct)).ShouldContain("cardiology.not_in_pacs");
    }

    /// <summary>An ultrasound loop as an echo machine exports it, under the machine's own patient ID and study.</summary>
    private static byte[] EchoLoop()
    {
        var dataset = new DicomDataset
        {
            { DicomTag.SpecificCharacterSet, "ISO_IR 100" },
            { DicomTag.SOPClassUID, DicomUID.UltrasoundMultiFrameImageStorage },
            { DicomTag.SOPInstanceUID, DicomConventions.NewUid() },
            { DicomTag.PatientID, "ECHO-17" },
            { DicomTag.PatientName, "ANITA" },
            { DicomTag.StudyInstanceUID, DicomConventions.NewUid() },
            { DicomTag.SeriesInstanceUID, DicomConventions.NewUid() },
            { DicomTag.Modality, "US" },
        };
        using var stream = new MemoryStream();
        new DicomFile(dataset).Save(stream);
        return stream.ToArray();
    }

    private static async Task<Guid> UploadAsync(ApiClient client, Guid patientId, byte[] content, string name)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", name);
        form.Add(new StringContent("cardiology"), "category");
        form.Add(new StringContent(patientId.ToString()), "patientId");
        using var response = await client.Http.PostAsync("/api/v1/files", form, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<Created>(ApiClient.Json, Ct))!.Id;
    }

    private static async Task RefusedAsync(ApiClient client, string path, string code)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(code);
    }

    private async Task<PatientCreated> RegisterPatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        // A name and birth date of its own: another test's patient with the same ones would be taken as a duplicate.
        var surname = "Echo" + new string(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        var phone = "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture);
        var born = new DateOnly(1940, 1, 1).AddDays(Random.Shared.Next(0, 365 * 50));
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName("Kavitha"), MiddleName: null, LastName: surname, Gender: Gender.Female,
            DateOfBirth: born, AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
    }

    /// <summary>A 2D echo being recorded, as a clinician's order would create it.</summary>
    private async Task<CardiologyStudy> AddEchoAsync(Guid facilityId, Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var study = new CardiologyStudy
        {
            FacilityId = facilityId,
            PatientId = patientId,
            AccessionNumber = "NHCD" + Random.Shared.Next(10_000_000, 99_999_999).ToString(CultureInfo.InvariantCulture),
            ServiceCode = "CAR-ECHO",
            StudyName = "2D Echo",
            StudyType = CardiologyStudyType.Echo,
            Priority = OrderPriority.Routine,
            OrderedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Status = CardiologyStudyStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            DeviceName = "Echo machine 1",
        };
        db.Add(study);
        await db.SaveChangesAsync(Ct);
        return study;
    }

    private static Task SetPacsAsync(ApiClient admin, JsonNode value) =>
        admin.PutAsync($"/api/v1/admin/settings/{PacsKey}", new SaveSettingRequest(value, null, false, null));

    /// <summary>
    /// Turns on the PACS simulator with a viewer link by study UID and accession number for the rest of the test, then
    /// puts the setting back: the server is shared by every test of the run.
    /// </summary>
    private static async Task<IAsyncDisposable> PacsSimulatorAsync(ApiClient admin)
    {
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={PacsKey}"))
            .FirstOrDefault(s => s.Key == PacsKey && s.FacilityId is null)?.Value;
        await SetPacsAsync(admin, new JsonObject
        {
            ["enabled"] = true,
            ["provider"] = "simulator",
            ["viewerUrlTemplate"] = "https://pacs.example.org/viewer?study={studyUid}&acc={accession}",
        });
        return new Restore(() => SetPacsAsync(admin, before?.DeepClone() ?? new JsonObject { ["enabled"] = false }));
    }

    private sealed record Created(Guid Id);

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
