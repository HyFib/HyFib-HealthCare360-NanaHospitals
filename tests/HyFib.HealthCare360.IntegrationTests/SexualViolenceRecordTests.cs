using System.Net;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;
using HyFib.HealthCare360.Modules.Clinical.Features.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Migrations = HyFib.HealthCare360.Server.Persistence.Migrations;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A victim of sexual violence, over the real API: nothing may make the victim's identity known (Bharatiya Nyaya Sanhita
/// 2023, s.72; for a child, the POCSO Act 2012, s.23), so the record is kept confidential as an HIV or MTP record is. A
/// sexual assault entered in the MLC register, or a diagnosis of sexual abuse, puts it in the sexual-violence category,
/// and the emergency case opens, as the chart does, only to staff holding it; the upgrade does the same for the entries
/// and diagnoses already recorded.
/// </summary>
public sealed class SexualViolenceRecordTests(HospitalServer server)
{
    private const string Er = "/api/v1/emergency";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sexual_assault_in_the_mlc_register_or_a_diagnosis_of_sexual_abuse_makes_the_record_confidential()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);

        // A road accident in the register leaves the record as it was; a sexual assault puts it in the category.
        var (accident, accidentCase) = await MlcAsync(frontOffice, doctor, MlcCause.RoadTrafficAccident);
        (await CategoriesAsync(admin, accident)).ShouldBeEmpty();
        var (victim, victimCase) = await MlcAsync(frontOffice, doctor, MlcCause.SexualAssault);
        (await CategoriesAsync(admin, victim)).ShouldBe([SensitiveCategory.SexualViolence]);

        // Doctors, nurses and medical records hold the category: the ER doctor carries on with the case. The front office,
        // which sees the ER board, opens the road accident's case but not the victim's: it is her clinical record.
        (await doctor.GetAsync<EmergencyCaseDetail>($"{Er}/cases/{victimCase}")).Mlc.ShouldNotBeNull().Cause.ShouldBe(MlcCause.SexualAssault);
        (await frontOffice.GetAsync<EmergencyCaseDetail>($"{Er}/cases/{accidentCase}")).Mlc.ShouldNotBeNull();
        using (var closed = await frontOffice.SendRawAsync(HttpMethod.Get, $"{Er}/cases/{victimCase}"))
        {
            closed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await closed.Content.ReadAsStringAsync(Ct)).ShouldContain("patient.restricted");
        }

        // A diagnosis of sexual abuse in a consultation does the same.
        var abused = await AbuseDiagnosedAsync(frontOffice, doctor);
        (await CategoriesAsync(admin, abused)).ShouldBe([SensitiveCategory.SexualViolence]);
    }

    [Fact]
    public async Task The_upgrade_puts_the_records_of_sexual_assaults_and_abuse_already_recorded_in_the_category()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (accident, _) = await MlcAsync(frontOffice, doctor, MlcCause.RoadTrafficAccident);
        var (victim, _) = await MlcAsync(frontOffice, doctor, MlcCause.SexualAssault);
        var abused = await AbuseDiagnosedAsync(frontOffice, doctor);

        // As the records were before the upgrade, when nothing put them in the category; then the upgrade's own statement.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await db.Database.ExecuteSqlAsync(
                $"UPDATE patients.patients SET sensitive_categories = sensitive_categories & ~8 WHERE id IN ({accident}, {victim}, {abused})", Ct);
            (await CategoriesAsync(admin, victim)).ShouldBeEmpty();
            await db.Database.ExecuteSqlRawAsync(Migrations.SexualViolenceCategory.MarkVictimsRecords, Ct);
        }

        (await CategoriesAsync(admin, victim)).ShouldBe([SensitiveCategory.SexualViolence]);
        (await CategoriesAsync(admin, abused)).ShouldBe([SensitiveCategory.SexualViolence]);
        (await CategoriesAsync(admin, accident)).ShouldBeEmpty();
    }

    /// <summary>A new emergency patient, a woman, whose case is entered in the MLC register as <paramref name="cause"/>.</summary>
    private static async Task<(Guid PatientId, Guid CaseId)> MlcAsync(ApiClient frontOffice, ApiClient doctor, MlcCause cause)
    {
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice, Gender.Female);
        var caseId = (await doctor.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.VisitId == visit.Id).CaseId;
        await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc", new SaveMlcRequest(
            MlcBroughtBy.Relative, "Sister, Anitha", cause, DateTimeOffset.UtcNow.AddHours(-2), "Near the bus stand, Adyar", "Abrasions over both forearms",
            true, false, "Adyar", null, null));
        return (patientId, caseId);
    }

    /// <summary>A new patient diagnosed with sexual abuse (T74.2) in a consultation.</summary>
    private static async Task<Guid> AbuseDiagnosedAsync(ApiClient frontOffice, ApiClient doctor)
    {
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patient.Id, VisitId: null, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: "Disclosed abuse"));
        await doctor.PostAsync<DiagnosisDto>($"/api/v1/emr/encounters/{encounter.Id}/diagnoses",
            new AddDiagnosisRequest("T74.2", null, DiagnosisType.Final, IsPrimary: true, null, DiagnosisStatus.Active, null));
        return patient.Id;
    }

    private static async Task<IReadOnlyList<SensitiveCategory>> CategoriesAsync(ApiClient admin, Guid patientId) =>
        (await admin.GetAsync<SensitiveCategoriesDto>($"/api/v1/patients/{patientId}/sensitive-categories")).Categories;
}
