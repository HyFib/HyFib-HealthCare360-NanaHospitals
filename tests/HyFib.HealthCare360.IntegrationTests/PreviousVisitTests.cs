using System.Globalization;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Features.Vitals;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The previous visit summary on the encounter workspace (EMR-02..07), over the real API and database: while the doctor
/// writes an encounter, its previous visit is the patient's latest signed encounter (signed, or amended since) that started
/// before it, at any facility of the hospital. Never the encounter itself, a later one, one still being written, or another
/// patient's; with no earlier signed encounter there is none. The doctor reads it in full: when and with whom, what was
/// found and decided, what was prescribed and the vitals then.
/// </summary>
public sealed class PreviousVisitTests(HospitalServer server)
{
    private const string Encounters = "/api/v1/emr/encounters";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Is_the_latest_signed_encounter_before_this_one()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patient = await RegisterAsync();
        var someoneElse = await RegisterAsync();

        // Dengue, diagnosed and signed; its follow-up, signed and later amended; then a visit whose note was never signed.
        var dengue = await StartAsync(patient, "Fever for two days with body ache");
        await doctor.PostAsync<DiagnosisDto>($"{Encounters}/{dengue.Id}/diagnoses",
            new AddDiagnosisRequest("A90", null, DiagnosisType.Final, IsPrimary: true, null, DiagnosisStatus.Active, null));
        dengue = await SignAsync(dengue);
        var followUp = await SignAsync(await StartAsync(patient, "Review after dengue"));
        followUp = await doctor.PostAsync<EncounterDetail>($"{Encounters}/{followUp.Id}/amend", new AmendEncounterRequest(
            "Platelet count from the lab report added", "Review after dengue", null, null, "Recovering; platelets 1.8 lakh", null, null,
            null, null, null, null, null, followUp.Version));
        followUp.Status.ShouldBe(EncounterStatus.Amended);
        var unsigned = await StartAsync(patient, "Cough for a week");

        // Then the patient was seen and the note signed at the hospital's annexe, and another patient was seen here.
        var annexe = await SignedAtAnotherFacilityAsync(patient, dengue.PractitionerId, "Seen at the annexe for a cough");
        await SignAsync(await StartAsync(someoneElse, "Headache"));

        // Today's consultation is being written; a later one is already signed.
        var today = await StartAsync(patient, "Cough not better");
        var evening = await SignAsync(await StartAsync(patient, "Seen again in the evening"));

        // Today's previous visit is the annexe's, the latest signed before it, not the evening's.
        (await PreviousVisitAsync(today)).ShouldNotBeNull().EncounterId.ShouldBe(annexe);

        // The evening's is the annexe's too: today's note, the latest before it, is not signed yet.
        (await PreviousVisitAsync(evening)).ShouldNotBeNull().EncounterId.ShouldBe(annexe);

        // Before the unsigned note, the amended follow-up was the latest signed visit.
        (await PreviousVisitAsync(unsigned)).ShouldNotBeNull().ShouldSatisfyAllConditions(
            v => v.EncounterId.ShouldBe(followUp.Id),
            v => v.Status.ShouldBe(EncounterStatus.Amended));

        // The follow-up's previous visit is the dengue visit, as the chart shows it.
        var previous = (await PreviousVisitAsync(followUp)).ShouldNotBeNull();
        previous.ShouldSatisfyAllConditions(
            v => v.EncounterId.ShouldBe(dengue.Id),
            v => v.Type.ShouldBe(EncounterType.OutPatient),
            v => v.Status.ShouldBe(EncounterStatus.Signed),
            v => v.StartedAt.ShouldBe(dengue.StartedAt),
            v => v.PractitionerName.ShouldBe(dengue.PractitionerName),
            v => v.DepartmentName.ShouldBe(dengue.DepartmentName),
            v => v.ChiefComplaint.ShouldBe("Fever for two days with body ache"),
            v => v.Diagnoses.ShouldBe(["A90 Dengue fever [classical dengue]"]));

        // Nothing was signed before the first visit.
        (await PreviousVisitAsync(dengue)).ShouldBeNull();
    }

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
        var previous = (await doctor.GetAsync<PreviousVisitResponse>($"{Encounters}/{second.Id}/previous-visit")).Previous.ShouldNotBeNull();
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
        (await doctor.GetAsync<PreviousVisitResponse>($"{Encounters}/{first.Id}/previous-visit")).Previous.ShouldBeNull();
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

    private async Task<PreviousVisitSummary?> PreviousVisitAsync(EncounterDetail encounter)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        return (await doctor.GetAsync<PreviousVisitResponse>($"{Encounters}/{encounter.Id}/previous-visit")).Previous;
    }

    private async Task<EncounterDetail> StartAsync(Guid patientId, string complaint)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        return await doctor.PostAsync<EncounterDetail>(Encounters,
            new StartEncounterRequest(patientId, null, null, EncounterType.OutPatient, null, null, null, complaint));
    }

    private async Task<EncounterDetail> SignAsync(EncounterDetail encounter)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var signed = await doctor.PostAsync<EncounterDetail>($"{Encounters}/{encounter.Id}/sign", new SignEncounterRequest(encounter.Version));
        signed.Status.ShouldBe(EncounterStatus.Signed);
        return signed;
    }

    /// <summary>A signed encounter of the patient at another facility of the group, which the hospital administrator adds.</summary>
    private async Task<Guid> SignedAtAnotherFacilityAsync(Guid patientId, Guid practitionerId, string complaint)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var annexe = await admin.PostAsync<Created>("/api/v1/masters/facilities", new SaveFacilityRequest(
            Letters(6).ToUpperInvariant(), "Nana Hospitals Annexe " + Letters(4), null, new Address { City = "Chennai", PinCode = "600002" },
            null, null, null, null, null, null, null, null, null, null, IsActive: false, null));

        var encounter = new Encounter
        {
            FacilityId = annexe.Id,
            PatientId = patientId,
            PractitionerId = practitionerId,
            Type = EncounterType.OutPatient,
            StartedAt = DateTimeOffset.UtcNow,
        };
        encounter.Update(new EncounterContent(complaint, null, null, null, null, null, null, null, null, null, null)).IsSuccess.ShouldBeTrue();
        encounter.Sign(null, practitionerId, DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        db.Add(encounter);
        await db.SaveChangesAsync(Ct);
        return encounter.Id;
    }

    private async Task<Guid> RegisterAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Previous", MiddleName: null, LastName: "Visit" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(10_000)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture),
            AlternatePhone: null, Email: null,
            Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null,
            EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null,
            Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return patient.Id;
    }

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

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);

    private static string Letters(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Created(Guid Id);
}
