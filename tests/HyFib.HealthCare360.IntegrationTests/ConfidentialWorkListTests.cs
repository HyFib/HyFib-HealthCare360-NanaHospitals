using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Rounds;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A patient whose record is in a sensitive category the doctor does not hold (here termination of pregnancy, which only
/// medical records hold by default) stays on the doctor's ward round and worklist by name, as on the bed board, but
/// without the clinical text: the diagnosis, the vital-sign details, the result's name. Break-the-glass opens it, as it
/// opens the chart.
/// </summary>
public sealed class ConfidentialWorkListTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_record_in_a_sensitive_category_the_doctor_does_not_hold_is_on_the_round_and_worklist_without_its_clinical_text()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var doctor = await server.SignInAsync(Users.Doctor);
        var (patient, admission) = await ClinicalSignOffTests.AdmitAsync(admin, await ClinicalSignOffTests.PractitionerAsync(doctor), "Incomplete abortion");
        try
        {
            await PutInCategoryAsync(patient.Id, SensitiveCategory.Mtp);
            var result = await new ClinicalSignOffTests(server).ResultAsync(doctor, patient.Id, critical: true, "Serum beta-hCG");

            var onRound = (await doctor.GetAsync<WardRound>("/api/v1/inpatient/rounds")).Patients.Single(p => p.AdmissionId == admission.Id);
            onRound.ShouldSatisfyAllConditions(
                p => p.IsConfidential.ShouldBeTrue(),
                p => p.IsRestricted.ShouldBeFalse(),
                p => p.PatientName.ShouldNotBe("Restricted patient", "the name stays, as on the bed board"),
                p => p.ProvisionalDiagnosis.ShouldBeNull(),
                p => p.VitalsFlags.ShouldBeEmpty(),
                p => p.Uhid.ShouldBe(patient.Uhid),
                p => p.HasCriticalResults.ShouldBeTrue("what needs attention stays"));

            var worklist = await doctor.GetAsync<EmrWorklist>("/api/v1/emr/worklist");
            worklist.ResultsToReview.Single(r => r.Id == result).ShouldSatisfyAllConditions(
                r => r.Title.ShouldBe("Confidential record"),
                r => r.HasCriticalFindings.ShouldBeTrue());

            // Break-the-glass opens the record, and the round shows it all while the access runs.
            await doctor.PostAsync<JsonElement>($"/api/v1/patients/{patient.Id}/break-glass", new BreakGlassRequest("Bleeding on the ward round; need the history"));
            var opened = (await doctor.GetAsync<WardRound>("/api/v1/inpatient/rounds")).Patients.Single(p => p.AdmissionId == admission.Id);
            (opened.IsConfidential, opened.ProvisionalDiagnosis).ShouldBe((false, "Incomplete abortion"));
            (await doctor.GetAsync<EmrWorklist>("/api/v1/emr/worklist")).ResultsToReview.Single(r => r.Id == result).Title.ShouldBe("Serum beta-hCG");
        }
        finally
        {
            await admin.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted for a test of the ward round"));
        }
    }

    private async Task PutInCategoryAsync(Guid patientId, SensitiveCategory category)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var patient = await db.Set<Patient>().SingleAsync(p => p.Id == patientId, Ct);
        patient.SensitiveCategories |= category;
        await db.SaveChangesAsync(Ct);
    }
}
