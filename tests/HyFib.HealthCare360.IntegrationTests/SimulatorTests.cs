using System.Net;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Features.Admin;
using HyFib.HealthCare360.Integrations.Features.Labels;
using HyFib.HealthCare360.Integrations.Labels;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Simulators for training (SRS §5: every integration has one), over the real API: ICD lookup answers from its
/// training list without WHO, and barcode labels go to the simulator inbox instead of a printer; both are logged and
/// show their provider and health on the integrations dashboard. They work only on a training or test server, one set
/// up with demonstration data: on a hospital's live server a simulator cannot be chosen, and one chosen before does
/// nothing.
/// </summary>
public sealed class SimulatorTests(HospitalServer server)
{
    [Fact]
    public async Task Online_icd_lookup_answers_from_the_training_list_with_the_simulator()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var coder = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        await SaveAsync(admin, "integrations.icd", """{"enabled":true,"provider":"simulator"}""");
        try
        {
            var results = await coder.GetAsync<List<OnlineIcdResult>>("/api/v1/mrd/icd-codes/online?search=cholera&release=ICD-11%20MMS");
            var cholera = results.ShouldHaveSingleItem();
            (cholera.Code, cholera.Title, cholera.Release).ShouldBe(("1A00", "Cholera", "ICD-11 MMS"));

            var icd10 = await coder.GetAsync<List<OnlineIcdResult>>("/api/v1/mrd/icd-codes/online?search=J45&release=ICD-10");
            icd10.Select(r => r.Code).ShouldBe(["J45", "J45.9"]);

            var test = await admin.PostAsync<IntegrationTestResult>("/api/v1/admin/integrations/icd/test", new TestIntegrationRequest(null, null, null, null, null));
            test.Success.ShouldBeTrue(test.Message);
            var row = (await admin.GetAsync<List<IntegrationStatusDto>>("/api/v1/admin/integrations")).Single(r => r.Name == "icd");
            row.Provider.ShouldBe("simulator");
            row.Health.ShouldBeOneOf("healthy", "degraded");
        }
        finally
        {
            await SaveAsync(admin, "integrations.icd", """{"enabled":false,"provider":"disabled"}""");
        }
    }

    [Fact]
    public async Task Label_jobs_are_logged_and_with_the_simulator_kept_as_the_training_printer_received_them()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var zebra = new LabelPrinterSetup("zpl", 203, 50, 25);
        LabTubeLabelsRequest Tube(string barcode) => new(zebra,
        [
            new LabTubeLabel(barcode, "Kiran Test", "NH00012345", "46Y/M", "LA2600123", false, "EDTA", "HAEM", ["CBC"], DateTimeOffset.UtcNow),
        ]);

        // On the workstations' printers, each job is logged without what is on the label.
        (await lab.GetAsync<LabelMode>("/api/v1/integrations/labels/mode")).Simulator.ShouldBeFalse();
        await lab.PostAsync<LabelCommands>("/api/v1/integrations/labels/lab-tubes", Tube("LS" + Digits(10)));
        var logged = (await admin.GetAsync<PagedResult<IntegrationLogDto>>("/api/v1/admin/integrations/logs?integration=labels&pageSize=5")).Items.First();
        (logged.Provider, logged.Operation, logged.Success).ShouldBe(("zpl", "lab-tubes", true));
        logged.Request.ShouldBe("1 label(s) × 1, 50 × 25 mm at 203 dpi");
        logged.Response.ShouldNotBeNull().ShouldNotContain("Kiran");

        await SaveAsync(admin, "integrations.labels", """{"provider":"simulator"}""");
        try
        {
            (await lab.GetAsync<LabelMode>("/api/v1/integrations/labels/mode")).Simulator.ShouldBeTrue();
            var barcode = "LS" + Digits(10);
            var commands = await lab.PostAsync<LabelCommands>("/api/v1/integrations/labels/lab-tubes", Tube(barcode));

            var kept = (await admin.GetAsync<PagedResult<SimulatorMessageDto>>(
                $"/api/v1/admin/integrations/simulator/messages?channel=Labels&search={barcode}")).Items.ShouldHaveSingleItem();
            kept.Body.ShouldBe(commands.Content);
            kept.Subject.ShouldBe("lab-tubes: 1 label(s) × 1, 50 × 25 mm at 203 dpi");
            var row = (await admin.GetAsync<List<IntegrationStatusDto>>("/api/v1/admin/integrations")).Single(r => r.Name == "labels");
            row.Provider.ShouldBe("simulator");
            row.Health.ShouldBeOneOf("healthy", "degraded");
        }
        finally
        {
            await SaveAsync(admin, "integrations.labels", """{"provider":"printers"}""");
        }
    }

    [Fact]
    public async Task On_a_live_server_no_integration_can_be_switched_to_its_simulator()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        await using var putBack = await PutBackAfterwardsAsync(admin);
        using var live = server.AsLiveServer();

        foreach (var name in WithSimulator)
        {
            var key = "integrations." + name;
            using var refused = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/admin/settings/{key}", new SaveSettingRequest(
                new JsonObject { ["enabled"] = true, ["provider"] = " Simulator" }, null, false, null, await admin.SettingVersionAsync(key)));
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"{key} was saved with its simulator.");
            (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("training");
        }
    }

    [Fact]
    public async Task On_a_live_server_the_simulators_chosen_on_a_training_server_do_nothing()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await using var putBack = await PutBackAfterwardsAsync(admin);
        foreach (var name in WithSimulator)
        {
            await SaveAsync(admin, "integrations." + name, """{"enabled":true,"provider":"simulator"}""");
        }

        // The server goes live with them still chosen: none sends, takes a payment or answers in its place.
        using var live = server.AsLiveServer();
        var answered = new List<string>();
        foreach (var name in WithSimulator.Where(n => n != "labels"))
        {
            var to = name switch { "sms" or "whatsapp" => "9876543210", "email" => "it-simulator@example.test", _ => null };
            var test = await admin.PostAsync<IntegrationTestResult>($"/api/v1/admin/integrations/{name}/test", new TestIntegrationRequest(to, null, null, null, null));
            if (test.Success)
            {
                answered.Add($"{name} ({test.Message})");
            }
        }

        if ((await lab.GetAsync<LabelMode>("/api/v1/integrations/labels/mode")).Simulator)
        {
            answered.Add("labels (shown on screen instead of printed)");
        }

        using (var report = await accounts.SendRawAsync(HttpMethod.Get, "/api/v1/billing/pos/settlements/training-file"))
        {
            if (report.IsSuccessStatusCode)
            {
                answered.Add("pos (a settlement report of the training terminal's sales)");
            }
        }

        answered.ShouldBeEmpty();
    }

    /// <summary>The integrations with a simulator for training, chosen as their provider.</summary>
    private static readonly string[] WithSimulator = ["sms", "whatsapp", "email", "payment", "pos", "tally", "pacs", "icd", "biometric", "aadhaar", "labels"];

    /// <summary>
    /// Puts back, when disposed, every hospital-wide integration setting the test changed, because the server is shared
    /// by every test of the run.
    /// </summary>
    private static async Task<IAsyncDisposable> PutBackAfterwardsAsync(ApiClient admin)
    {
        var before = await IntegrationSettingsAsync(admin);
        return new PutBack(async () =>
        {
            foreach (var (key, value) in await IntegrationSettingsAsync(admin))
            {
                var was = before.GetValueOrDefault(key);
                if (!JsonNode.DeepEquals(value, was))
                {
                    await admin.SaveSettingAsync(key, new SaveSettingRequest(was?.DeepClone(), null, false, null));
                }
            }
        });
    }

    private static async Task<Dictionary<string, JsonNode?>> IntegrationSettingsAsync(ApiClient admin) =>
        (await admin.GetAsync<List<SettingDto>>("/api/v1/admin/settings?prefix=integrations."))
        .Where(s => s.FacilityId is null && !s.IsSecret)
        .ToDictionary(s => s.Key, s => s.Value);

    private sealed class PutBack(Func<Task> putBack) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await putBack();
    }

    private static Task SaveAsync(ApiClient admin, string key, string json) =>
        admin.SaveSettingAsync(key, new SaveSettingRequest(JsonNode.Parse(json), null, false, null));

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));
}
