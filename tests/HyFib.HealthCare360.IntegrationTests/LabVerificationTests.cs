using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.Diagnostics.Features.QualityControl;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The pathologist's sign-off over the real API: a critical value is verified only once the call to the clinician is
/// documented with read-back (LIS-05), so it can never reach the report without anyone having been told; and while
/// today's QC of an assay has failed, its results are verified only with a pathologist's audited override, or once QC
/// passes again (LIS-06).
/// </summary>
public sealed class LabVerificationTests(HospitalServer server)
{
    private const string VerifyPath = "/api/v1/lab/results/verify";
    private const string OverrideReason = "Patient values match the previous run and the level 2 control is in range";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_critical_value_is_verified_only_once_the_call_to_the_doctor_is_documented()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);

        // The technician enters a tube of electrolytes: the potassium is critically high, sodium and chloride are normal.
        var tube = await ReceivedSampleAsync("LAB-ELECTROLYTES");
        var lines = await EnterAsync(lab, tube, new Dictionary<string, string> { ["NA"] = "138", ["K"] = "7.2", ["CL"] = "101" });
        var potassium = lines.Single(l => l.TestCode == "K");
        potassium.Flag.ShouldBe(ResultFlag.CriticalHigh);
        potassium.IsCritical.ShouldBeTrue();
        lines.Where(l => l.TestCode != "K").ShouldAllBe(l => !l.IsCritical);
        var tubeResults = lines.Select(l => l.ResultId).ToList();

        // Nobody has told the doctor yet: the pathologist cannot verify the tube, and the refusal names the critical value.
        var refused = await RefusalAsync(pathologist, VerifyPath, new VerifyResultsRequest(tubeResults, null, null, Release: false));
        refused.GetProperty("code").GetString().ShouldBe("lab.critical_call_required");
        refused.GetProperty("data").GetProperty("results").EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ShouldBe([potassium.ResultId]);
        (await SheetAsync(lab, tube)).Lines.ShouldAllBe(l => l.Status == LabResultStatus.Entered, "nothing on the tube is verified");

        // The technician phones the duty doctor, who reads the value back, and documents the call.
        await lab.PostAsync<Guid>($"/api/v1/lab/results/{potassium.ResultId}/critical-calls", new CriticalCallDetails(
            "Dr. Meena Iyer", "Duty doctor, casualty", null, CommunicationMethod.Phone, DateTimeOffset.UtcNow.AddMinutes(-2), ReadBackConfirmed: true,
            "Repeat sample and ECG advised"));
        (await SheetAsync(lab, tube)).Lines.Single(l => l.TestCode == "K").HasCriticalCall.ShouldBeTrue();

        // Now the tube is verified, and the call is not asked for again.
        var verified = await pathologist.PostAsync<VerificationOutcome>(VerifyPath, new VerifyResultsRequest(tubeResults, null, null, Release: false));
        verified.Verified.ShouldBe(3);
        verified.CriticalCallsRecorded.ShouldBe(0, "the call was documented before the verification");
        (await SheetAsync(lab, tube)).Lines.ShouldAllBe(l => l.Status == LabResultStatus.Verified);

        // The critical call register shows what was found, who was told, how, and that the value was read back.
        var call = await RegisteredCallAsync(pathologist, potassium.ResultId);
        call.Finding.ShouldBe("Potassium 7.2 mmol/L (CriticalHigh)");
        (call.InformedPersonName, call.Method, call.ReadBackConfirmed).ShouldBe(("Dr. Meena Iyer", CommunicationMethod.Phone, true));
    }

    [Fact]
    public async Task A_call_documented_with_the_verification_is_recorded_before_the_critical_value_is_verified()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var me = await pathologist.GetAsync<AuthUser>("/api/v1/auth/me");

        var tube = await ReceivedSampleAsync("LAB-ELECTROLYTES");
        var lines = await EnterAsync(lab, tube, new Dictionary<string, string> { ["NA"] = "141", ["K"] = "2.1", ["CL"] = "99" });
        var potassium = lines.Single(l => l.TestCode == "K");
        potassium.Flag.ShouldBe(ResultFlag.CriticalLow);

        // The pathologist phones the ward and documents the call in the same step as the verification.
        var verified = await pathologist.PostAsync<VerificationOutcome>(VerifyPath, new VerifyResultsRequest(
            [.. lines.Select(l => l.ResultId)], null,
            new CriticalCallDetails("Sr. Kavitha Nair", "Ward sister, medical ward", null, CommunicationMethod.Phone, DateTimeOffset.UtcNow.AddMinutes(-1),
                ReadBackConfirmed: true, null),
            Release: false));
        verified.Verified.ShouldBe(3);
        verified.CriticalCallsRecorded.ShouldBe(1);

        var sheet = await SheetAsync(lab, tube);
        sheet.Lines.ShouldAllBe(l => l.Status == LabResultStatus.Verified);
        sheet.Lines.Single(l => l.TestCode == "K").HasCriticalCall.ShouldBeTrue();
        var call = await RegisteredCallAsync(pathologist, potassium.ResultId);
        (call.InformedPersonName, call.ReadBackConfirmed, call.CalledBy).ShouldBe(("Sr. Kavitha Nair", true, me.DisplayName));
    }

    [Fact]
    public async Task While_qc_has_failed_results_are_verified_only_with_a_pathologists_audited_override_or_once_qc_passes()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var pathologistId = (await pathologist.GetAsync<AuthUser>("/api/v1/auth/me")).Id;

        // Two patients' fasting sugars are entered, both normal.
        var first = (await EnterAsync(lab, await ReceivedSampleAsync("LAB-FBS"), new Dictionary<string, string> { ["LAB-FBS"] = "92" })).Single().ResultId;
        var second = (await EnterAsync(lab, await ReceivedSampleAsync("LAB-FBS"), new Dictionary<string, string> { ["LAB-FBS"] = "88" })).Single().ResultId;

        // The glucose assay's level 1 control on the chemistry analyser (seeded with three weeks of accepted runs).
        var fbs = (await lab.GetAsync<PagedResult<LabTestListItem>>("/api/v1/lab/tests?search=LAB-FBS")).Items.Single(t => t.Code == "LAB-FBS");
        var lot = (await lab.GetAsync<List<QcLotDto>>($"/api/v1/lab/qc/lots?labTestId={fbs.Id}")).First(l => l.Level == 1 && !l.IsExpired);
        try
        {
            // This morning's control reads 5 SD above its target: the 1-3s rule rejects the run.
            var rejected = await RecordQcRunAsync(lab, lot, sds: 5);
            rejected.Outcome.ShouldBe(QcOutcome.Rejected);
            rejected.Violations.ShouldContain("1-3s");

            // Verification of the assay is locked, and the refusal names the assay and the rejected run.
            var locked = await RefusalAsync(pathologist, VerifyPath, new VerifyResultsRequest([first], null, null, Release: false));
            locked.GetProperty("code").GetString().ShouldBe("lab.qc_lockout");
            var lockout = locked.GetProperty("data").GetProperty("lockouts").EnumerateArray().ShouldHaveSingleItem();
            lockout.GetProperty("labTestId").GetGuid().ShouldBe(fbs.Id);
            lockout.GetProperty("rejectedRunIds").EnumerateArray().Select(r => r.GetGuid()).ShouldBe([rejected.Id]);

            // Only a pathologist may override: the technician who runs the QC may not, on the QC board or when verifying.
            await ShouldBeForbiddenAsync(lab, "/api/v1/lab/qc/overrides", new QcOverrideRequest(fbs.Id, OverrideReason));
            await ShouldBeForbiddenAsync(lab, VerifyPath, new VerifyResultsRequest([first], OverrideReason, null, Release: false));
            (await QcBoardAsync(lab, lot)).Locked.ShouldBeTrue("the refused attempts recorded no override");

            // The pathologist verifies with a reason: the override is recorded against the rejected run and audited.
            var overridden = await pathologist.PostAsync<VerificationOutcome>(VerifyPath, new VerifyResultsRequest([first], OverrideReason, null, Release: false));
            (overridden.Verified, overridden.QcOverrides).ShouldBe((1, 1));
            (await QcRunAsync(pathologist, lot, rejected.Id)).Overridden.ShouldBeTrue();
            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            var audited = (await admin.GetAsync<PagedResult<AuditEventDto>>(
                $"/api/v1/admin/audit?action=lab.qc.override&entityType=QcRun&entityId={rejected.Id}")).Items.ShouldHaveSingleItem();
            audited.UserId.ShouldBe(pathologistId);
            audited.Summary.ShouldBe($"QC lockout overridden: {OverrideReason}");
            audited.Severity.ShouldBe(AuditSeverity.Warning);

            // The override was for that run only: the next control run is rejected too, and the assay is locked again.
            (await RecordQcRunAsync(lab, lot, sds: 5)).Outcome.ShouldBe(QcOutcome.Rejected);
            (await RefusalAsync(pathologist, VerifyPath, new VerifyResultsRequest([second], null, null, Release: false)))
                .GetProperty("code").GetString().ShouldBe("lab.qc_lockout");

            // After recalibration the control reads on target: QC passes and the lock clears without an override.
            (await RecordQcRunAsync(lab, lot, sds: 0)).Outcome.ShouldBe(QcOutcome.Accepted);
            var verified = await pathologist.PostAsync<VerificationOutcome>(VerifyPath, new VerifyResultsRequest([second], null, null, Release: false));
            (verified.Verified, verified.QcOverrides).ShouldBe((1, 0));
        }
        finally
        {
            // The server is shared by every test of the run: the assay is never left locked.
            if ((await QcBoardAsync(lab, lot)).Locked)
            {
                await RecordQcRunAsync(lab, lot, sds: 0);
            }
        }
    }

    /// <summary>A walk-in order for one test, its tube collected and received in the lab (the counter's payment gate waived).</summary>
    private async Task<CreatedSample> ReceivedSampleAsync(string testCode)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var patient = await RegisterPatientAsync();
        var test = (await lab.GetAsync<PagedResult<LabTestListItem>>($"/api/v1/lab/tests?search={testCode}")).Items.Single(t => t.Code == testCode);
        var order = await lab.PostAsync<LabOrderCreated>("/api/v1/lab/orders", new CreateLabOrderRequest(
            patient.Id, null, [test.Id], OrderPriority.Routine, LabOrderSource.WalkIn, null, null, null));
        var tube = order.Samples.ShouldHaveSingleItem();

        // A walk-in test is drawn only once paid; these tests are about the sign-off, so the counter waives that here.
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");
        await SetPaymentGateAsync(admin, facilityId, required: false);
        try
        {
            await lab.PostAsync<List<SampleLabel>>($"/api/v1/lab/orders/{order.Id}/collect", new CollectSamplesRequest(null, null, null));
            var outcome = (await lab.PostAsync<List<ReceiveOutcome>>("/api/v1/lab/samples/receive", new ReceiveSamplesRequest([tube.Barcode]))).ShouldHaveSingleItem();
            outcome.Received.ShouldBeTrue(outcome.Message);
        }
        finally
        {
            await SetPaymentGateAsync(admin, facilityId, required: true);
        }

        return tube;
    }

    /// <summary>The technician enters a value for every analyte on the tube, by test code.</summary>
    private static async Task<List<ResultSheetLine>> EnterAsync(ApiClient lab, CreatedSample tube, IReadOnlyDictionary<string, string> values)
    {
        var sheet = await SheetAsync(lab, tube);
        return await lab.PostAsync<List<ResultSheetLine>>("/api/v1/lab/results", new EnterResultsRequest(
            [.. sheet.Lines.Select(l => new ResultEntry(l.ResultId, values[l.TestCode], null, null, null, null))]));
    }

    private static Task<ResultSheet> SheetAsync(ApiClient api, CreatedSample tube) => api.GetAsync<ResultSheet>($"/api/v1/lab/samples/{tube.Id}/results");

    /// <summary>Today's entry of the critical call register for a result.</summary>
    private static async Task<CriticalCallDto> RegisteredCallAsync(ApiClient api, Guid resultId) =>
        (await api.GetAsync<PagedResult<CriticalCallDto>>("/api/v1/lab/critical-calls?kind=Laboratory&pageSize=100")).Items.Single(c => c.LabResultId == resultId);

    /// <summary>A control run of the lot, <paramref name="sds"/> standard deviations from its target mean.</summary>
    private static Task<QcRunDto> RecordQcRunAsync(ApiClient lab, QcLotDto lot, int sds) =>
        lab.PostAsync<QcRunDto>("/api/v1/lab/qc/runs", new RecordQcRunRequest(lot.Id, lot.TargetMean + (sds * lot.TargetSd), null));

    private static async Task<QcRunDto> QcRunAsync(ApiClient api, QcLotDto lot, Guid runId) =>
        (await api.GetAsync<PagedResult<QcRunDto>>($"/api/v1/lab/qc/runs?lotId={lot.Id}&pageSize=200")).Items.Single(r => r.Id == runId);

    /// <summary>The lot's row on today's QC board.</summary>
    private static async Task<QcStatusRow> QcBoardAsync(ApiClient api, QcLotDto lot) =>
        (await api.GetAsync<List<QcStatusRow>>("/api/v1/lab/qc/status")).Single(r => r.LotId == lot.Id);

    /// <summary>The problem details of a request the server refuses as against its rules (422).</summary>
    private static async Task<JsonElement> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
    }

    private static async Task ShouldBeForbiddenAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"POST {path}");
    }

    private static Task SetPaymentGateAsync(ApiClient admin, Guid facilityId, bool required) =>
        admin.PutAsync("/api/v1/admin/settings/lab.requirePaymentBeforeCollection", new SaveSettingRequest(JsonValue.Create(required), facilityId, false, null));

    private async Task<PatientCreated> RegisterPatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Suresh"), MiddleName: null, LastName: "Verify" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
