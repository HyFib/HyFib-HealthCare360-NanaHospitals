using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Sensitive categories (gap R-4): an HIV, mental-illness or abortion diagnosis makes the clinical record confidential.
/// It opens to staff holding the category's permission, to those who may open restricted records, or through
/// break-the-glass; the patient's details, the desk and the departments keep serving the patient.
/// </summary>
public sealed class SensitiveRecordTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_hiv_diagnosis_makes_the_record_confidential_and_it_opens_only_with_the_category()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var categories = $"/api/v1/patients/{patient.Id}/sensitive-categories";
        var chart = $"/api/v1/emr/patients/{patient.Id}/diagnoses";

        // An ordinary diagnosis changes nothing; an HIV one puts the record in the HIV category.
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patient.Id, VisitId: null, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: "Weight loss"));
        await doctor.PostAsync<DiagnosisDto>($"/api/v1/emr/encounters/{encounter.Id}/diagnoses",
            new AddDiagnosisRequest("R50.9", null, DiagnosisType.Provisional, IsPrimary: true, null, DiagnosisStatus.Active, null));
        (await admin.GetAsync<SensitiveCategoriesDto>(categories)).Categories.ShouldBeEmpty();
        await doctor.PostAsync<DiagnosisDto>($"/api/v1/emr/encounters/{encounter.Id}/diagnoses",
            new AddDiagnosisRequest("Z21", null, DiagnosisType.Final, IsPrimary: false, null, DiagnosisStatus.Active, null));
        var flagged = await admin.GetAsync<SensitiveCategoriesDto>(categories);
        flagged.Categories.ShouldBe([SensitiveCategory.Hiv]);

        // Doctors and nurses hold the HIV category by default; the desk still serves the patient, but may not see the flag.
        (await doctor.GetAsync<List<DiagnosisDto>>(chart)).Count.ShouldBe(2);
        (await nurse.GetAsync<List<DiagnosisDto>>(chart)).Count.ShouldBe(2);
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).Uhid.ShouldBe(patient.Uhid);
        using (var notTheDesks = await frontOffice.SendRawAsync(HttpMethod.Get, categories))
        {
            notTheDesks.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // In the MTP category too, which doctors and nurses do not hold: the chart closes to them until they break the glass.
        var both = await admin.PutAsync<SensitiveCategoriesDto>(categories,
            new SetSensitiveCategoriesRequest([SensitiveCategory.Hiv, SensitiveCategory.Mtp], "Termination of pregnancy recorded in the MTP register", flagged.Version));
        both.Categories.ShouldBe([SensitiveCategory.Hiv, SensitiveCategory.Mtp], ignoreOrder: true);
        using (var stale = await admin.SendRawAsync(HttpMethod.Put, categories,
                   new SetSensitiveCategoriesRequest([], "Recorded in error on the wrong patient", flagged.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using (var closed = await nurse.SendRawAsync(HttpMethod.Get, chart))
        {
            closed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            var problem = await closed.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
            problem.GetProperty("code").GetString().ShouldBe("patient.restricted");
            problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("termination of pregnancy");
        }

        await nurse.PostAsync<BreakGlassGrant>($"/api/v1/patients/{patient.Id}/break-glass", new BreakGlassRequest("Collapsed in the ward, needs the full history"));
        (await nurse.GetAsync<List<DiagnosisDto>>(chart)).Count.ShouldBe(2);

        // Allergies stay visible to everyone treating the patient, and the desk still serves the patient.
        await doctor.GetAsync<JsonElement>($"/api/v1/emr/patients/{patient.Id}/allergies");
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).Id.ShouldBe(patient.Id);
        using (var doctorClosed = await doctor.SendRawAsync(HttpMethod.Get, chart))
        {
            doctorClosed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // The doctor may not open the MTP chart, but can still correct a category recorded in error.
        var current = await doctor.GetAsync<SensitiveCategoriesDto>(categories);
        current.Categories.ShouldBe([SensitiveCategory.Hiv, SensitiveCategory.Mtp], ignoreOrder: true);
        (await doctor.PutAsync<SensitiveCategoriesDto>(categories,
            new SetSensitiveCategoriesRequest([SensitiveCategory.Hiv], "MTP category recorded on the wrong patient", current.Version))).Categories
            .ShouldBe([SensitiveCategory.Hiv]);
        (await doctor.GetAsync<List<DiagnosisDto>>(chart)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_merged_duplicate_brings_its_categories_to_the_surviving_record()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var kept = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var duplicate = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var flagged = await admin.GetAsync<SensitiveCategoriesDto>($"/api/v1/patients/{duplicate.Id}/sensitive-categories");
        await admin.PutAsync<SensitiveCategoriesDto>($"/api/v1/patients/{duplicate.Id}/sensitive-categories",
            new SetSensitiveCategoriesRequest([SensitiveCategory.MentalHealth], "Psychiatric admission recorded on paper", flagged.Version));

        await mrd.PostAsync($"/api/v1/patients/{duplicate.Id}/merge", new MergePatientRequest(kept.Id, "Registered twice"));

        (await admin.GetAsync<SensitiveCategoriesDto>($"/api/v1/patients/{kept.Id}/sensitive-categories")).Categories
            .ShouldBe([SensitiveCategory.MentalHealth]);
    }
}
