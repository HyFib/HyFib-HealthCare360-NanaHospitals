using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emergency;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The front desk closes or cancels a visit from the Visits screen (a patient checked in by mistake, a visit left open):
/// the queue token goes with it. An in-patient's visit is the admission's and closes only with the stay. The OPD visit a
/// patient is admitted from closes with the admission; an emergency visit is the ER's to close, at its disposition. A stay
/// comes only from a visit the Admit screen offers: of its source, not cancelled, of the three days before it and none after.
/// </summary>
public sealed class VisitCloseTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_visit_checked_in_by_mistake_is_cancelled_with_its_token()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var visit = await CheckInAsync(frontOffice, await RegisterPatientAsync(frontOffice));
        visit.Token.ShouldNotBeNull();

        await frontOffice.PostAsync($"/api/v1/visits/{visit.Id}/cancel", new CancelVisitRequest("Checked in the wrong patient"));

        var cancelled = await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}");
        cancelled.Status.ShouldBe(VisitStatus.Cancelled);
        cancelled.CancelReason.ShouldBe("Checked in the wrong patient");
        cancelled.Token.ShouldNotBeNull().Status.ShouldBe(TokenStatus.Cancelled);

        using var again = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/visits/{visit.Id}/complete");
        (await CodeAsync(again)).ShouldBe("visit.cancelled");
    }

    [Fact]
    public async Task A_visit_left_open_is_closed()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var visit = await CheckInAsync(frontOffice, await RegisterPatientAsync(frontOffice));

        await frontOffice.PostAsync($"/api/v1/visits/{visit.Id}/complete");

        (await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}")).Status.ShouldBe(VisitStatus.Completed);
    }

    [Fact]
    public async Task An_in_patients_visit_closes_only_with_the_stay()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var admission = await AdmitAsync(frontOffice, doctor, await RegisterPatientAsync(frontOffice));
        try
        {
            using (var cancel = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/visits/{admission.VisitId}/cancel", new CancelVisitRequest("Wrong patient")))
            {
                (await CodeAsync(cancel)).ShouldBe("visit.inpatient");
            }

            using (var complete = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/visits/{admission.VisitId}/complete"))
            {
                (await CodeAsync(complete)).ShouldBe("visit.inpatient");
            }

            (await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{admission.VisitId}")).Status.ShouldNotBeOneOf(VisitStatus.Cancelled, VisitStatus.Completed);
        }
        finally
        {
            // A cancelled admission sends the bed for cleaning, so the suite stays out of use for the rest of the run.
            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            await admin.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted for a test of closing visits"));
        }
    }

    [Fact]
    public async Task A_patient_admitted_from_the_opd_has_the_visit_closed_with_its_token_and_can_be_checked_in_again_the_same_day()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patientId = await RegisterPatientAsync(frontOffice);

        // The doctor takes the patient in from the queue and advises admission; the desk admits him from that visit.
        var visit = await CheckInAsync(frontOffice, patientId);
        (await doctor.PostAsync<QueueTokenDto>($"/api/v1/queue/{visit.Token.ShouldNotBeNull().Id}/start")).Status.ShouldBe(TokenStatus.InService);
        (await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}")).Status.ShouldBe(VisitStatus.InProgress);
        var admission = await AdmitAsync(frontOffice, doctor, patientId, sourceVisitId: visit.Id);
        try
        {
            // The visit ends with the admission, and its token with it: neither is left in consultation.
            var closed = await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}");
            closed.Status.ShouldBe(VisitStatus.Completed);
            closed.CompletedAt.ShouldNotBeNull();
            closed.Token.ShouldNotBeNull().Status.ShouldBe(TokenStatus.Completed);

            // So it no longer stands in the way of checking him in again the same day, in the same department.
            var again = await CheckInAsync(frontOffice, patientId);
            again.Status.ShouldBe(VisitStatus.Open);
            await frontOffice.PostAsync($"/api/v1/visits/{again.Id}/cancel", new CancelVisitRequest("Checked in again only to see that the desk may"));
        }
        finally
        {
            await CancelAdmissionAsync(admission.Id);
        }
    }

    [Fact]
    public async Task An_admission_entered_late_cannot_come_from_a_visit_checked_in_after_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patientId = await RegisterPatientAsync(frontOffice);

        // The desk enters an admission made two hours ago, naming the visit the patient was checked in to just now.
        var visit = await CheckInAsync(frontOffice, patientId);
        using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/inpatient/admissions",
            await AdmissionAsync(frontOffice, doctor, patientId, AdmissionSource.OutPatient, visit.Id, DateTimeOffset.UtcNow.AddHours(-2)));
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        (problem.GetProperty("code").GetString(), problem.GetProperty("detail").GetString()).ShouldBe(("admission.source_visit_after_admission",
            $"Visit {visit.VisitNumber} was checked in after the admission time, so the patient cannot have been admitted from it. "
            + "Choose the visit the admission was advised at, or correct the admission time."));

        // Nothing is admitted, and the visit is left open.
        (await frontOffice.GetAsync<List<PatientAdmissionItem>>($"/api/v1/patients/{patientId}/admissions")).ShouldBeEmpty();
        (await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}")).Status.ShouldBe(VisitStatus.Open);
    }

    [Fact]
    public async Task An_admission_comes_only_from_a_visit_of_its_source_that_was_not_cancelled()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patientId = await RegisterPatientAsync(frontOffice);
        var visit = await CheckInAsync(frontOffice, patientId);

        // An OPD visit is not the casualty visit an admission "From casualty / ER" comes from.
        using (var fromEr = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/inpatient/admissions",
                   await AdmissionAsync(frontOffice, doctor, patientId, AdmissionSource.Emergency, visit.Id, null)))
        {
            (await CodeAsync(fromEr)).ShouldBe("admission.source_visit_type");
        }

        // Nor is a visit cancelled as checked in by mistake the one an admission advised in OPD comes from.
        await frontOffice.PostAsync($"/api/v1/visits/{visit.Id}/cancel", new CancelVisitRequest("Checked in by mistake"));
        using (var cancelled = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/inpatient/admissions",
                   await AdmissionAsync(frontOffice, doctor, patientId, AdmissionSource.OutPatient, visit.Id, null)))
        {
            (await CodeAsync(cancelled)).ShouldBe("admission.source_visit_cancelled");
        }

        (await frontOffice.GetAsync<List<PatientAdmissionItem>>($"/api/v1/patients/{patientId}/admissions")).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_emergency_visit_the_patient_is_admitted_from_is_left_for_the_er_to_close_at_its_disposition()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);

        // The casualty nurse triages him, and the desk admits him from the casualty visit.
        var triage = (await nurse.GetAsync<ErBoard>("/api/v1/emergency/board")).Rows.Single(r => r.VisitId == visit.Id);
        await nurse.PostAsync<EmergencyCaseDetail>($"/api/v1/emergency/cases/{triage.CaseId}/triage", EmergencyDepartmentTests.Triage(triage.Version));
        var admission = await AdmitAsync(frontOffice, doctor, patientId, visit.Id, AdmissionSource.Emergency);
        try
        {
            // Admitted before the doctor recorded where he goes: the case stays on the ER board, its visit open.
            var row = (await doctor.GetAsync<ErBoard>("/api/v1/emergency/board")).Rows.Single(r => r.VisitId == visit.Id);
            (await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}")).Status.ShouldBe(VisitStatus.Open);

            // The ER's disposition closes it, once.
            await doctor.PostAsync<EmergencyCaseDetail>($"/api/v1/emergency/cases/{row.CaseId}/disposition",
                new DispositionRequest(EmergencyDisposition.Admitted, "Admitted to the ward for observation", null, row.Version));
            (await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}")).Status.ShouldBe(VisitStatus.Completed);
        }
        finally
        {
            await CancelAdmissionAsync(admission.Id);
        }
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    /// <summary>Front office: checks the patient in to see the doctor, with a queue token.</summary>
    private async Task<VisitDetail> CheckInAsync(ApiClient frontOffice, Guid patientId)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var departmentId = (await frontOffice.GetAsync<JsonElement>($"/api/v1/masters/practitioners/{practitionerId}")).GetProperty("departmentId").GetGuid();
        return await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.OutPatient, departmentId, practitionerId, null, PayerType.Self, null, ReferralSource.Self, null, "Fever",
            false, null, null, TokenPriority.Normal, IssueToken: true, NotifyPatient: false));
    }

    /// <summary>A new patient; the random surname, date of birth and mobile number keep the duplicate check quiet.</summary>
    private static async Task<Guid> RegisterPatientAsync(ApiClient frontOffice)
    {
        var surname = Letters(8);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Ravi"), MiddleName: null, LastName: surname[0] + surname[1..].ToLowerInvariant(), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 10)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        return created.Id;
    }

    /// <summary>
    /// Front office: admits the patient under the doctor to a free deluxe suite, a ward no other test admits to, from the
    /// visit the admission was advised at when there is one (an OPD visit, unless <paramref name="source"/> says otherwise).
    /// </summary>
    private async Task<AdmissionCreated> AdmitAsync(
        ApiClient frontOffice, ApiClient doctor, Guid patientId, Guid? sourceVisitId = null, AdmissionSource source = AdmissionSource.OutPatient) =>
        await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions",
            await AdmissionAsync(frontOffice, doctor, patientId, source, sourceVisitId, null));

    /// <summary>
    /// The admission of the patient to a free deluxe suite, from the source and the visit named, at the time given (now when
    /// none). One from the casualty is an emergency admission; any other is elective.
    /// </summary>
    private async Task<AdmitPatientRequest> AdmissionAsync(
        ApiClient frontOffice, ApiClient doctor, Guid patientId, AdmissionSource source, Guid? sourceVisitId, DateTimeOffset? admittedAt)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "DLX");
        var bed = await TestBeds.AddAsync(server, ward.Id);
        return new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: source == AdmissionSource.Emergency ? AdmissionType.Emergency : AdmissionType.Elective,
            Source: source, SourceVisitId: sourceVisitId,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Cellulitis of the right leg", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: admittedAt, Notes: null);
    }

    /// <summary>
    /// The administrator cancels an admission made for a test of closing visits, and housekeeping cleans the suite, which
    /// is vacant again: there are few of them.
    /// </summary>
    private async Task CancelAdmissionAsync(Guid admissionId)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var stay = await admin.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admissionId}");
        await admin.PostAsync($"/api/v1/inpatient/admissions/{admissionId}/cancel", new CancelAdmissionRequest("Admitted for a test of closing visits"));
        var task = (await nurse.GetAsync<List<HousekeepingTaskDto>>($"/api/v1/beds/housekeeping?wardId={stay.WardId}"))
            .Single(t => t.BedId == stay.BedId && t.AdmissionId == stay.Id);
        await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/start");
        (await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/complete", new BedNoteRequest("Cleaned")))
            .Status.ShouldBe(HousekeepingStatus.Completed);
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
