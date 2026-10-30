using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A copy of the record issued as documents, so that it can be given in evidence under the Bharatiya Sakshya Adhiniyam
/// 2023, s.63: each document made once, when the copy is issued, and kept as it was handed over with its SHA-256; the
/// certificate of s.63(4) stating those hashes; a file brought back found by its hash; a copy issued whole or not at all;
/// and a file changed on the server never given out. The PDFs are made by <see cref="TestPdfRenderer"/>, so that the
/// hashes are checked alike with a browser on the server and without one.
/// </summary>
public sealed partial class RecordCopyTests(HospitalServer server)
{
    private const string Requests = "/api/v1/mrd/record-requests";
    private const string Er = "/api/v1/emergency";
    private const string ParacetamolCode = "DRG-PCM500";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_copy_for_a_court_is_made_once_kept_as_issued_and_certified_with_its_hashes()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var record = await PatientAsync(withLabReport: true);

        // A court asks for the record. MRD lists the patient's documents, newest first, and issues them all.
        var request = await RequestAsync(mrd, record.PatientId, "Judicial Magistrate, Saidapet", RecordRequestPurpose.Legal);
        var documents = await mrd.GetAsync<List<RecordDocumentEntry>>($"{Requests}/{request}/documents");
        documents.Select(d => d.Date).ShouldBeInOrder(SortDirection.Descending);
        var mlc = documents.Where(d => d.Kind == ClinicalKinds.Mlc).ShouldHaveSingleItem();
        var prescription = documents.Where(d => d.Kind == ClinicalKinds.Prescription).ShouldHaveSingleItem();
        var report = documents.Where(d => d.Kind == "diagnostics.lab-report").ShouldHaveSingleItem();
        documents.Count.ShouldBe(3);
        (mlc.Id, prescription.Id).ShouldBe((record.Mlc.Id, record.Prescription.Id));

        var issued = await IssueAsync(mrd, request, [Pick(mlc), Pick(prescription), Pick(report)]);
        issued.Status.ShouldBe(RecordRequestStatus.Issued);
        issued.DueBackOn.ShouldBeNull("a copy is the court's to keep");
        var files = issued.Files.ShouldNotBeNull();
        files.Select(f => (f.Sequence, f.DocumentKind, f.DocumentId, f.Title))
            .ShouldBe([(1, mlc.Kind, mlc.Id, mlc.Title), (2, prescription.Kind, prescription.Id, prescription.Title), (3, report.Kind, report.Id, report.Title)]);
        (await RequestOfAsync(mrd, record.PatientId, request)).Files.ShouldNotBeNull().Select(f => f.Sha256).ShouldBe(files.Select(f => f.Sha256));

        // Each file is its document, and its bytes are those the SHA-256 recorded is of.
        var downloaded = new List<byte[]>();
        foreach (var file in files)
        {
            var bytes = await DownloadAsync(mrd, request, file.Id);
            Convert.ToHexString(SHA256.HashData(bytes)).ShouldBe(file.Sha256);
            file.SizeBytes.ShouldBe(bytes.LongLength);
            downloaded.Add(bytes);
        }

        TestPdfRenderer.PageOf(downloaded[0]).ShouldContain(record.Mlc.Number);
        TestPdfRenderer.PageOf(downloaded[1]).ShouldContain(record.Prescription.PrescriptionNumber);
        TestPdfRenderer.PageOf(downloaded[2]).ShouldContain(report.Title["Lab report ".Length..]);

        // The doctor corrects the MLC afterwards. The copy stays as it was handed over: the same bytes, the old text.
        await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{record.CaseId}/mlc", Mlc("Corrected: fracture of the left radius", record.Mlc.Version));
        var again = await DownloadAsync(mrd, request, files[0].Id);
        again.ShouldBe(downloaded[0]);
        TestPdfRenderer.PageOf(again).ShouldContain(Injuries);
        TestPdfRenderer.PageOf(again).ShouldNotContain("fracture of the left radius");

