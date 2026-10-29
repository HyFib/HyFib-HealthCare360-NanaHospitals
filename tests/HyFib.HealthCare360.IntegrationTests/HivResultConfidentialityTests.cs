using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task A_reactive_band_of_an_hiv_panel_makes_the_record_confidential_but_a_test_beside_an_hiv_test_does_not()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        // An HIV confirmation panel reports bands named by their protein alone: a reactive band is an HIV result.
        var (gp41, p24) = ($"IT-GP41-{suffix}", $"IT-P24-{suffix}");
        var confirmation = await PanelAsync($"IT-HIVCONF-{suffix}", "HIV confirmation", [await BandAsync(gp41, "gp41 band"), await BandAsync(p24, "p24 band")]);
        var confirmed = await ReleasedAsync(confirmation, answers: new Dictionary<string, string> { [gp41] = "Reactive", [p24] = "Non-reactive" });
        (await CategoriesAsync(admin, confirmed.PatientId)).ShouldBe([SensitiveCategory.Hiv]);

        // Viral markers hold the HIV test and the hepatitis B test: a reactive hepatitis B result beside a non-reactive HIV
        // test says nothing of HIV status.
        var markers = await PanelAsync($"IT-MARKERS-{suffix}", "Viral markers", [await TestIdAsync("LAB-HIV"), await TestIdAsync("LAB-HBSAG")]);
        var hepatitis = await ReleasedAsync(markers, answers: new Dictionary<string, string> { ["LAB-HIV"] = "Non-reactive", ["LAB-HBSAG"] = "Reactive" });
        (await CategoriesAsync(admin, hepatitis.PatientId)).ShouldBeEmpty();
    }

    /// <summary>
    /// A test of a new patient (an HIV test unless another is named), drawn on their consent, entered as
    /// <paramref name="answer"/>, verified and released.
    /// </summary>
    private Task<ReleasedResult> ReleasedAsync(string answer, string testCode = "LAB-HIV") =>
        ReleasedAsync(testCode, answers: new Dictionary<string, string> { [testCode] = answer });

    /// <summary>A test of a new patient, drawn on their consent, its analytes entered as <paramref name="answers"/>, verified and released.</summary>
    private async Task<ReleasedResult> ReleasedAsync(string testCode, IReadOnlyDictionary<string, string> answers)
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var patient = await LabVerificationTests.RegisterPatientAsync(server);
        var tube = await LabVerificationTests.ReceivedSampleAsync(server, testCode, patient.Id, consented: true);
        var lines = await LabVerificationTests.EnterAsync(lab, tube, answers);
        var released = await pathologist.PostAsync<VerificationOutcome>(
            "/api/v1/lab/results/verify", new VerifyResultsRequest([.. lines.Select(l => l.ResultId)], null, null, Release: true));
        released.ReleasedItemIds.ShouldHaveSingleItem();

        var sheet = await lab.GetAsync<ResultSheet>($"/api/v1/lab/samples/{tube.Id}/results");
        var order = await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{sheet.LabOrderId}");
        return new ReleasedResult(patient.Id, order.ReportId.ShouldNotBeNull(), lines[0].ResultId);
    }

    /// <summary>A band answered as reactive or non-reactive, set up like the hepatitis B test but named by its protein alone.</summary>
    private async Task<Guid> BandAsync(string code, string name)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var like = await db.Set<LabTest>().AsNoTracking().Include(t => t.Ranges).SingleAsync(t => t.Code == "LAB-HBSAG", Ct);
        var band = new LabTest
        {
            Code = code,
            Name = name,
            Section = like.Section,
            SpecimenType = like.SpecimenType,
            Container = like.Container,
            TatHours = like.TatHours,
            ResultType = like.ResultType,
            Options = [.. like.Options],
            Ranges = [.. like.Ranges.Select(r => new ReferenceRange { NormalValues = [.. r.NormalValues], CriticalValues = [.. r.CriticalValues] })],
        };
        db.Add(band);
        await db.SaveChangesAsync(Ct);
        return band.Id;
    }

    /// <summary>An orderable panel of the tests, drawn in the hepatitis B test's tube; returns its code.</summary>
    private async Task<string> PanelAsync(string code, string name, IReadOnlyList<Guid> components)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var like = await db.Set<LabTest>().AsNoTracking().SingleAsync(t => t.Code == "LAB-HBSAG", Ct);
        var panel = new LabTest
        {
            Code = code,
            Name = name,
            Section = like.Section,
            SpecimenType = like.SpecimenType,
            Container = like.Container,
            TatHours = like.TatHours,
            IsOrderable = true,
            IsPanel = true,
        };
        panel.Components = [.. components.Select((test, i) => new LabTestComponent { PanelTestId = panel.Id, TestId = test, DisplayOrder = i + 1 })];
        db.Add(panel);
        await db.SaveChangesAsync(Ct);
        return code;
    }

    private async Task<Guid> TestIdAsync(string code)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Set<LabTest>().Where(t => t.Code == code).Select(t => t.Id).SingleAsync(Ct);
    }

    private static async Task<IReadOnlyList<SensitiveCategory>> CategoriesAsync(ApiClient admin, Guid patientId) =>
        (await admin.GetAsync<SensitiveCategoriesDto>($"/api/v1/patients/{patientId}/sensitive-categories")).Categories;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record ReleasedResult(Guid PatientId, Guid ReportId, Guid ResultId);
}
