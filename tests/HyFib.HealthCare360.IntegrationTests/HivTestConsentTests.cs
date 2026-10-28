using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Consent before an HIV test, over the real API. The HIV and AIDS (Prevention and Control) Act 2017, s.5, lets no HIV
/// test be undertaken without the informed consent of the person tested or their representative; s.6 adds a court's
/// order. The laboratory does not draw an HIV test's sample until the consent, or the order, is recorded on the test.
/// </summary>
public sealed class HivTestConsentTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_hiv_test_is_drawn_only_once_the_patients_consent_is_recorded_and_other_tests_need_none()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var patient = await LabVerificationTests.RegisterPatientAsync(server);
        var order = await lab.PostAsync<LabOrderCreated>("/api/v1/lab/orders", new CreateLabOrderRequest(
            patient.Id, null, [await TestIdAsync(lab, "LAB-HIV"), await TestIdAsync(lab, "LAB-CBC")], OrderPriority.Routine, LabOrderSource.WalkIn,
            null, null, null));
        var hivItem = (await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{order.Id}")).Items.Single(i => i.TestCode == "LAB-HIV");
        var hivTube = order.Samples.Single(s => s.Id == hivItem.SampleId);
        var cbcTube = order.Samples.Single(s => s.Id != hivItem.SampleId);
        var collect = $"/api/v1/lab/orders/{order.Id}/collect";

        // A walk-in test is drawn only once paid; these tests are about consent, so the counter waives that here.
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");
        await SetPaymentGateAsync(admin, facilityId, required: false);
        try
        {
            // The blood count needs no consent: its tube is drawn.
            await lab.PostAsync<List<SampleLabel>>(collect, new CollectSamplesRequest([cbcTube.Id], null, null));

            // The HIV test is not drawn without the patient's consent, and the test waiting for it is named.
            using (var refused = await lab.SendRawAsync(HttpMethod.Post, collect, new CollectSamplesRequest([hivTube.Id], null, null)))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
                problem.GetProperty("code").GetString().ShouldBe("lab.consent_required");
                problem.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("HIV and AIDS Act 2017, s.5");
                problem.GetProperty("data").GetProperty("tests").EnumerateArray().Select(t => t.GetProperty("itemId").GetGuid()).ShouldBe([hivItem.Id]);
            }

            // A representative's consent names them and their relation to the patient.
            using (var unnamed = await lab.SendRawAsync(HttpMethod.Post, collect, new CollectSamplesRequest(
                       [hivTube.Id], null, null, [new TestConsentInput(hivItem.Id, ConsentBasis.Representative, null, null, null)])))
            {
                unnamed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            }

            // With the patient's consent, given after counselling on the numbered form, the tube is drawn, and the consent
            // stays on the test.
            await lab.PostAsync<List<SampleLabel>>(collect, new CollectSamplesRequest(
                [hivTube.Id], null, null, [new TestConsentInput(hivItem.Id, ConsentBasis.Patient, null, null, "ICTC/42")]));
            var recorded = (await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{order.Id}")).Items.Single(i => i.Id == hivItem.Id);
            recorded.Status.ShouldBe(LabOrderItemStatus.Collected);
            recorded.ConsentBasis.ShouldBe(ConsentBasis.Patient);
            recorded.ConsentRecordedAt.ShouldNotBeNull();
        }
        finally
        {
            await SetPaymentGateAsync(admin, facilityId, required: true);
        }
    }

    [Fact]
    public async Task An_hiv_test_in_a_panel_within_the_ordered_panel_also_waits_for_consent()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var hiv = await admin.GetAsync<LabTestDetail>($"/api/v1/lab/tests/{await TestIdAsync(lab, "LAB-HIV")}");

        // A pre-operative profile holds a viral markers panel, which holds the HIV test.
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var viral = await PanelAsync(hiv, $"IT-VIRAL-{suffix}", "Viral markers", orderable: false, [hiv.Id, await TestIdAsync(lab, "LAB-HBSAG")]);
        var preOp = await PanelAsync(hiv, $"IT-PREOP-{suffix}", "Pre-operative profile", orderable: true, [viral]);

        var patient = await LabVerificationTests.RegisterPatientAsync(server);
        var order = await lab.PostAsync<LabOrderCreated>("/api/v1/lab/orders", new CreateLabOrderRequest(
            patient.Id, null, [preOp], OrderPriority.Routine, LabOrderSource.WalkIn, null, null, null));
        var profile = (await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{order.Id}")).Items.ShouldHaveSingleItem();
        var collect = $"/api/v1/lab/orders/{order.Id}/collect";

        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");
        await SetPaymentGateAsync(admin, facilityId, required: false);
        try
        {
            // The profile's tube is held for the HIV test two panels down, and the refusal names the tube it is in.
            using (var refused = await lab.SendRawAsync(HttpMethod.Post, collect, new CollectSamplesRequest(null, null, null)))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
                problem.GetProperty("code").GetString().ShouldBe("lab.consent_required");
                var held = problem.GetProperty("data").GetProperty("tests").EnumerateArray().ShouldHaveSingleItem();
                (held.GetProperty("itemId").GetGuid(), held.GetProperty("sampleId").GetGuid()).ShouldBe((profile.Id, profile.SampleId.ShouldNotBeNull()));
            }

            await lab.PostAsync<List<SampleLabel>>(collect, new CollectSamplesRequest(
                null, null, null, [new TestConsentInput(profile.Id, ConsentBasis.Patient, null, null, "ICTC/43")]));
        }
        finally
        {
            await SetPaymentGateAsync(admin, facilityId, required: true);
        }
    }

    /// <summary>
    /// A panel of <paramref name="components"/>, drawn in the tube of <paramref name="like"/>, in the test master. It goes in
    /// directly: an orderable test saved from the masters screen needs a billable service of its own.
    /// </summary>
    private async Task<Guid> PanelAsync(LabTestDetail like, string code, string name, bool orderable, IReadOnlyList<Guid> components)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var panel = new LabTest
        {
            Code = code,
            Name = name,
            Section = like.Section,
            SpecimenType = like.SpecimenType,
            Container = like.Container,
            TatHours = like.TatHours,
            IsOrderable = orderable,
            IsPanel = true,
        };
        panel.Components = [.. components.Select((test, i) => new LabTestComponent { PanelTestId = panel.Id, TestId = test, DisplayOrder = i + 1 })];
        db.Add(panel);
        await db.SaveChangesAsync(Ct);
        return panel.Id;
    }

    private static async Task<Guid> TestIdAsync(ApiClient lab, string code) =>
        (await lab.GetAsync<PagedResult<LabTestListItem>>($"/api/v1/lab/tests?search={code}")).Items.Single(t => t.Code == code).Id;

    private static Task SetPaymentGateAsync(ApiClient admin, Guid facilityId, bool required) =>
        admin.SaveSettingAsync("lab.requirePaymentBeforeCollection", new SaveSettingRequest(JsonValue.Create(required), facilityId, false, null));
}
