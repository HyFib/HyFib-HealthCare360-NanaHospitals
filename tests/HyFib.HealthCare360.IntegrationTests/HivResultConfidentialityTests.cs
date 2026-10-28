using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A released HIV result is a record of HIV status, which the HIV and AIDS (Prevention and Control) Act 2017 keeps
/// confidential (s.8 to 11). A reactive one puts the patient's record in the HIV category, as an HIV diagnosis does, so
/// the chart opens only to staff holding that category. A non-reactive one, as on most pre-operative screens, changes
/// nothing.
/// </summary>
public sealed class HivResultConfidentialityTests(HospitalServer server)
{
    [Fact]
    public async Task A_reactive_hiv_result_makes_the_record_confidential_and_a_non_reactive_one_does_not()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);

        // Released reactive: the record is put in the HIV category.
        var reactive = await ReleasedAsync("Reactive");
        (await CategoriesAsync(admin, reactive.PatientId)).ShouldBe([SensitiveCategory.Hiv]);

        // Released non-reactive: nothing changes. Nor does a reactive result of another test.
        var screened = await ReleasedAsync("Non-reactive");
        (await CategoriesAsync(admin, screened.PatientId)).ShouldBeEmpty();
        var hepatitis = await ReleasedAsync("Reactive", "LAB-HBSAG");
        (await CategoriesAsync(admin, hepatitis.PatientId)).ShouldBeEmpty();

        // Corrected to reactive after its release: the record is put in the category then.
        await pathologist.PostAsync($"/api/v1/lab/reports/{screened.ReportId}/amend", new AmendReportRequest(
            "Transcription error: the analyser printout reads reactive", [new AmendmentChange(screened.ResultId, "Reactive", null, null, null)], null));
        (await CategoriesAsync(admin, screened.PatientId)).ShouldBe([SensitiveCategory.Hiv]);
    }

    /// <summary>
    /// A test of a new patient (an HIV test unless another is named), drawn on their consent, entered as
    /// <paramref name="answer"/>, verified and released.
    /// </summary>
    private async Task<ReleasedResult> ReleasedAsync(string answer, string testCode = "LAB-HIV")
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var patient = await LabVerificationTests.RegisterPatientAsync(server);
        var tube = await LabVerificationTests.ReceivedSampleAsync(server, testCode, patient.Id, consented: true);
        var line = (await LabVerificationTests.EnterAsync(lab, tube, new Dictionary<string, string> { [testCode] = answer })).ShouldHaveSingleItem();
        var released = await pathologist.PostAsync<VerificationOutcome>("/api/v1/lab/results/verify", new VerifyResultsRequest([line.ResultId], null, null, Release: true));
        released.ReleasedItemIds.ShouldHaveSingleItem();

        var sheet = await lab.GetAsync<ResultSheet>($"/api/v1/lab/samples/{tube.Id}/results");
        var order = await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{sheet.LabOrderId}");
        return new ReleasedResult(patient.Id, order.ReportId.ShouldNotBeNull(), line.ResultId);
    }

    private static async Task<IReadOnlyList<SensitiveCategory>> CategoriesAsync(ApiClient admin, Guid patientId) =>
        (await admin.GetAsync<SensitiveCategoriesDto>($"/api/v1/patients/{patientId}/sensitive-categories")).Categories;

    private sealed record ReleasedResult(Guid PatientId, Guid ReportId, Guid ResultId);
}