        // The certificate states each hash in both parts and in its hash report, with the server and SHA256 ticked.
        var certificate = await mrd.GetAsync<PrintableDocumentDto>($"{Requests}/{request}/certificate/document");
        certificate.Html.ShouldContain("Certificate under section 63(4) of the Bharatiya Sakshya Adhiniyam, 2023");
        certificate.Html.ShouldContain(issued.RequestNumber);
        foreach (var file in files)
        {
            Regex.Matches(certificate.Html, file.Sha256).Count.ShouldBe(3);
        }

        Ticked().Matches(certificate.Html).Count.ShouldBe(4);
        certificate.Html.ShouldContain($"HC360 server ID {server.Services.GetRequiredService<ServerNodeBoard>().Node}");

        // A file brought back is found by its hash, however it is written; one with a digit changed is not.
        var second = files[1].Sha256;
        var match = (await VerifyAsync(mrd, string.Join(' ', second.ToLowerInvariant().Chunk(8).Select(c => new string(c))))).ShouldHaveSingleItem();
        (match.RequestId, match.Sequence, match.Title, match.Uhid, match.Purpose).ShouldBe((request, 2, prescription.Title, record.Uhid, RecordRequestPurpose.Legal));
        (await VerifyAsync(mrd, (second[0] == '0' ? "1" : "0") + second[1..])).ShouldBeEmpty();

        // The issue is in the audit log, with every hash: the sealed chain keeps them.
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var audit = await db.Set<AuditEvent>().AsNoTracking().SingleAsync(a => a.Action == "record.copy_issued" && a.EntityId == request.ToString(), Ct);
        (audit.Category, audit.PatientId).ShouldBe((AuditCategories.PhiAccess, record.PatientId));
        foreach (var file in files)
        {
            audit.Details.ShouldNotBeNull().ShouldContain(file.Sha256);
        }

