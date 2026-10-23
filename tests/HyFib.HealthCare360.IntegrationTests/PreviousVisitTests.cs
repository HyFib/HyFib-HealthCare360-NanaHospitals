using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Features.Vitals;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The previous visit summary of the doctor's encounter workspace (EMR-07), through the API: at the next consultation
/// the doctor sees the patient's encounter before this one — when and with whom, what was found and decided, what was
/// prescribed and the vitals then — and nothing for a patient's first encounter.
/// </summary>
public sealed class PreviousVisitTests(HospitalServer server)
{
    private const string Encounters = "/api/v1/emr/encounters";

    [Fact]
    public async Task The_next_encounter_shows_what_was_found_decided_prescribed_and_measured_at_the_one_before()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var today = server.Services.GetRequiredService<TimeProvider>().Today();
        var patientId = await PatientAsync();

        // The first consultation: a fever, measured, examined and diagnosed; paracetamol given and a review advised.
        var first = await doctor.PostAsync<EncounterDetail>(Encounters, new StartEncounterRequest(
            patientId, VisitId: null, AdmissionId: null, EncounterType.OutPatient, DepartmentId: null, PractitionerId: null, TemplateId: null,
            ChiefComplaint: "Fever for three days"));
        await nurse.PostAsync<VitalsDto>($"/api/v1/emr/patients/{patientId}/vitals", new RecordVitalsRequest(
            first.Id, RecordedAt: null, Temperature: 38.9m, TemperatureUnit.Celsius, Pulse: 108, RespiratoryRate: 18, SystolicBp: 126, DiastolicBp: 82,
            SpO2: 97, OnSupplementalOxygen: false, HeightCm: null, WeightKg: 64m, PainScore: 3, BloodGlucose: null, GlucoseTiming.Random, Gcs: null,
            Notes: null));
        first = await doctor.PutAsync<EncounterDetail>($"{Encounters}/{first.Id}", new UpdateEncounterRequest(
            ChiefComplaint: "Fever for three days", HistoryOfPresentIllness: "Evening fever with body ache.", Examination: "Throat congested.",
            Assessment: "Acute febrile illness, probably viral.", Plan: "Paracetamol, fluids and rest.", FollowUpDate: today.AddDays(5),
            FollowUpAdvice: "Come back if the fever lasts beyond two more days.", ReferralDepartmentId: null, ReferralPractitionerId: null,
            ReferralExternal: null, ReferralReason: null, first.Version));
        await doctor.PostAsync<DiagnosisDto>($"{Encounters}/{first.Id}/diagnoses",
            new AddDiagnosisRequest("R50.9", Title: null, DiagnosisType.Provisional, IsPrimary: true, OnsetDate: null, DiagnosisStatus.Active, Notes: null));
        await doctor.PostAsync<DiagnosisDto>($"{Encounters}/{first.Id}/diagnoses",
            new AddDiagnosisRequest("A90", Title: null, DiagnosisType.Differential, IsPrimary: false, OnsetDate: null, DiagnosisStatus.Active, Notes: null));
        var paracetamol = await DrugAsync(doctor, "Paracetamol", "DRG-PCM500");
        var given = await PrescribeAsync(doctor, patientId, first.Id, paracetamol, "1 tablet", DoseFrequency.TDS, 5);
        await doctor.PostAsync<SafetyCheckResponse>($"/api/v1/clinical/prescriptions/{given.Id}/check");
        (await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{given.Id}/sign",
                new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: false, ConfirmControlledDrugs: false, given.Version)))
            .Prescription.Status.ShouldBe(PrescriptionStatus.Signed);
        // A prescription drafted and never signed is not what the patient was given.
        await PrescribeAsync(doctor, patientId, first.Id, await DrugAsync(doctor, "Pantoprazole", "DRG-PANTO40"), "1 tablet", DoseFrequency.OD, 5);
        (await doctor.PostAsync<EncounterDetail>($"{Encounters}/{first.Id}/sign", new SignEncounterRequest(first.Version)))
            .Status.ShouldBe(EncounterStatus.Signed);

        // The patient comes back, afebrile and with a sore throat: the new encounter's previous visit is the first one, in
        // full, with what was found and measured then and not today.
        var second = await doctor.PostAsync<EncounterDetail>(Encounters, new StartEncounterRequest(
            patientId, VisitId: null, AdmissionId: null, EncounterType.OutPatient, DepartmentId: null, PractitionerId: null, TemplateId: null,
            ChiefComplaint: "Review of the fever"));
        await nurse.PostAsync<VitalsDto>($"/api/v1/emr/patients/{patientId}/vitals", new RecordVitalsRequest(
            second.Id, RecordedAt: null, Temperature: 36.9m, TemperatureUnit.Celsius, Pulse: 82, RespiratoryRate: 16, SystolicBp: 120, DiastolicBp: 78,
            SpO2: 98, OnSupplementalOxygen: false, HeightCm: null, WeightKg: 64m, PainScore: 1, BloodGlucose: null, GlucoseTiming.Random, Gcs: null,
            Notes: null));
        await doctor.PostAsync<DiagnosisDto>($"{Encounters}/{second.Id}/diagnoses",
            new AddDiagnosisRequest("J02.9", Title: null, DiagnosisType.Provisional, IsPrimary: true, OnsetDate: null, DiagnosisStatus.Active, Notes: null));
        var previous = (await doctor.GetAsync<PreviousVisitResponse>($"{Encounters}/{second.Id}/previous")).Previous.ShouldNotBeNull();
        previous.EncounterId.ShouldBe(first.Id);
        (previous.Type, previous.Status, previous.StartedAt).ShouldBe((EncounterType.OutPatient, EncounterStatus.Signed, first.StartedAt));
        previous.PractitionerName.ShouldBe(first.PractitionerName);
        previous.ChiefComplaint.ShouldBe("Fever for three days");
        previous.Diagnoses.ShouldBe(["R50.9 Fever, unspecified"], "Dengue was only considered: a differential diagnosis is not what was found.");
        previous.Assessment.ShouldBe("Acute febrile illness, probably viral.");
        previous.Plan.ShouldBe("Paracetamol, fluids and rest.");
        (previous.FollowUpDate, previous.FollowUpAdvice).ShouldBe((today.AddDays(5), "Come back if the fever lasts beyond two more days."));
        var medicine = previous.Medicines.ShouldHaveSingleItem();
        (medicine.DrugName, medicine.Strength, medicine.Dose, medicine.Frequency, medicine.DurationDays)
            .ShouldBe((paracetamol.Name, paracetamol.Strength, "1 tablet", "TDS", 5));
        var vitals = previous.Vitals.ShouldNotBeNull();
        (vitals.TemperatureCelsius, vitals.Pulse, vitals.SystolicBp, vitals.DiastolicBp).ShouldBe((38.9m, 108, 126, 82));
        vitals.Flags.ShouldContain(f => f.Parameter == "temperature" && f.Message == "Fever", "38.9 °C is flagged as it was at the visit.");

        // The first encounter had none before it.
        (await doctor.GetAsync<PreviousVisitResponse>($"{Encounters}/{first.Id}/previous")).Previous.ShouldBeNull();
    }

    private static async Task<PrescribableDrug> DrugAsync(ApiClient doctor, string search, string code) =>
        (await doctor.GetAsync<List<PrescribableDrug>>($"/api/v1/emr/drugs?search={search}")).Single(d => d.Code == code);

    private static Task<PrescriptionDto> PrescribeAsync(
        ApiClient doctor, Guid patientId, Guid encounterId, PrescribableDrug drug, string dose, DoseFrequency frequency, int days) =>
        doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patientId, encounterId, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    drug.Id, drug.Name, drug.GenericName, drug.Strength, drug.DosageForm, Route: "Oral", dose, frequency, CustomFrequency: null,
                    DosesPerDay: null, days, Quantity: null, Instructions: "After food", drug.IsControlled, drug.ControlledSchedule),
            ]));

    private async Task<Guid> PatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);

        // A patient of this test alone: the unique names and mobile number keep the duplicate check quiet.
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Suresh"), MiddleName: null, LastName: "Review" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1979, 1, 1).AddDays(Random.Shared.Next(365 * 10)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
        return created.Id;
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
