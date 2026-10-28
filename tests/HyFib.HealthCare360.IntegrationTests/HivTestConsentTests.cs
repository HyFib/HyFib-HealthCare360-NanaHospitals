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
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;

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

    private static async Task<Guid> TestIdAsync(ApiClient lab, string code) =>
        (await lab.GetAsync<PagedResult<LabTestListItem>>($"/api/v1/lab/tests?search={code}")).Items.Single(t => t.Code == code).Id;

    private static Task SetPaymentGateAsync(ApiClient admin, Guid facilityId, bool required) =>
        admin.SaveSettingAsync("lab.requirePaymentBeforeCollection", new SaveSettingRequest(JsonValue.Create(required), facilityId, false, null));
}
