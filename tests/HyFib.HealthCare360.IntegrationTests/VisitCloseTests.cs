using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// the queue token goes with it. An in-patient's visit is the admission's and closes only with the stay.
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
            // The general ward's beds are shared by every test in the run.
            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            await admin.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted for a test of closing visits"));
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

    /// <summary>Front office: admits the patient under the doctor to a free bed of the male general ward.</summary>
    private static async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWM");
        var bed = (await frontOffice.GetAsync<List<AvailableBedDto>>($"/api/v1/beds/available?patientId={patientId}&wardId={ward.Id}"))
            .First(b => b.IsEligible && b.Status == BedStatus.Vacant);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Cellulitis of the right leg", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
