using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Flags;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The patient banner's safety flags need a second clinician where one person could silence them (maker-checker, slice 7),
/// over the real API and PostgreSQL: Do Not Resuscitate is put on at once, and only by a doctor, and taken off only when a
/// second doctor agrees; an allergy is recorded or made stricter at once, and taken out of the prescribing checks or
/// lowered in severity only when a prescriber who did not ask agrees. Until then the flag stays on the banner and in every
/// check, and each change is made only over the flag as it was asked.
/// </summary>
public sealed class ClinicalFlagApprovalTests(HospitalServer server)
{
    private const string Approvals = "/api/v1/approvals";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Dnr_is_put_on_only_by_a_doctor_and_taken_off_only_when_a_second_doctor_agrees()
    {
        var nurse = await server.SignInAsync(Users.Nurse);
        var doctor = await server.SignInAsync(Users.Doctor);
        var secondDoctor = await server.SignInAsync(Users.SecondDoctor);
        var patient = await LabVerificationTests.RegisterPatientAsync(server);
        var alerts = $"/api/v1/emr/patients/{patient.Id}/alerts";
        var dnr = new RaiseAlertRequest(PatientAlertType.Dnr, null, "Discussed with the son; consent form 12", null, null);

        // A nurse records other alerts, not a resuscitation decision.
        (await RefusalAsync(nurse, HttpMethod.Post, alerts, dnr)).ShouldBe((HttpStatusCode.Forbidden, "alert.dnr_doctor_only"));
        var fallRisk = await nurse.PostAsync<PatientAlertDto>(alerts, new RaiseAlertRequest(PatientAlertType.FallRisk, null, null, null, null));

        // An end date would take DNR off by itself.
        (await RefusalAsync(doctor, HttpMethod.Post, alerts, dnr with { ExpiresOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)) }))
            .ShouldBe((HttpStatusCode.BadRequest, null));
        var order = await doctor.PostAsync<PatientAlertDto>(alerts, dnr);
        order.IsActive.ShouldBeTrue("a doctor's DNR is on the banner at once");

        // Taking it off: never by a nurse; a doctor asks, and the banner keeps it until a second doctor agrees.
        (await RefusalAsync(nurse, HttpMethod.Post, $"/api/v1/emr/alerts/{order.Id}/deactivate", new DeactivateAlertRequest("Family changed their mind")))
            .ShouldBe((HttpStatusCode.Forbidden, "alert.dnr_doctor_only"));
        var asked = await AcceptedAsync(doctor, HttpMethod.Post, $"/api/v1/emr/alerts/{order.Id}/deactivate", new DeactivateAlertRequest("Family changed their mind"));
        asked.Kind.ShouldBe(PatientFlagApprovals.DnrKind);
        var flags = await nurse.GetAsync<PatientFlags>($"/api/v1/emr/patients/{patient.Id}/flags");
        flags.Alerts.ShouldContain(a => a.Id == order.Id, "the banner keeps DNR until a second doctor agrees");
        (await nurse.GetAsync<List<PatientAlertDto>>(alerts)).Single(a => a.Id == order.Id).PendingChange.ShouldNotBeNull().ApprovalId.ShouldBe(asked.Id);

        (await RefusalAsync(doctor, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        (await RefusalAsync(nurse, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"), "only a doctor agrees");
        var request = await secondDoctor.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}");
        (await secondDoctor.PostAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}/approve", new ApproveRequest("Agreed after speaking to the family", request.Version))).Status
            .ShouldBe(ApprovalStatus.Approved);
        (await nurse.GetAsync<List<PatientAlertDto>>(alerts)).ShouldNotContain(a => a.Id == order.Id);
        (await AuditAsync(patient.Id, "alert.dnr_lifted")).ShouldContain("approved by");

        // Other alerts are cleared at once, by the nurse too.
        (await nurse.PostAsync<PatientAlertDto>($"/api/v1/emr/alerts/{fallRisk.Id}/deactivate", new DeactivateAlertRequest("Mobilising well"))).IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task An_allergy_is_made_stricter_at_once_and_taken_out_of_the_checks_only_when_a_prescriber_who_did_not_ask_agrees()
    {
        var nurse = await server.SignInAsync(Users.Nurse);
        var doctor = await server.SignInAsync(Users.Doctor);
        var patient = await LabVerificationTests.RegisterPatientAsync(server);
        var peanuts = await nurse.PostAsync<AllergyDto>($"/api/v1/emr/patients/{patient.Id}/allergies",
            new RecordAllergyRequest("Peanuts", AllergyType.Food, "Hives", AllergySeverity.Moderate, null, null));

        // Raising the severity and changing the reaction: at once.
        var raised = await nurse.PutAsync<AllergyDto>($"/api/v1/emr/allergies/{peanuts.Id}",
            new UpdateAllergyRequest("Lip swelling", AllergySeverity.Severe, AllergyStatus.Active, null, null, peanuts.Version));
        (raised.Severity, raised.Reaction).ShouldBe((AllergySeverity.Severe, "Lip swelling"));

        // Entered in error: it waits, and the allergy stays active in the checks and on the banner.
        var asked = await AcceptedAsync(nurse, HttpMethod.Put, $"/api/v1/emr/allergies/{peanuts.Id}",
            new UpdateAllergyRequest("Lip swelling", AllergySeverity.Severe, AllergyStatus.EnteredInError, "Recorded on the wrong patient", "Checked with the ward", raised.Version));
        asked.Kind.ShouldBe(PatientFlagApprovals.AllergyKind);
        var listed = (await nurse.GetAsync<List<AllergyDto>>($"/api/v1/emr/patients/{patient.Id}/allergies")).ShouldHaveSingleItem();
        (listed.Status, listed.Notes, listed.PendingChange?.ApprovalId).ShouldBe((AllergyStatus.Active, "Checked with the ward", asked.Id));
        (await nurse.GetAsync<PatientFlags>($"/api/v1/emr/patients/{patient.Id}/flags")).Allergies.ShouldContain(a => a.Id == peanuts.Id);

        // The nurse does not decide it (a prescriber does), nor does anyone who asked.
        (await nurse.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}")).CanDecide.ShouldBeFalse("the nurse asked; a prescriber decides");
        var request = await doctor.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}");
        (await doctor.PostAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}/approve", new ApproveRequest("Confirmed", request.Version))).Status.ShouldBe(ApprovalStatus.Approved);
        (await nurse.GetAsync<List<AllergyDto>>($"/api/v1/emr/patients/{patient.Id}/allergies")).ShouldBeEmpty();
        (await AuditAsync(patient.Id, "allergy.changed_with_approval")).ShouldContain("approved by");
    }

    [Fact]
    public async Task A_lower_severity_waits_with_a_reason_and_is_not_made_over_an_allergy_changed_since()
    {
        var nurse = await server.SignInAsync(Users.Nurse);
        var doctor = await server.SignInAsync(Users.Doctor);
        var secondDoctor = await server.SignInAsync(Users.SecondDoctor);
        var patient = await LabVerificationTests.RegisterPatientAsync(server);
        var penicillin = await doctor.PostAsync<AllergyDto>($"/api/v1/emr/patients/{patient.Id}/allergies",
            new RecordAllergyRequest("Penicillin", AllergyType.Drug, "Rash", AllergySeverity.Severe, null, null));
        var path = $"/api/v1/emr/allergies/{penicillin.Id}";
        var lower = new UpdateAllergyRequest("Rash", AllergySeverity.Mild, AllergyStatus.Active, null, null, penicillin.Version);

        (await RefusalAsync(doctor, HttpMethod.Put, path, lower)).ShouldBe((HttpStatusCode.BadRequest, "validation"));
        var asked = await AcceptedAsync(doctor, HttpMethod.Put, path, lower with { ChangeReason = "Only a mild rash in 2019" });
        (await doctor.GetAsync<List<AllergyDto>>($"/api/v1/emr/patients/{patient.Id}/allergies")).Single().Severity.ShouldBe(AllergySeverity.Severe);
        (await RefusalAsync(doctor, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));

        // A nurse makes it stricter meanwhile, at once; the lower severity asked for is then not made.
        var current = (await nurse.GetAsync<List<AllergyDto>>($"/api/v1/emr/patients/{patient.Id}/allergies")).Single();
        (await nurse.PutAsync<AllergyDto>(path, new UpdateAllergyRequest("Wheeze", AllergySeverity.Anaphylaxis, AllergyStatus.Active, null, null, current.Version)))
            .Severity.ShouldBe(AllergySeverity.Anaphylaxis);
        var request = await secondDoctor.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}");
        (await RefusalAsync(secondDoctor, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, request.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "approval.stale"));
        (await doctor.GetAsync<List<AllergyDto>>($"/api/v1/emr/patients/{patient.Id}/allergies")).Single().Severity.ShouldBe(AllergySeverity.Anaphylaxis);
    }

    private async Task<string> AuditAsync(Guid patientId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId && e.Action == action)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => e.Summary ?? string.Empty)
            .FirstAsync(Ct);
    }

    private static async Task<ApprovalRequestDto> AcceptedAsync(ApiClient api, HttpMethod method, string path, object body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, method, path);
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, HttpMethod method, string path, object body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.IsSuccessStatusCode.ShouldBeFalse(await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }
}
