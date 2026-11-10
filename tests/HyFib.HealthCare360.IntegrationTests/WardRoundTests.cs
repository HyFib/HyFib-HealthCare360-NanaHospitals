using System.Text.Json;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Inpatient.Features.Rounds;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The doctor's ward round (EMR-01 "IP rounds") over the real API, for what the in-patient journey does not cover: a
/// restricted (VIP) patient keeps their name and diagnosis off the round, as off the bed board, while the bed, the UHID
/// and the round's reminders stay. The test admits to the female general ward, which the journeys don't use, and
/// cancels the admission at the end.
/// </summary>
public sealed class WardRoundTests(HospitalServer server)
{
    [Fact]
    public async Task A_restricted_patient_is_on_the_round_without_their_name_or_diagnosis()
    {
        // The hospital administrator may open restricted records, so registers and admits the patient.
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var patient = await admin.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName("Kavitha"), MiddleName: null, LastName: "Round" + Letters(8), Gender: Gender.Female,
            DateOfBirth: null, AgeYears: 47, AgeMonths: null, Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null,
            PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: true,
            Notes: null, ConfirmedNotDuplicate: false));
        var ward = (await admin.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWF");
        var bed = await TestBeds.AddAsync(admin, ward.Id);
        var admission = await admin.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient.Id, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Emergency, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Acute pancreatitis", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null, IsMlc: false,
            MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null, AdmittedAt: null,
            Notes: null, NotifyPatient: false));

        try
        {
            var onRound = (await doctor.GetAsync<WardRound>("/api/v1/inpatient/rounds")).Patients.Single(p => p.AdmissionId == admission.Id);
            onRound.IsRestricted.ShouldBeTrue();
            onRound.PatientName.ShouldBe("Restricted patient");
            onRound.ProvisionalDiagnosis.ShouldBeNull();
            onRound.Uhid.ShouldBe(patient.Uhid, "The UHID and the bed still say whom to see.");
            onRound.BedLabel.ShouldBe(admission.BedLabel);
            onRound.WardName.ShouldBe(ward.Name);
            onRound.DayOfStay.ShouldBe(1);
            onRound.SeenToday.ShouldBeFalse();
            onRound.IsPrimaryConsultant.ShouldBeTrue();
        }
        finally
        {
            await admin.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted for a test of the ward round"));
        }
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
