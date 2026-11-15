using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Immunizations;
using HyFib.HealthCare360.Modules.Interop;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HyFib.HealthCare360.IntegrationTests.ScopedRecordSharingTests;
using ClinicalEncounter = HyFib.HealthCare360.Modules.Clinical.Domain.Encounter;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// #251, decided by the hospital: a partner reading through the FHIR API reads only the records of the facility its
/// account is linked to, for every kind, and only that facility's patients. Allergies are the patient's and are read
/// whole. Each record kind is recorded at the partner's facility and at an annexe of the hospital; the partner, under a
/// consent that shares everything with it, reads and finds the first and neither reads nor finds the second.
/// </summary>
public sealed class FhirFacilityScopeTests(HospitalServer server)
{
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_partner_reads_and_finds_only_its_own_facilitys_records_and_the_patients_allergies_whole()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var here = (await partner.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var annexe = await AnnexeAsync();
        var patient = await RegisterAsync(frontOffice);
        await ShareEverythingAsync(frontOffice, patient, partnerId);

        // An encounter here and one at the annexe, each with its diagnosis (the doctor opens both; one is then the annexe's).
        var ours = await AbdmExportTests.ConsultAsync(doctor, patient, sign: true);
        var theirs = await AbdmExportTests.ConsultAsync(doctor, patient, sign: true);
        await MoveEncounterAsync(theirs, annexe);

        // A dose given here and one recorded at the annexe.
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Ist).DateTime);
        var doses = $"/api/v1/emr/patients/{patient}/immunizations";
        var ourDose = await nurse.PostAsync<ImmunizationDto>(doses, new RecordImmunizationRequest(
            null, "Td", "TD", 1, today, "IM", "Left deltoid", "TD2608-114", "Biological E. Limited", today.AddMonths(10), null, false, null, null));
        var theirDose = await nurse.PostAsync<ImmunizationDto>(doses, new RecordImmunizationRequest(
            null, "Hepatitis B", "HEPB", 1, today, "IM", "Right deltoid", "HB-26-04", "Serum Institute of India", today.AddMonths(12), null, false, null, null));
        await MoveDoseAsync(theirDose.Id, annexe);

        // A document uploaded here and one uploaded at the annexe.
        var documents = $"/api/v1/patients/{patient}/documents";
        var ourDocument = await frontOffice.PostAsync<Created>(documents, PatientDocumentTests.OutsideReportFrom(await PatientDocumentTests.UploadAsync(frontOffice, patient)));
        var theirFile = await PatientDocumentTests.UploadAsync(frontOffice, patient);
        var theirDocument = await frontOffice.PostAsync<Created>(documents, PatientDocumentTests.OutsideReportFrom(theirFile));
        await MoveFileAsync(theirFile, annexe);

        // Vital signs taken here, at the annexe, and two sets recorded without a facility, in each facility's encounter;
        // and an allergy, which is the patient's.
        var vitals = await VitalsAndAllergyAsync(patient, here, annexe, ours, theirs);

        // Vital signs: here, or in an encounter here.
        var observations = Matches((await FhirAsync(partner, $"Observation?patient={patient}&category=vital-signs", "TREAT")).Body);
        observations.ShouldContain(id => id.StartsWith($"vs-{vitals.Here:D}-", StringComparison.Ordinal));
        observations.ShouldContain(id => id.StartsWith($"vs-{vitals.InOurEncounter:D}-", StringComparison.Ordinal));
        observations.ShouldNotContain(id => id.StartsWith($"vs-{vitals.Annexe:D}-", StringComparison.Ordinal));
        observations.ShouldNotContain(id => id.StartsWith($"vs-{vitals.InTheirEncounter:D}-", StringComparison.Ordinal));
        (await FhirAsync(partner, $"Observation/vs-{vitals.Annexe:D}-hr", "TREAT")).Status.ShouldBe(404);
        (await FhirAsync(partner, $"Observation/vs-{vitals.InTheirEncounter:D}-hr", "TREAT")).Status.ShouldBe(404);
        (await FhirAsync(partner, $"Observation/vs-{vitals.Here:D}-hr", "TREAT")).Status.ShouldBe(200);

        // Conditions: those of encounters here.
        var (ourDiagnosis, theirDiagnosis) = (await DiagnosisOfAsync(ours), await DiagnosisOfAsync(theirs));
        Matches((await FhirAsync(partner, $"Condition?patient={patient}", "TREAT")).Body).ShouldBe([ourDiagnosis.ToString("D")]);
        (await FhirAsync(partner, $"Condition/{theirDiagnosis:D}", "TREAT")).Status.ShouldBe(404);
        (await FhirAsync(partner, $"Condition/{ourDiagnosis:D}", "TREAT")).Status.ShouldBe(200);

        // Encounters (by the platform's facility filter, as before).
        Matches((await FhirAsync(partner, $"Encounter?patient={patient}", "TREAT")).Body).ShouldBe([$"enc-{ours:D}"]);
        (await FhirAsync(partner, $"Encounter/enc-{theirs:D}", "TREAT")).Status.ShouldBe(404);

        // Doses: those recorded here.
        Matches((await FhirAsync(partner, $"Immunization?patient={patient}", "TREAT")).Body).ShouldBe([ourDose.Id.ToString("D")]);
        (await FhirAsync(partner, $"Immunization/{theirDose.Id:D}", "TREAT")).Status.ShouldBe(404);

        // Documents: those uploaded here.
        Matches((await FhirAsync(partner, $"DocumentReference?patient={patient}", "TREAT")).Body).ShouldBe([$"doc-{ourDocument.Id:D}"]);
        (await FhirAsync(partner, $"DocumentReference/doc-{theirDocument.Id:D}", "TREAT")).Status.ShouldBe(404);

        // The allergy is the patient's, wherever it was recorded.
        Matches((await FhirAsync(partner, $"AllergyIntolerance?patient={patient}", "TREAT")).Body).ShouldBe([vitals.Allergy.ToString("D")]);
    }

    [Fact]
    public async Task A_patient_never_registered_or_seen_at_the_partners_facility_is_not_the_partners()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        await ShareEverythingAsync(frontOffice, patient, partnerId);
        (await FhirAsync(partner, $"Patient/{patient:D}", "TREAT")).Status.ShouldBe(200);

        // Registered at the annexe and never seen here: not found, whatever their consent says, and no search lists them.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var annexe = await AnnexeAsync();
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>().Where(p => p.Id == patient)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.RegisteredFacilityId, annexe), Ct);
        }

        (await FhirAsync(partner, $"Patient/{patient:D}", "TREAT")).Refusal.ShouldBe((404, "not-found"));
        Matches((await FhirAsync(partner, $"Patient?_id={patient:D}", "TREAT")).Body).ShouldBeEmpty();
        var records = await FhirAsync(partner, $"AllergyIntolerance?patient={patient}", "TREAT");
        Matches(records.Body).ShouldBeEmpty();
        Withheld(records.Body).ShouldBeTrue();

        // Once seen here, in a consultation, they are.
        await AbdmExportTests.ConsultAsync(doctor, patient, sign: true);
        (await FhirAsync(partner, $"Patient/{patient:D}", "TREAT")).Status.ShouldBe(200);
        Matches((await FhirAsync(partner, $"Patient?_id={patient:D}", "TREAT")).Body).ShouldBe([patient.ToString("D")]);
    }

    [Fact]
    public async Task An_account_not_assigned_to_a_facility_reads_nothing_even_signed_in_at_one()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roleName = "FHIR partner " + suffix;
        await server.CreateRoleAsync(await server.SignInAsync(HospitalServer.Users.Admin),
            new SaveRoleRequest(roleName, "Another provider reading records through the FHIR API (test)", [InteropPermissions.FhirRead], null));
        var userName = "fhir" + suffix;
        await server.CreateStaffAccountAsync(userName, roleName);
        var signedIn = await server.SignInAsync(userName);
        var me = await signedIn.GetAsync<AuthUser>("/api/v1/auth/me");
        me.FacilityId.ShouldNotBeNull();
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await RegisterAsync(frontOffice);
        await ShareEverythingAsync(frontOffice, patient, me.Id);
        (await FhirAsync(signedIn, $"Patient/{patient:D}", "TREAT")).Status.ShouldBe(200);

        // The hospital gives the account all facilities in place of its own: an account given all facilities may sign in at
        // any facility, so it is no facility's partner. Its session is refused at once, and so is a new one.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await db.Set<UserFacility>().Where(f => f.UserId == me.Id).ExecuteDeleteAsync(Ct);
            await db.Set<User>().Where(u => u.Id == me.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.AllFacilities, true), Ct);
        }

        (await FhirAsync(signedIn, $"Patient/{patient:D}", "TREAT")).Refusal.ShouldBe((403, "facility-required"));
        var again = await server.SignInAsync(userName);
        (await again.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull("it may still sign in somewhere");
        (await FhirAsync(again, $"Patient?_id={patient:D}", "TREAT")).Refusal.ShouldBe((403, "facility-required"));
        (await FhirAsync(again, $"AllergyIntolerance?patient={patient}", "TREAT")).Refusal.ShouldBe((403, "facility-required"));
    }

    private sealed record Vitals(Guid Here, Guid Annexe, Guid InOurEncounter, Guid InTheirEncounter, Guid Allergy);

    /// <summary>A consent that shares every kind of record with the partner, for any purpose.</summary>
    private static Task ShareEverythingAsync(ApiClient frontOffice, Guid patient, Guid partnerId) =>
        frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents", new CaptureConsentRequest(
            ConsentType.DataSharing, null, true, null, null, null, null, null,
            new RecordSharingScope([partnerId], null, null, null, null, null)));

    /// <summary>A new, inactive facility of the hospital: an annexe.</summary>
    private async Task<Guid> AnnexeAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var code = string.Concat(Enumerable.Range(0, 6).Select(_ => (char)('A' + Random.Shared.Next(26))));
        return (await admin.PostAsync<Created>("/api/v1/masters/facilities", new SaveFacilityRequest(
            code, "Nana Hospitals Annexe " + code, null, new Address { City = "Chennai", PinCode = "600002" },
            null, null, null, null, null, null, null, null, null, null, IsActive: false, null))).Id;
    }

    /// <summary>Makes the encounter the annexe's, as if the doctor had seen the patient there.</summary>
    private async Task MoveEncounterAsync(Guid id, Guid facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ClinicalEncounter>().IgnoreQueryFilters().Where(e => e.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.FacilityId, facilityId), Ct)).ShouldBe(1);
    }

    /// <summary>Makes the dose the annexe's, as if it had been recorded there.</summary>
    private async Task MoveDoseAsync(Guid id, Guid facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Immunization>().IgnoreQueryFilters().Where(i => i.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.FacilityId, facilityId), Ct)).ShouldBe(1);
    }

    /// <summary>Makes the stored file the annexe's, as if front office had uploaded it there.</summary>
    private async Task MoveFileAsync(Guid id, Guid facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<StoredFile>().IgnoreQueryFilters().Where(f => f.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.FacilityId, (Guid?)facilityId), Ct)).ShouldBe(1);
    }

    private async Task<Guid> DiagnosisOfAsync(Guid encounterId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Diagnosis>().IgnoreQueryFilters()
            .Where(d => d.EncounterId == encounterId).Select(d => d.Id).SingleAsync(Ct);
    }

    private async Task<Vitals> VitalsAndAllergyAsync(Guid patientId, Guid here, Guid annexe, Guid ourEncounter, Guid theirEncounter)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = DateTimeOffset.UtcNow;
        VitalSigns Set(Guid? facility, Guid? encounter, int pulse) =>
            new() { PatientId = patientId, FacilityId = facility, EncounterId = encounter, RecordedAt = now.AddMinutes(-pulse), Pulse = pulse, SystolicBp = 120, DiastolicBp = 80 };
        var atHere = Set(here, null, 72);
        var atAnnexe = Set(annexe, null, 74);
        var inOurs = Set(null, ourEncounter, 76);
        var inTheirs = Set(null, theirEncounter, 78);
        var allergy = new Allergy { PatientId = patientId, Allergen = "Penicillin", Type = AllergyType.Drug, Severity = AllergySeverity.Severe, RecordedAt = now.AddDays(-3) };
        db.AddRange(atHere, atAnnexe, inOurs, inTheirs, allergy);
        await db.SaveChangesAsync(Ct);
        return new Vitals(atHere.Id, atAnnexe.Id, inOurs.Id, inTheirs.Id, allergy.Id);
    }
}
