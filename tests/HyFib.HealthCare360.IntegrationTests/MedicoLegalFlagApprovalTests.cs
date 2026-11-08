using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A stay's medico-legal flag (maker-checker, slice 7), over the real API and PostgreSQL: the admission desk marks a stay
/// medico-legal, and links it to the MLC register entry, at once; clearing the flag, or unlinking it, waits for someone
/// who keeps the MLC register. Until then the stay stays medico-legal and the victim of a sexual offence is still treated
/// free (BNSS 2023, s.397).
/// </summary>
public sealed class MedicoLegalFlagApprovalTests(HospitalServer server)
{
    private const string Admissions = "/api/v1/inpatient/admissions";
    private const string Approvals = "/api/v1/approvals";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Clearing_a_stays_medico_legal_flag_waits_for_the_mlc_register_and_free_treatment_stays_until_then()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var billing = await server.SignInAsync(Users.Billing);
        var mrd = await server.SignInAsync(Users.MrdOfficer);
        var victim = await FreeTreatmentTests.ErCaseAsync(frontOffice, doctor, MlcCause.SexualAssault, Gender.Female);
        var reason = $"BNSS 2023 s.397, MLC no. {victim.Mlc.Number}";
        var stay = await AdmitAsync(frontOffice, doctor, victim.PatientId);
        try
        {
            // Linked to her MLC entry at once: the stay is free.
            var admitted = await frontOffice.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}");
            await NoContentAsync(frontOffice, $"{Admissions}/{stay.Id}", Edit(admitted) with { IsMlc = true, MlcNumber = victim.Mlc.Number });
            (await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{stay.Id}")).FreeTreatmentReason.ShouldBe(reason);

            // Cleared: a reason is needed, and it waits; the stay stays medico-legal and free meanwhile.
            var linked = await frontOffice.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}");
            var clear = Edit(linked) with { IsMlc = false, MlcNumber = null, Notes = "Moved to bay 2" };
            (await RefusalAsync(frontOffice, HttpMethod.Put, $"{Admissions}/{stay.Id}", clear)).ShouldBe((HttpStatusCode.BadRequest, "validation"));
            var asked = await AcceptedAsync(frontOffice, HttpMethod.Put, $"{Admissions}/{stay.Id}", clear with { ChangeReason = "Entered against the wrong stay" });
            asked.Kind.ShouldBe(MlcApprovals.Kind);
            asked.Details.ShouldContain(d => d.Label == "Free treatment");
            var waiting = await frontOffice.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}");
            (waiting.IsMlc, waiting.MlcNumber, waiting.Notes, waiting.PendingMlc?.ApprovalId).ShouldBe((true, victim.Mlc.Number, "Moved to bay 2", asked.Id));
            (await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{stay.Id}")).FreeTreatmentReason.ShouldBe(reason);

            // The desk does not decide it, nor does the doctor; the medical records officer, who keeps the register, does.
            (await RefusalAsync(frontOffice, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
                .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
            (await RefusalAsync(doctor, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
                .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
            var request = await mrd.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}");
            (await mrd.PostAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}/approve", new ApproveRequest("Checked against the register", request.Version))).Status
                .ShouldBe(ApprovalStatus.Approved);

            var cleared = await frontOffice.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}");
            cleared.IsMlc.ShouldBeFalse();
            cleared.MlcNumber.ShouldBeNull();
            cleared.PendingMlc.ShouldBeNull();
            (await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{stay.Id}")).FreeTreatmentReason.ShouldBeNull();
            (await AuditAsync(victim.PatientId, "admission.mlc_cleared")).ShouldContain("approved by");
        }
        finally
        {
            await CancelAdmissionAsync(frontOffice, await server.SignInAsync(Users.Nurse), stay.Id);
        }
    }

    private static UpdateAdmissionRequest Edit(AdmissionDetail a) => new(
        a.DepartmentId, a.ConsultantPractitionerId!.Value, a.ProvisionalDiagnosis, a.ExpectedDischargeDate, null, a.IsMlc, a.MlcNumber, a.AttendantName,
        a.AttendantRelation, a.AttendantPhone, a.Notes, a.Version);

    /// <summary>The front office admits the patient straight to a free bed of the women's general ward, under the doctor.</summary>
    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWF");
        var bed = await TestBeds.AddAsync(server, ward.Id);
        return await frontOffice.PostAsync<AdmissionCreated>(Admissions, new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Observation and treatment", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    /// <summary>The stay, made only for the test, is cancelled and its bed cleaned: the run's other tests read the same ward.</summary>
    private static async Task CancelAdmissionAsync(ApiClient frontOffice, ApiClient nurse, Guid admissionId)
    {
        var stay = await frontOffice.GetAsync<AdmissionDetail>($"{Admissions}/{admissionId}");
        using (var cancelled = await frontOffice.SendRawAsync(HttpMethod.Post, $"{Admissions}/{admissionId}/cancel", new CancelAdmissionRequest("Admitted only for the test")))
        {
            cancelled.EnsureSuccessStatusCode();
        }

        var task = (await nurse.GetAsync<List<HousekeepingTaskDto>>($"/api/v1/beds/housekeeping?wardId={stay.WardId}"))
            .Single(t => t.BedId == stay.BedId && t.AdmissionId == stay.Id);
        await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/start");
        await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/complete", new BedNoteRequest("Cleaned"));
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

    private static async Task NoContentAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Put, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
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
