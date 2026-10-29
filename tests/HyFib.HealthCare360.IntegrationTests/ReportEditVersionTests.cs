using System.Net;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS REL-04 on reports, codes and the Tally ledger names. A report is created by its first save, so a later save that
/// never saw it is refused like one made to an older version; the radiologist's or cardiologist's words are never
/// overwritten unseen, and a report is submitted and signed only as it was read.
/// </summary>
public sealed class ReportEditVersionTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_imaging_report_is_not_saved_over_one_written_meanwhile()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var patient = await RegisterPatientAsync(admin);
        var xray = (await admin.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?category=Radiology&search=RAD-XR-CHEST"))
            .Single(s => s.Code == "RAD-XR-CHEST");
        await admin.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, OrderPriority.Routine, "Cough for two weeks",
            [new OrderLineRequest(xray.Id, Notes: null)], OrderSetId: null, ConfirmDuplicates: false));
        var studyId = (await admin.GetAsync<PagedResult<ImagingStudyListItem>>($"/api/v1/radiology/studies?patientId={patient.Id}")).Items.ShouldHaveSingleItem().Id;
        await admin.PostAsync($"/api/v1/radiology/studies/{studyId}/start");
        await admin.PostAsync($"/api/v1/radiology/studies/{studyId}/acquire", new AcquireStudyRequest(false, null, null, null, null));
        var path = $"/api/v1/radiology/studies/{studyId}/report";
        SaveRadiologyReportRequest Report(string impression, uint? version) =>
            new(null, "PA view", "Cough", null, "Both lung fields are clear.", impression, false, null, version);

        var first = await admin.PutAsync<RadiologyReportDto>(path, Report("No active lung lesion.", null));
        using (var unseen = await admin.SendRawAsync(HttpMethod.Put, path, Report("Consolidation, right lower lobe.", null)))
        {
            unseen.StatusCode.ShouldBe(HttpStatusCode.Conflict, "A second radiologist who opened the study before the first saved.");
        }

        await admin.PutAsync<RadiologyReportDto>(path, Report("No active lung lesion. Heart size normal.", first.Version));
        using (var stale = await admin.SendRawAsync(HttpMethod.Put, path, Report("Consolidation, right lower lobe.", first.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await admin.GetAsync<RadiologyReportDto>(path)).Impression.ShouldBe("No active lung lesion. Heart size normal.");

        // Submitting and signing name the version read: a report changed since is not submitted or signed unseen.
        var read = await admin.GetAsync<RadiologyReportDto>(path);
        await ShouldBeRefusedAsync(admin, $"{path}/submit", new SubmitReportRequest(first.Version), HttpStatusCode.Conflict);
        await ShouldBeRefusedAsync(admin, $"{path}/submit", new SubmitReportRequest(0), HttpStatusCode.BadRequest);
        var submitted = await admin.PostAsync<RadiologyReportDto>($"{path}/submit", new SubmitReportRequest(read.Version));
        submitted.Status.ShouldBe(DiagnosticReportStatus.Reported);

        await ShouldBeRefusedAsync(admin, $"{path}/verify", new SignReportRequest(null, read.Version), HttpStatusCode.Conflict);
        (await admin.PostAsync<RadiologyReportDto>($"{path}/verify", new SignReportRequest(null, submitted.Version))).Status
            .ShouldBe(DiagnosticReportStatus.Released);
    }

    [Fact]
    public async Task A_cardiology_report_is_not_saved_over_one_written_meanwhile()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var patient = await RegisterPatientAsync(admin);
        var studyId = await AcquiredEcgAsync(admin, patient.Id);
        var path = $"/api/v1/cardiology/studies/{studyId}/report";
        SaveCardiologyReportRequest Report(string conclusion, uint? version) => new(null, null, null, null, null, conclusion, null, false, null, version);

        var first = (await admin.PutAsync<CardiologyStudyDetail>(path, Report("Normal ECG.", null))).Report.ShouldNotBeNull();
        using (var unseen = await admin.SendRawAsync(HttpMethod.Put, path, Report("Sinus tachycardia.", null)))
        {
            unseen.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        await admin.PutAsync<CardiologyStudyDetail>(path, Report("Normal ECG. No ST changes.", first.Version));
        using (var stale = await admin.SendRawAsync(HttpMethod.Put, path, Report("Sinus tachycardia.", first.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var read = (await admin.GetAsync<CardiologyStudyDetail>($"/api/v1/cardiology/studies/{studyId}")).Report.ShouldNotBeNull();
        read.Conclusion.ShouldBe("Normal ECG. No ST changes.");

        await ShouldBeRefusedAsync(admin, $"{path}/submit", new SubmitCardiologyReportRequest(first.Version), HttpStatusCode.Conflict);
        var submitted = (await admin.PostAsync<CardiologyStudyDetail>($"{path}/submit", new SubmitCardiologyReportRequest(read.Version))).Report.ShouldNotBeNull();
        submitted.Status.ShouldBe(DiagnosticReportStatus.Reported);

        await ShouldBeRefusedAsync(admin, $"{path}/verify", new SignCardiologyReportRequest(null, read.Version), HttpStatusCode.Conflict);
        (await admin.PostAsync<CardiologyStudyDetail>($"{path}/verify", new SignCardiologyReportRequest(null, submitted.Version))).Report.ShouldNotBeNull()
            .Status.ShouldBe(DiagnosticReportStatus.Released);
    }

    [Fact]
    public async Task A_procedure_code_is_changed_only_as_it_was_loaded()
    {
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var code = "RV" + Digits(6);
        var created = await mrd.PostAsync<ProcedureCodeDto>("/api/v1/mrd/procedure-codes", new SaveProcedureCodeRequest(code, "Version test procedure", null, null, null, true));
        var path = $"/api/v1/mrd/procedure-codes/{created.Id}";

        using (var unversioned = await mrd.SendRawAsync(HttpMethod.Put, path, new SaveProcedureCodeRequest(code, "Without a version", created.System, null, null, true)))
        {
            unversioned.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var saved = await mrd.PutAsync<ProcedureCodeDto>(path, new SaveProcedureCodeRequest(code, "Local wording", created.System, null, null, true, created.Version));
        saved.Title.ShouldBe("Local wording");
        using (var stale = await mrd.SendRawAsync(HttpMethod.Put, path, new SaveProcedureCodeRequest(code, "Other wording", created.System, null, null, true, created.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
    }

    [Fact]
    public async Task The_tally_ledger_names_are_saved_only_over_the_names_as_they_were_loaded()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        const string path = "/api/v1/billing/tally/ledgers";
        var original = await admin.GetAsync<TallyLedgerMap>(path);
        try
        {
            await admin.PutAsync(path, original with { RoundOff = "Round Off (test)", Version = original.Version });
            using (var unseen = await admin.SendRawAsync(HttpMethod.Put, path, original with { RoundOff = "Rounding", Version = null }))
            {
                unseen.StatusCode.ShouldBe(HttpStatusCode.Conflict, "The facility's names are saved now: a change that never saw them is refused.");
            }

            var loaded = await admin.GetAsync<TallyLedgerMap>(path);
            loaded.Version.ShouldNotBeNull();
            await admin.PutAsync(path, loaded with { RoundOff = "Round Off (again)" });
            using (var stale = await admin.SendRawAsync(HttpMethod.Put, path, loaded with { RoundOff = "Rounding" }))
            {
                stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            }

            (await admin.GetAsync<TallyLedgerMap>(path)).RoundOff.ShouldBe("Round Off (again)");
        }
        finally
        {
            var now = await admin.GetAsync<TallyLedgerMap>(path);
            await admin.PutAsync(path, original with { Version = now.Version });
        }
    }

    private static async Task ShouldBeRefusedAsync(ApiClient client, string path, object body, HttpStatusCode status)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
    }

    // At the facility the reporter is signed in to: other tests add facilities, and one elsewhere is not found there.
    private async Task<Guid> AcquiredEcgAsync(ApiClient reporter, Guid patientId)
    {
        var facilityId = (await reporter.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The reporter has no facility.");
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = DateTimeOffset.UtcNow;
        var study = new CardiologyStudy
        {
            FacilityId = facilityId,
            PatientId = patientId,
            AccessionNumber = "NHCDV" + Digits(7),
            ServiceCode = "CARD-ECG",
            StudyName = "ECG (12-lead)",
            StudyType = CardiologyStudyType.Ecg,
            Priority = OrderPriority.Routine,
            OrderedAt = now.AddHours(-1),
            Status = CardiologyStudyStatus.Acquired,
            StartedAt = now.AddMinutes(-30),
            AcquiredAt = now.AddMinutes(-25),
        };
        db.Add(study);
        await db.SaveChangesAsync(Ct);
        return study.Id;
    }

    private static Task<PatientCreated> RegisterPatientAsync(ApiClient client) =>
        client.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Kiran", MiddleName: null, LastName: "Rv" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1965, 1, 1).AddDays(Random.Shared.Next(7300)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true, WhatsAppOptIn: false));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));
}
