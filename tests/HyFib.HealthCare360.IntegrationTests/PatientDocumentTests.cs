using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Documents attached to the patient record (EHR-04) over the real API: an outside report keeps its type, date and
/// source; a restricted record's documents are added and removed only with the access that opens the record (EHR-07); a
/// patient who does not exist has no record to add to; a document cannot be dated after today in the hospital; and a
/// patient's files are only for staff whose work is about patients.
/// </summary>
public sealed class PatientDocumentTests(HospitalServer server)
{
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromMinutes(330);

    // The file store takes a file by what it is, and "%PDF-" is what a PDF starts with.
    private static readonly byte[] OutsideReport = "%PDF-1.7\n% MRI brain, outside report\n%%EOF\n"u8.ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IndiaOffset).DateTime);

    [Fact]
    public async Task An_outside_report_is_listed_with_its_type_date_and_source()
    {
        var patient = await RegisterPatientAsync(isVip: false);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var path = $"/api/v1/patients/{patient.Id}/documents";

        var fileId = await UploadAsync(frontOffice, patient.Id);
        var attached = await frontOffice.PostAsync<Created>(path, new AttachDocumentRequest(
            fileId, PatientDocumentType.ExternalReport, " MRI brain ", new DateOnly(2026, 8, 14), "  Apollo Diagnostics, Pune  ", "Brought by the patient's son"));

        var document = (await frontOffice.GetAsync<List<PatientDocumentDto>>(path)).ShouldHaveSingleItem();
        document.Id.ShouldBe(attached.Id);
        document.FileId.ShouldBe(fileId);
        document.Type.ShouldBe(PatientDocumentType.ExternalReport);
        document.Title.ShouldBe("MRI brain");
        document.DocumentDate.ShouldBe(new DateOnly(2026, 8, 14));
        document.Source.ShouldBe("Apollo Diagnostics, Pune");
        document.Notes.ShouldBe("Brought by the patient's son");
        document.FileName.ShouldBe("mri-report.pdf");
        document.ContentType.ShouldBe("application/pdf");
        document.SizeBytes.ShouldBe(OutsideReport.Length);

        using (var remove = await frontOffice.SendRawAsync(HttpMethod.Delete, $"/api/v1/patients/documents/{attached.Id}"))
        {
            remove.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await frontOffice.GetAsync<List<PatientDocumentDto>>(path)).ShouldBeEmpty();
        (await ChangesAsync(patient.Id, HospitalServer.Users.FrontOffice))
            .ShouldBe(["PatientDocument.create", "PatientDocument.delete"], "Who added and removed the document is in the audit trail.");
    }

    [Fact]
    public async Task Adding_to_a_restricted_record_needs_the_access_that_opens_it()
    {
        var patient = await RegisterPatientAsync(isVip: true);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var path = $"/api/v1/patients/{patient.Id}/documents";

        using (var attach = await nurse.SendRawAsync(HttpMethod.Post, path, OutsideReportFrom(await UploadAsync(nurse, patient.Id))))
        {
            attach.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await attach.Content.ReadAsStringAsync(Ct)).ShouldContain("patient.restricted");
        }

        // The hospital administrator may open restricted records, and attaches there.
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var byAdmin = await admin.PostAsync<Created>(path, OutsideReportFrom(await UploadAsync(admin, patient.Id)));

        // A nurse's audited emergency access opens the record's documents, to add to them too.
        await nurse.PostAsync($"/api/v1/patients/{patient.Id}/break-glass", new BreakGlassRequest("Unconscious in casualty, needs the outside MRI report"));
        var byNurse = await nurse.PostAsync<Created>(path, OutsideReportFrom(await UploadAsync(nurse, patient.Id)));

        (await admin.GetAsync<List<PatientDocumentDto>>(path)).Select(d => d.Id).ShouldBe([byNurse.Id, byAdmin.Id], ignoreOrder: true,
            "The refused document was not added.");
    }

    [Fact]
    public async Task Removing_from_a_restricted_record_needs_the_access_that_opens_it()
    {
        var patient = await RegisterPatientAsync(isVip: true);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var path = $"/api/v1/patients/{patient.Id}/documents";
        var attached = await admin.PostAsync<Created>(path, OutsideReportFrom(await UploadAsync(admin, patient.Id)));

        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        using (var remove = await nurse.SendRawAsync(HttpMethod.Delete, $"/api/v1/patients/documents/{attached.Id}"))
        {
            remove.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await remove.Content.ReadAsStringAsync(Ct)).ShouldContain("patient.restricted");
        }

        (await admin.GetAsync<List<PatientDocumentDto>>(path)).Select(d => d.Id).ShouldBe([attached.Id], "The refusal left the document in the record.");

        await nurse.PostAsync($"/api/v1/patients/{patient.Id}/break-glass", new BreakGlassRequest("Unconscious in casualty, report filed against the wrong patient"));
        using (var remove = await nurse.SendRawAsync(HttpMethod.Delete, $"/api/v1/patients/documents/{attached.Id}"))
        {
            remove.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await admin.GetAsync<List<PatientDocumentDto>>(path)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_restricted_records_files_open_only_with_the_access_that_opens_the_record()
    {
        var patient = await RegisterPatientAsync(isVip: true);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var fileId = await UploadAsync(admin, patient.Id);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);

        // Knowing a file's id is not enough: the file follows its record's rules, as the record's documents list does.
        foreach (var path in new[] { $"/api/v1/files/{fileId}", $"/api/v1/files/{fileId}/meta" })
        {
            using var refused = await nurse.SendRawAsync(HttpMethod.Get, path);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("patient.restricted");
        }

        using (var byAdmin = await admin.SendRawAsync(HttpMethod.Get, $"/api/v1/files/{fileId}"))
        {
            byAdmin.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await byAdmin.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(OutsideReport);
        }

        await nurse.PostAsync($"/api/v1/patients/{patient.Id}/break-glass", new BreakGlassRequest("Unconscious in casualty, needs the outside MRI report"));
        using (var afterBreakGlass = await nurse.SendRawAsync(HttpMethod.Get, $"/api/v1/files/{fileId}"))
        {
            afterBreakGlass.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Another patient's file, and a file of no patient, open as before.
        var unrestricted = await UploadAsync(admin, (await RegisterPatientAsync(isVip: false)).Id);
        var unowned = await UploadAsync(admin, patientId: null);
        (await nurse.GetAsync<StoredFileDto>($"/api/v1/files/{unrestricted}/meta")).Id.ShouldBe(unrestricted);
        (await nurse.GetAsync<StoredFileDto>($"/api/v1/files/{unowned}/meta")).PatientId.ShouldBeNull();
    }

    [Fact]
    public async Task A_patients_files_are_not_for_staff_whose_work_is_not_about_patients()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var patientsFile = await UploadAsync(admin, (await RegisterPatientAsync(isVip: false)).Id);
        var unowned = await UploadAsync(admin, patientId: null);

        // An MIS analyst may open stored documents, but not patients' records (DPDP s.8): not by a file's id either.
        var analyst = await server.SignInAsync(HospitalServer.Users.MisAnalyst);
        foreach (var path in new[] { $"/api/v1/files/{patientsFile}", $"/api/v1/files/{patientsFile}/meta" })
        {
            using var refused = await analyst.SendRawAsync(HttpMethod.Get, path);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("files.patient_record");
        }

        (await analyst.GetAsync<StoredFileDto>($"/api/v1/files/{unowned}/meta")).Id.ShouldBe(unowned);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        (await nurse.GetAsync<StoredFileDto>($"/api/v1/files/{patientsFile}/meta")).Id.ShouldBe(patientsFile);

        // A role made for patients' documents alone (attach and view them), without searching the patient register.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var role = await (await server.SignInAsync(HospitalServer.Users.Admin)).PostAsync<RoleDetail>("/api/v1/admin/roles", new SaveRoleRequest(
            "IT documents " + suffix, "Scans and files patients' documents", [PatientsPermissions.Documents, PlatformPermissions.FilesRead], Version: null));
        await server.CreateStaffAccountAsync("it-docs-" + suffix, role.Name);
        var scanner = await server.SignInAsync("it-docs-" + suffix);
        (await scanner.GetAsync<StoredFileDto>($"/api/v1/files/{patientsFile}/meta")).Id.ShouldBe(patientsFile);
    }

    [Fact]
    public async Task A_patient_who_does_not_exist_has_no_record_to_add_to()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var fileId = await UploadAsync(frontOffice, patientId: null);

        using var response = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/patients/{Guid.CreateVersion7()}/documents", OutsideReportFrom(fileId));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("patient.not_found");
    }

    [Fact]
    public async Task A_document_cannot_be_dated_after_today_in_the_hospital()
    {
        var patient = await RegisterPatientAsync(isVip: false);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var path = $"/api/v1/patients/{patient.Id}/documents";
        var fileId = await UploadAsync(frontOffice, patient.Id);

        using (var tomorrow = await frontOffice.SendRawAsync(HttpMethod.Post, path, OutsideReportFrom(fileId) with { DocumentDate = Today.AddDays(1) }))
        {
            tomorrow.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await tomorrow.Content.ReadAsStringAsync(Ct)).ShouldContain("documentDate");
        }

        // Today is the hospital's (IST): between midnight and 05:30 it is a day ahead of the date in UTC.
        await frontOffice.PostAsync<Created>(path, OutsideReportFrom(fileId) with { DocumentDate = Today });
        (await frontOffice.GetAsync<List<PatientDocumentDto>>(path)).ShouldHaveSingleItem().DocumentDate.ShouldBe(Today);
    }

    [Fact]
    public async Task A_title_or_source_with_control_characters_is_refused()
    {
        var patient = await RegisterPatientAsync(isVip: false);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var request = OutsideReportFrom(await UploadAsync(frontOffice, patient.Id)) with { Title = "MRI\u0007brain", Source = "Apollo\u001b[2J" };

        using var response = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/patients/{patient.Id}/documents", request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadAsStringAsync(Ct);
        problem.ShouldContain("\"title\"");
        problem.ShouldContain("\"source\"");
    }

    private static AttachDocumentRequest OutsideReportFrom(Guid fileId) =>
        new(fileId, PatientDocumentType.ExternalReport, "MRI brain", new DateOnly(2026, 8, 14), "Apollo Diagnostics, Pune", null);

    private async Task<PatientCreated> RegisterPatientAsync(bool isVip)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName("Lata"), MiddleName: null, LastName: "Documents" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1965, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: isVip, Notes: null,
            ConfirmedNotDuplicate: false));
    }

    /// <summary>Uploads the outside report as the staff app does before attaching it.</summary>
    private static async Task<Guid> UploadAsync(ApiClient client, Guid? patientId)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(OutsideReport);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "mri-report.pdf");
        form.Add(new StringContent("patient-document"), "category");
        if (patientId is { } id)
        {
            form.Add(new StringContent(id.ToString()), "patientId");
        }

        using var response = await client.Http.PostAsync("/api/v1/files", form, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<Created>(ApiClient.Json, Ct))!.Id;
    }

    /// <summary>The changes to this patient's documents the audit trail holds for <paramref name="userName"/>, oldest first.</summary>
    private async Task<List<string>> ChangesAsync(Guid patientId, string userName)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId && e.UserName == userName && e.Category == AuditCategories.Data && e.EntityType == nameof(PatientDocument))
            .OrderBy(e => e.Id)
            .Select(e => e.Action)
            .ToListAsync(Ct);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Created(Guid Id);
}