        // Issued once.
        using var twice = await IssueRawAsync(mrd, request, new IssueRecordRequest(null, [Pick(mlc)]), TestPdfRenderer.Made);
        (await CodeAsync(twice, HttpStatusCode.UnprocessableEntity)).ShouldBe("record_request.not_pending");
    }

    [Fact]
    public async Task A_copy_is_issued_whole_or_not_at_all_and_only_medical_records_issues_one()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var record = await PatientAsync(withLabReport: false);
        var stranger = await PatientAsync(withLabReport: false);
        var request = await RequestAsync(mrd, record.PatientId, "Kavya (daughter)", RecordRequestPurpose.PatientCopy);
        var documents = (await mrd.GetAsync<List<RecordDocumentEntry>>($"{Requests}/{request}/documents")).Select(Pick).ToList();
        documents.Count.ShouldBe(2);

        // A doctor treats the patient, but does not issue copies of the record.
        using (var forbidden = await doctor.SendRawAsync(HttpMethod.Get, $"{Requests}/{request}/documents"))
        {
            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // No PDF can be made: nothing is issued.
        using (var unavailable = await IssueRawAsync(mrd, request, new IssueRecordRequest(null, documents), TestPdfRenderer.Fails))
        {
            (await CodeAsync(unavailable, HttpStatusCode.ServiceUnavailable)).ShouldBe("pdf.unavailable");
        }

        // Another patient's MLC, a kind of document nobody prints, or a document twice: nothing is issued either.
        using (var strangers = await IssueRawAsync(mrd, request, new IssueRecordRequest(null, [.. documents, new RecordCopyDocument(ClinicalKinds.Mlc, stranger.Mlc.Id)]),
                   TestPdfRenderer.Made))
        {
            (await CodeAsync(strangers, HttpStatusCode.UnprocessableEntity)).ShouldBe("record_copy.document_unavailable");
        }

        using (var unknown = await IssueRawAsync(mrd, request, new IssueRecordRequest(null, [new RecordCopyDocument("clinical.nursing-notes", record.Mlc.Id)]),
                   TestPdfRenderer.Made))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var twice = await IssueRawAsync(mrd, request, new IssueRecordRequest(null, [documents[0], documents[0]]), TestPdfRenderer.Made))
        {
            twice.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // The second PDF is larger than the file store keeps: nothing is issued, and the first's content, already written to
        // the server's disk, goes with it. No file is left there that no row names.
        var folder = server.Services.GetRequiredService<DataDirectories>().Files;
        var before = Blobs(folder);
        using (var tooLarge = await IssueRawAsync(mrd, request, new IssueRecordRequest(null, documents), TestPdfRenderer.TooLargeAfterTheFirst))
        {
            (await CodeAsync(tooLarge, HttpStatusCode.UnprocessableEntity)).ShouldBe("record_copy.document_not_kept");
        }

        var written = Blobs(folder).Except(before).ToList();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var named = await db.Set<StoredFile>().IgnoreQueryFilters().AsNoTracking().Where(f => written.Contains(f.Id)).Select(f => f.Id).ToListAsync(Ct);
            written.Except(named).ShouldBeEmpty("the content of a file whose row was rolled back");
        }

        var pending = await RequestOfAsync(mrd, record.PatientId, request);
        (pending.Status, pending.Files.ShouldNotBeNull().Count).ShouldBe((RecordRequestStatus.Requested, 0));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<StoredFile>().AsNoTracking().CountAsync(f => f.PatientId == record.PatientId && f.Category == "record-copy", Ct)).ShouldBe(0);
        }

        // Then issued whole.
        (await IssueAsync(mrd, request, documents)).Files.ShouldNotBeNull().Count.ShouldBe(2);

        // A chart lent to a ward comes back in a week; a copy given on paper, with no documents, is not returned.
        var ward = await RequestAsync(mrd, record.PatientId, "Dr. Meera Iyer", RecordRequestPurpose.PatientCare);
        (await IssueAsync(mrd, ward, [])).DueBackOn.ShouldBe(DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(330)).AddDays(RecordRequest.LendDays));
        var paper = await RequestAsync(mrd, record.PatientId, "Kavya (daughter)", RecordRequestPurpose.PatientCopy);
        var paperCopy = await IssueAsync(mrd, paper, []);
        (paperCopy.DueBackOn, paperCopy.Files.ShouldNotBeNull().Count).ShouldBe((null, 0));
        using var noCertificate = await mrd.SendRawAsync(HttpMethod.Get, $"{Requests}/{paper}/certificate/document");
        (await CodeAsync(noCertificate, HttpStatusCode.UnprocessableEntity)).ShouldBe("record_copy.no_files");
    }

    [Fact]
    public async Task Where_the_hospital_requires_the_doctors_DSC_a_prescription_without_one_is_not_offered_for_a_copy()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var facilityId = (await mrd.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId!.Value;
        var record = await PatientAsync(withLabReport: false);
        var request = await RequestAsync(mrd, record.PatientId, "Judicial Magistrate, Saidapet", RecordRequestPurpose.Legal);

        await DscTestPki.SetPolicyAsync(server, facilityId, "required", Ct);
        try
        {
            // The prescription was signed in HC360 but not with the doctor's DSC, so the hospital does not issue it: it is
            // not offered, and chosen anyway it is refused. The rest of the record is copied as offered.
            var documents = await mrd.GetAsync<List<RecordDocumentEntry>>($"{Requests}/{request}/documents");
            documents.ShouldHaveSingleItem().Kind.ShouldBe(ClinicalKinds.Mlc);
            using (var refused = await IssueRawAsync(mrd, request,
                       new IssueRecordRequest(null, [new RecordCopyDocument(ClinicalKinds.Prescription, record.Prescription.Id)]), TestPdfRenderer.Made))
            {
                (await CodeAsync(refused, HttpStatusCode.UnprocessableEntity)).ShouldBe("record_copy.document_unavailable");
            }

            (await IssueAsync(mrd, request, [.. documents.Select(Pick)])).Files.ShouldNotBeNull().ShouldHaveSingleItem().DocumentId.ShouldBe(record.Mlc.Id);
        }
        finally
        {
            await DscTestPki.SetPolicyAsync(server, facilityId, null, Ct);
        }
    }

    [Fact]
    public async Task A_file_changed_on_the_server_after_it_was_issued_is_not_given_out_and_the_copy_cannot_be_rewritten()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var record = await PatientAsync(withLabReport: false);
        var request = await RequestAsync(mrd, record.PatientId, "Insurance desk, Star Health", RecordRequestPurpose.Insurance);
        var documents = (await mrd.GetAsync<List<RecordDocumentEntry>>($"{Requests}/{request}/documents")).Select(Pick).ToList();
        var files = (await IssueAsync(mrd, request, documents)).Files.ShouldNotBeNull();
        files.Count.ShouldBe(2);

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var rows = await db.Set<RecordCopyFile>().AsNoTracking().Where(f => f.RecordRequestId == request).OrderBy(f => f.Sequence).ToListAsync(Ct);

        // The database refuses to change or remove a file of a copy issued.
        var altered = await Should.ThrowAsync<PostgresException>(() =>
            db.Database.ExecuteSqlAsync($"UPDATE clinical.record_copy_files SET sha256 = {rows[1].Sha256} WHERE id = {rows[0].Id}", Ct));
        altered.MessageText.ShouldContain("append-only");
        await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlAsync($"DELETE FROM clinical.record_copy_files WHERE id = {rows[0].Id}", Ct));

        // Someone puts the second file's content in place of the first on the server's disk: the first is not given out,
        // and that is recorded as a security event; the second still is.
        var stored = await db.Set<StoredFile>().AsNoTracking().Where(f => f.Id == rows[0].StoredFileId || f.Id == rows[1].StoredFileId).ToDictionaryAsync(f => f.Id, Ct);
        var folder = server.Services.GetRequiredService<DataDirectories>().Files;
        File.Copy(Path.Combine(folder, stored[rows[1].StoredFileId].StoragePath), Path.Combine(folder, stored[rows[0].StoredFileId].StoragePath), overwrite: true);

        using (var swapped = await mrd.SendRawAsync(HttpMethod.Get, $"{Requests}/{request}/files/{files[0].Id}"))
        {
            (await CodeAsync(swapped, HttpStatusCode.Conflict)).ShouldBe("record_copy.altered");
        }

        var alarm = await db.Set<AuditEvent>().AsNoTracking().SingleAsync(a => a.Action == "record.copy_altered" && a.EntityId == files[0].Id.ToString(), Ct);
        (alarm.Category, alarm.Severity, alarm.PatientId).ShouldBe((AuditCategories.Security, AuditSeverity.Critical, record.PatientId));
        Convert.ToHexString(SHA256.HashData(await DownloadAsync(mrd, request, files[1].Id))).ShouldBe(files[1].Sha256);

        // A file is found only through the copy it belongs to.
        using var elsewhere = await mrd.SendRawAsync(HttpMethod.Get, $"{Requests}/{Guid.CreateVersion7()}/files/{files[1].Id}");
        elsewhere.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private const string Injuries = "Lacerated wound 4 x 1 cm, left forearm";

    private static class ClinicalKinds
    {
        public const string Prescription = "clinical.prescription";
        public const string Mlc = "clinical.mlc";
    }

    private sealed record Patient(Guid PatientId, string Uhid, Guid CaseId, MlcDto Mlc, PrescriptionDto Prescription);

    /// <summary>
    /// A road-accident patient of the ER: the doctor's MLC entry and a signed prescription, and a released CRP report when
    /// <paramref name="withLabReport"/>.
    /// </summary>
    private async Task<Patient> PatientAsync(bool withLabReport)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        var row = (await doctor.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.VisitId == visit.Id);
        var mlc = (await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{row.CaseId}/mlc", Mlc(Injuries, null))).Mlc.ShouldNotBeNull();

        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patientId, visit.Id, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: "Pain"));
        var drug = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Paracetamol")).Single(d => d.Code == ParacetamolCode);
        var prescription = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patientId, encounter.Id, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null, RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    drug.Id, drug.Name, drug.GenericName, drug.Strength, drug.DosageForm, Route: "Oral", Dose: "1 tablet", DoseFrequency.TDS,
                    CustomFrequency: null, DosesPerDay: null, 3, Quantity: null, Instructions: "After food", drug.IsControlled, drug.ControlledSchedule),
            ]));
        await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{prescription.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: false, ConfirmControlledDrugs: false, prescription.Version));

        if (withLabReport)
        {
            var lab = await server.SignInAsync(HospitalServer.Users.Lab);
            var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
            var tube = await LabVerificationTests.ReceivedSampleAsync(server, "LAB-CRP", patientId);
            var lines = await LabVerificationTests.EnterAsync(lab, tube, new Dictionary<string, string> { ["LAB-CRP"] = "3.2" });
            await pathologist.PostAsync<VerificationOutcome>("/api/v1/lab/results/verify",
                new VerifyResultsRequest([.. lines.Select(l => l.ResultId)], null, null, Release: true));
        }

        return new Patient(patientId, row.Uhid, row.CaseId, mlc, prescription);
    }

    private static SaveMlcRequest Mlc(string injuries, uint? version) => new(
        MlcBroughtBy.Ambulance, "108 ambulance, EMT Priya", MlcCause.RoadTrafficAccident, DateTimeOffset.UtcNow.AddHours(-1), "OMR, Thoraipakkam",
        injuries, true, false, "Adyar", "TNMC 45678", version);

    private static RecordCopyDocument Pick(RecordDocumentEntry document) => new(document.Kind, document.Id);

    private static async Task<Guid> RequestAsync(ApiClient mrd, Guid patientId, string by, RecordRequestPurpose purpose) =>
        (await mrd.PostAsync<RecordRequestDto>(Requests, new CreateRecordRequestRequest(patientId, null, by, null, purpose, null))).Id;

    private static async Task<RecordRequestDto> RequestOfAsync(ApiClient mrd, Guid patientId, Guid id) =>
        (await mrd.GetAsync<PagedResult<RecordRequestDto>>($"{Requests}?pageSize=100")).Items.Single(r => r.Id == id && r.PatientId == patientId);

    private static async Task<RecordRequestDto> IssueAsync(ApiClient mrd, Guid request, List<RecordCopyDocument> documents)
    {
        using var response = await IssueRawAsync(mrd, request, new IssueRecordRequest(null, documents), TestPdfRenderer.Made);
        return await ApiClient.ReadAsync<RecordRequestDto>(response, HttpMethod.Post, $"{Requests}/{request}/issue");
    }

    /// <summary>Issues the request, with its PDFs made as <paramref name="pdf"/> says (<see cref="TestPdfRenderer"/>).</summary>
    private static Task<HttpResponseMessage> IssueRawAsync(ApiClient mrd, Guid request, IssueRecordRequest body, string pdf)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"{Requests}/{request}/issue") { Content = JsonContent.Create(body, options: ApiClient.Json) };
        message.Headers.Add(TestPdfRenderer.Header, pdf);
        return mrd.Http.SendAsync(message, Ct);
    }

    // The ids of the files whose content is on the server's disk (EncryptedFileStore names each "{id:N}.bin").
    private static HashSet<Guid> Blobs(string folder) => Directory.Exists(folder)
        ? [.. Directory.EnumerateFiles(folder, "*.bin", SearchOption.AllDirectories).Select(f => Guid.ParseExact(Path.GetFileNameWithoutExtension(f), "N"))]
        : [];

    private static async Task<byte[]> DownloadAsync(ApiClient mrd, Guid request, Guid file)
    {
        using var response = await mrd.SendRawAsync(HttpMethod.Get, $"{Requests}/{request}/files/{file}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/pdf");
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    private static async Task<List<RecordCopyMatch>> VerifyAsync(ApiClient mrd, string sha256) =>
        await mrd.PostAsync<List<RecordCopyMatch>>("/api/v1/mrd/record-copies/verify", new VerifyRecordCopyRequest(sha256));

    private static async Task<string?> CodeAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, body);
        using var problem = JsonDocument.Parse(body);
        return problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [GeneratedRegex("class=\"box on\"")]
    private static partial Regex Ticked();
}
