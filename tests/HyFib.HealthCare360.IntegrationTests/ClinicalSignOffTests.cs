using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Who signs off another doctor's work (maker-checker, slice 15), over the real API and PostgreSQL. Each rule is strict by
/// default, and relaxed only through its setting, which a second administrator approves:
/// <list type="bullet">
/// <item>a discharge summary is signed by the stay's consultant or a doctor on its care team, as themselves;</item>
/// <item>another doctor's signed encounter is amended only when its author or the head of its department approves, the
/// original kept; its author amends at once;</item>
/// <item>another doctor's critical result is acknowledged only by a doctor on the same stay's care team or one they handed
/// over to.</item>
/// </list>
/// </summary>
public sealed class ClinicalSignOffTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_discharge_summary_is_signed_only_by_the_consultant_or_a_doctor_on_the_care_team()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var doctor = await server.SignInAsync(Users.Doctor);
        var second = await server.SignInAsync(Users.SecondDoctor);
        var third = await server.SignInAsync(Users.ThirdDoctor);
        var (patient, admission) = await AdmitAsync(admin, await PractitionerAsync(doctor));

        try
        {
            var draft = await second.PutAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/by-admission/{admission.Id}", Draft(patient.Id, null));

            // Neither the second nor the third doctor is on the stay's care team: they draft, but do not sign.
            (await RefusalAsync(second, HttpMethod.Post, $"/api/v1/clinical/discharge-summaries/{draft.Id}/sign", new SignDischargeSummaryRequest(draft.Version)))
                .ShouldBe((HttpStatusCode.Forbidden, "discharge_summary.not_care_team"));

            // A cross-consultation puts the second doctor on the care team; the summary is signed in their name, not the consultant's.
            await second.PostAsync<JsonElement>($"/api/v1/inpatient/admissions/{admission.Id}/consultant-visits", new RecordConsultantVisitRequest(null, null, "Cross-consult: cardiology"));
            (await RefusalAsync(third, HttpMethod.Post, $"/api/v1/clinical/discharge-summaries/{draft.Id}/sign", new SignDischargeSummaryRequest(draft.Version)))
                .ShouldBe((HttpStatusCode.Forbidden, "discharge_summary.not_care_team"));
            var signed = await second.PostAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/{draft.Id}/sign", new SignDischargeSummaryRequest(draft.Version));
            signed.Status.ShouldBe(DischargeSummaryStatus.Signed);
            signed.SignedByPractitionerId.ShouldBe(await PractitionerAsync(second));
        }
        finally
        {
            await admin.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted for a test of the discharge summary"));
        }
    }

    [Fact]
    public async Task Where_the_hospital_lets_any_doctor_sign_a_doctor_off_the_care_team_signs()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var doctor = await server.SignInAsync(Users.Doctor);
        var third = await server.SignInAsync(Users.ThirdDoctor);
        var (patient, admission) = await AdmitAsync(admin, await PractitionerAsync(doctor));
        var facility = await FacilityAsync(doctor);
        await SetAsync(ClinicalSignOff.DischargeSummaryCareTeamSetting, false, facility);

        try
        {
            var draft = await third.PutAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/by-admission/{admission.Id}", Draft(patient.Id, null));
            (await third.PostAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/{draft.Id}/sign", new SignDischargeSummaryRequest(draft.Version)))
                .Status.ShouldBe(DischargeSummaryStatus.Signed);
        }
        finally
        {
            await RemoveAsync(ClinicalSignOff.DischargeSummaryCareTeamSetting, facility);
            await admin.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted for a test of the discharge summary"));
        }
    }

    [Fact]
    public async Task Relaxing_a_sign_off_rule_waits_for_a_second_administrator()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        using var asked = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/admin/settings/{ClinicalSignOff.CriticalResultCoveringSetting}",
            new SaveSettingRequest(System.Text.Json.Nodes.JsonValue.Create(false), null, false, null, null, "Night duty is short of doctors"));
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, "switching the rule off loosens it");
        var request = await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct);
        request!.Kind.ShouldBe(SettingGuard.ChangeKind);
        await admin.PostAsync($"/api/v1/approvals/{request.Id}/withdraw", new WithdrawApprovalRequest(request.Version));
    }

    [Fact]
    public async Task Another_doctors_signed_encounter_is_amended_only_with_the_authors_or_the_head_of_departments_approval_keeping_the_original()
    {
        var doctor = await server.SignInAsync(Users.Doctor);
        var second = await server.SignInAsync(Users.SecondDoctor);
        var third = await server.SignInAsync(Users.ThirdDoctor);
        var patient = await LabVerificationTests.RegisterPatientAsync(server);
        var encounter = await SignedEncounterAsync(doctor, patient.Id);

        // The author amends at once.
        var own = await doctor.PostAsync<EncounterDetail>($"/api/v1/emr/encounters/{encounter.Id}/amend", Amend("Typo in the plan", "Paracetamol 650 mg SOS", encounter.Version));
        own.Amendments.Count.ShouldBe(1);

        // Another doctor asks; the note stays as it is until someone who may decide agrees.
        var asked = await AcceptedAsync(second, $"/api/v1/emr/encounters/{encounter.Id}/amend", Amend("Added the ECG finding", "ECG: sinus tachycardia", own.Version));
        asked.Kind.ShouldBe(EncounterService.AmendKind);
        (await doctor.GetAsync<EncounterDetail>($"/api/v1/emr/encounters/{encounter.Id}")).Plan.ShouldBe("Paracetamol 650 mg SOS");
        (await RefusalAsync(second, HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));

        // A third doctor is neither the author nor the head of the department: refused, and the request still waits.
        var waiting = await third.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");
        (await RefusalAsync(third, HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, waiting.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "encounter.amend_approver"));
        (await third.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}")).Status.ShouldBe(ApprovalStatus.Pending);

        // The author approves: the amendment is the second doctor's, the original is kept in the history.
        var approved = await MakerChecker.ApproveAsync(doctor, await doctor.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}"));
        approved.Outcome.ShouldNotBeNull().ShouldContain("original is kept");
        var amended = await doctor.GetAsync<EncounterDetail>($"/api/v1/emr/encounters/{encounter.Id}");
        amended.Plan.ShouldBe("ECG: sinus tachycardia");
        amended.Amendments.Count.ShouldBe(2);
        amended.AmendmentReason.ShouldNotBeNull().ShouldContain("approved by");
        (await AuditAsync(patient.Id, "encounter.amended_with_approval")).ShouldContain("approved by");

        // The head of the encounter's department decides too.
        var again = await AcceptedAsync(second, $"/api/v1/emr/encounters/{encounter.Id}/amend", Amend("Follow-up date", "Review in a week", amended.Version));
        var previousHead = await SetDepartmentHeadAsync(encounter.Id, await PractitionerAsync(third));
        try
        {
            (await MakerChecker.ApproveAsync(third, await third.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{again.Id}"))).Status.ShouldBe(ApprovalStatus.Approved);
            (await doctor.GetAsync<EncounterDetail>($"/api/v1/emr/encounters/{encounter.Id}")).Amendments.Count.ShouldBe(3);
        }
        finally
        {
            await SetDepartmentHeadAsync(encounter.Id, previousHead);
        }
    }

    [Fact]
    public async Task Another_doctors_critical_result_is_acknowledged_by_the_covering_doctor_on_a_handover_only()
    {
        var doctor = await server.SignInAsync(Users.Doctor);
        var second = await server.SignInAsync(Users.SecondDoctor);
        var patient = await LabVerificationTests.RegisterPatientAsync(server);
        var critical = await ResultAsync(doctor, patient.Id, critical: true);
        var routine = await ResultAsync(doctor, patient.Id, critical: false);

        // A routine result is anyone's to acknowledge; a critical one is the ordering doctor's, or their cover's.
        using (var ok = await second.SendRawAsync(HttpMethod.Post, $"/api/v1/emr/results/{routine}/acknowledge", new AcknowledgeResultRequest(null)))
        {
            ok.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await RefusalAsync(second, HttpMethod.Post, $"/api/v1/emr/results/{critical}/acknowledge", new AcknowledgeResultRequest("Seen")))
            .ShouldBe((HttpStatusCode.Forbidden, "result.not_covering"));

        // The ordering doctor hands this patient over to the second doctor for the night.
        var handover = await doctor.PostAsync<DoctorHandoverDto>("/api/v1/emr/handovers",
            new RecordHandoverRequest(await PractitionerAsync(second), DateTimeOffset.UtcNow.AddHours(12), patient.Id, "Night cover"));
        (await second.GetAsync<List<DoctorHandoverDto>>("/api/v1/emr/handovers")).ShouldContain(h => h.Id == handover.Id);
        using (var covered = await second.SendRawAsync(HttpMethod.Post, $"/api/v1/emr/results/{critical}/acknowledge", new AcknowledgeResultRequest("Potassium repeated, treated")))
        {
            covered.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await AuditAsync(patient.Id, "result.acknowledged_covering")).ShouldContain("handover");

        // Once the handover ends, it covers nothing.
        await doctor.PostAsync<DoctorHandoverDto>($"/api/v1/emr/handovers/{handover.Id}/end", new EndHandoverRequest(handover.Version));
        var next = await ResultAsync(doctor, patient.Id, critical: true);
        (await RefusalAsync(second, HttpMethod.Post, $"/api/v1/emr/results/{next}/acknowledge", new AcknowledgeResultRequest(null)))
            .ShouldBe((HttpStatusCode.Forbidden, "result.not_covering"));
    }

    [Fact]
    public async Task A_doctor_on_the_same_stays_care_team_acknowledges_the_consultants_critical_result()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var doctor = await server.SignInAsync(Users.Doctor);
        var second = await server.SignInAsync(Users.SecondDoctor);
        var (patient, admission) = await AdmitAsync(admin, await PractitionerAsync(doctor));
        try
        {
            var critical = await ResultAsync(doctor, patient.Id, critical: true);
            (await RefusalAsync(second, HttpMethod.Post, $"/api/v1/emr/results/{critical}/acknowledge", new AcknowledgeResultRequest(null)))
                .ShouldBe((HttpStatusCode.Forbidden, "result.not_covering"));
            await second.PostAsync<JsonElement>($"/api/v1/inpatient/admissions/{admission.Id}/consultant-visits", new RecordConsultantVisitRequest(null, null, "Round for the consultant"));
            using var acknowledged = await second.SendRawAsync(HttpMethod.Post, $"/api/v1/emr/results/{critical}/acknowledge", new AcknowledgeResultRequest(null));
            acknowledged.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
        finally
        {
            await admin.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted for a test of result acknowledgement"));
        }
    }

    internal static async Task<(PatientCreated Patient, AdmissionCreated Admission)> AdmitAsync(ApiClient admin, Guid consultantId, string diagnosis = "Community-acquired pneumonia")
    {
        var patient = await admin.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName("Anitha"), MiddleName: null, LastName: "Signoff" + Letters(8), Gender: Gender.Female,
            DateOfBirth: null, AgeYears: 52, AgeMonths: null, Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null,
            PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false,
            Notes: null, ConfirmedNotDuplicate: false));
        var ward = (await admin.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWF");
        var bed = await TestBeds.AddAsync(admin, ward.Id);
        var admission = await admin.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient.Id, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Emergency, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: diagnosis, ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null, IsMlc: false,
            MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null, AdmittedAt: null,
            Notes: null, NotifyPatient: false));
        return (patient, admission);
    }

    /// <summary>A released report waiting for review, ordered by the doctor signed in as <paramref name="orderedBy"/>.</summary>
    internal async Task<Guid> ResultAsync(ApiClient orderedBy, Guid patientId, bool critical, string title = "Serum potassium 6.9 mmol/L")
    {
        var me = await orderedBy.GetAsync<JsonElement>("/api/v1/auth/me");
        var review = new ResultReview
        {
            FacilityId = me.GetProperty("facilityId").GetGuid(),
            PatientId = patientId,
            ReportId = Guid.CreateVersion7(),
            Kind = DiagnosticKind.Laboratory,
            Title = title,
            HasCriticalFindings = critical,
            ReleasedAt = DateTimeOffset.UtcNow,
            PractitionerId = me.GetProperty("practitionerId").GetGuid(),
        };
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Add(review);
        await db.SaveChangesAsync(Ct);
        return review.Id;
    }

    private static async Task<EncounterDetail> SignedEncounterAsync(ApiClient doctor, Guid patientId)
    {
        var started = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(patientId, null, null, EncounterType.OutPatient, null, null, null, "Palpitations"));
        var written = await doctor.PutAsync<EncounterDetail>($"/api/v1/emr/encounters/{started.Id}", new UpdateEncounterRequest(
            "Palpitations", "Two days", "Pulse 112/min", "Sinus tachycardia, ?anxiety", "Reassure; review", null, null, null, null, null, null, started.Version));
        return await doctor.PostAsync<EncounterDetail>($"/api/v1/emr/encounters/{started.Id}/sign", new SignEncounterRequest(written.Version));
    }

    private static AmendEncounterRequest Amend(string reason, string plan, uint version) => new(
        reason, "Palpitations", "Two days", "Pulse 112/min", "Sinus tachycardia, ?anxiety", plan, null, null, null, null, null, null, version);

    private static SaveDischargeSummaryRequest Draft(Guid patientId, uint? version) => new(
        patientId, null, null, null, null, "Community-acquired pneumonia", [], "Fever and cough", "Treated with IV antibiotics; afebrile for 48 hours", null, null,
        "Stable, ambulant", "Amoxicillin-clavulanate 625 mg BD x 5 days", "Plenty of fluids", null, null, version);

    private async Task<Guid?> SetDepartmentHeadAsync(Guid encounterId, Guid? headPractitionerId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var departmentId = await db.Set<Encounter>().IgnoreQueryFilters().Where(e => e.Id == encounterId).Select(e => e.DepartmentId).SingleAsync(Ct);
        var department = await db.Set<Department>().IgnoreQueryFilters().SingleAsync(d => d.Id == departmentId, Ct);
        var previous = department.HeadPractitionerId;
        department.HeadPractitionerId = headPractitionerId;
        await db.SaveChangesAsync(Ct);
        return previous;
    }

    private async Task SetAsync(string key, bool value, Guid facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(key, value, facilityId, cancellationToken: Ct);
    }

    private async Task RemoveAsync(string key, Guid facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(key, facilityId, Ct);
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

    internal static async Task<Guid> PractitionerAsync(ApiClient user) => (await user.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();

    private static async Task<Guid> FacilityAsync(ApiClient user) => (await user.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();

    private static async Task<ApprovalRequestDto> AcceptedAsync(ApiClient client, string path, object body)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct))!;
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient client, HttpMethod method, string path, object body)
    {
        using var response = await client.SendRawAsync(method, path, body);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (response.StatusCode, problem.ValueKind == JsonValueKind.Object && problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
