using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Dialysis;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A dialysis patient's infection status decides their machine and isolation (maker-checker, slice 15), over the real
/// API: it changes only on a verified lab result of the patient for the infection concerned; a sero-negative patient found
/// HBsAg reactive is isolated at once; taking a patient off a positive status waits for a second person (202), the status
/// in force standing meanwhile, and the one who asked never approves it. The profile form cannot change it.
/// </summary>
public sealed class DialysisInfectionStatusTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Found_positive_isolates_at_once_and_back_to_negative_waits_for_a_second_person_on_the_lab_result()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var second = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        var patient = await LabVerificationTests.RegisterPatientAsync(server);
        var profile = await EnrolAsync(doctor, patient.Id);

        // Not on any result: refused. On an unverified result, or one that tests for something else: refused.
        (await RefusalAsync(doctor, Change(patient.Id), new ChangeInfectionStatusRequest(SeroStatus.HBsAg, Guid.CreateVersion7(), "Screening positive", profile.Version)))
            .ShouldBe((HttpStatusCode.BadRequest, "validation"));
        var unverified = await ResultAsync(patient.Id, "LAB-HBSAG", "Reactive", verify: false);
        (await RefusalAsync(doctor, Change(patient.Id), new ChangeInfectionStatusRequest(SeroStatus.HBsAg, unverified, "Screening positive", profile.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "dialysis.lab_result_unverified"));
        var glucose = await ResultAsync(patient.Id, "LAB-FBS", "96", verify: true);
        (await RefusalAsync(doctor, Change(patient.Id), new ChangeInfectionStatusRequest(SeroStatus.HBsAg, glucose, "Screening positive", profile.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "dialysis.lab_result_mismatch"));

        // HBsAg reactive, verified: the patient is isolated at once (the stricter direction).
        var reactive = await ResultAsync(patient.Id, "LAB-HBSAG", "Reactive", verify: true);
        using (var isolated = await doctor.SendRawAsync(HttpMethod.Post, Change(patient.Id), new ChangeInfectionStatusRequest(SeroStatus.HBsAg, reactive, "Routine 3-monthly screening reactive", profile.Version)))
        {
            isolated.StatusCode.ShouldBe(HttpStatusCode.NoContent, await isolated.Content.ReadAsStringAsync(Ct));
        }

        profile = await ProfileAsync(doctor, patient.Id);
        (profile.SeroStatus, profile.SeroLabResultId, profile.SeroChangeRequestedByName).ShouldBe((SeroStatus.HBsAg, reactive, null));

        // The profile form does not change it.
        (await RefusalAsync(doctor, $"/api/v1/dialysis/patients/{patient.Id}", Profile(SeroStatus.Negative, profile.Version), HttpMethod.Put))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "dialysis.infection_status_change"));

        // Back to sero-negative on a non-reactive HBsAg: asked for (202), the patient stays on the HBsAg machine meanwhile.
        var cleared = await ResultAsync(patient.Id, "LAB-HBSAG", "Non-reactive", verify: true);
        using var response = await doctor.SendRawAsync(HttpMethod.Post, Change(patient.Id),
            new ChangeInfectionStatusRequest(SeroStatus.Negative, cleared, "Repeat HBsAg non-reactive; first result a false positive", profile.Version));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, Change(patient.Id));
        asked.Kind.ShouldBe("dialysis.infection_status");
        asked.Details.Single(d => d.Label == "Lab result").Value.ShouldStartWith("Hepatitis B surface antigen (HBsAg): Non-reactive");
        profile = await ProfileAsync(doctor, patient.Id);
        (profile.SeroStatus, profile.InfectionStatusPendingApprovalId).ShouldBe((SeroStatus.HBsAg, asked.Id));

        // The doctor who asked does not approve it; a second doctor does.
        (await RefusalAsync(doctor, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        var approved = await MakerChecker.ApproveAsync(second, asked);
        profile = await ProfileAsync(doctor, patient.Id);
        profile.ShouldSatisfyAllConditions(
            p => p.SeroStatus.ShouldBe(SeroStatus.Negative),
            p => p.SeroLabResultId.ShouldBe(cleared),
            p => p.SeroChangedByName.ShouldBe(approved.DecidedByName),
            p => p.SeroChangeRequestedByName.ShouldBe(asked.RequestedByName),
            p => p.InfectionStatusPendingApprovalId.ShouldBeNull());
    }

    private static string Change(Guid patientId) => $"/api/v1/dialysis/patients/{patientId}/infection-status";

    private SaveDialysisProfileRequest Profile(SeroStatus sero, uint? version)
    {
        var today = server.Services.GetRequiredService<TimeProvider>().Today();
        return new SaveDialysisProfileRequest(
            DryWeightKg: 58m, VascularAccess: VascularAccessType.AVF, AccessSite: "Left brachiocephalic", AccessCreatedOn: null,
            ScheduleDays: [Weekday.Tuesday, Weekday.Friday], Shift: DialysisShift.Afternoon, SessionMinutes: 240, SeroStatus: sero,
            SeroTestedOn: today.AddDays(-20), ReusePolicy: DialyzerReusePolicy.SingleUse, MaxDialyzerUses: 1, NephrologistId: null,
            PrimaryDiagnosis: "CKD stage 5", AnticoagulationNotes: null, IsActive: true, Version: version);
    }

    private async Task<DialysisProfileDto> EnrolAsync(ApiClient doctor, Guid patientId)
    {
        await doctor.PutAsync<JsonElement>($"/api/v1/dialysis/patients/{patientId}", Profile(SeroStatus.Negative, null));
        return await ProfileAsync(doctor, patientId);
    }

    private static Task<DialysisProfileDto> ProfileAsync(ApiClient api, Guid patientId) => api.GetAsync<DialysisProfileDto>($"/api/v1/dialysis/patients/{patientId}");

    /// <summary>A result of <paramref name="testCode"/> for the patient, entered by the technician and, when asked, verified by the pathologist.</summary>
    private async Task<Guid> ResultAsync(Guid patientId, string testCode, string value, bool verify)
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var tube = await LabVerificationTests.ReceivedSampleAsync(server, testCode, patientId);
        var line = (await LabVerificationTests.EnterAsync(lab, tube, new Dictionary<string, string> { [testCode] = value })).ShouldHaveSingleItem();
        if (verify)
        {
            var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
            (await pathologist.PostAsync<VerificationOutcome>("/api/v1/lab/results/verify", new VerifyResultsRequest([line.ResultId], null, null, Release: false)))
                .Verified.ShouldBe(1);
        }

        return line.ResultId;
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body, HttpMethod? method = null)
    {
        using var response = await api.SendRawAsync(method ?? HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (response.StatusCode, problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }
}
