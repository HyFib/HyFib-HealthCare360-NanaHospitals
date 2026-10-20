using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The pathologist's sign-off over the real API: a critical value is verified only once the call to the clinician is
/// documented with read-back (LIS-05), so it can never reach the report without anyone having been told.
/// </summary>
public sealed class LabVerificationTests(HospitalServer server)
{
    private const string VerifyPath = "/api/v1/lab/results/verify";

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

    /// <summary>The problem details of a request the server refuses as against its rules (422).</summary>
    private static async Task<JsonElement> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
    }

    private static Task SetPaymentGateAsync(ApiClient admin, Guid facilityId, bool required) =>
        admin.PutAsync("/api/v1/admin/settings/lab.requirePaymentBeforeCollection", new SaveSettingRequest(JsonValue.Create(required), facilityId, false, null));

    private async Task<PatientCreated> RegisterPatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: "Suresh", MiddleName: null, LastName: "Verify" + Letters(8), Gender: Gender.Male,
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
