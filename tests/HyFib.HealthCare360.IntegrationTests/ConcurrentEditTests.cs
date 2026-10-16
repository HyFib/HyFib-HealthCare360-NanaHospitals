using System.Net;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// REL-04: a change to a setting, a print template or a feature switch carries the version it was made to, and is
/// refused (409) when someone saved in between, so the last writer does not silently undo the other's change. A change
/// without a version saves as before. Each test puts back what it changed.
/// </summary>
public sealed class ConcurrentEditTests(HospitalServer server)
{
    [Fact]
    public async Task A_setting_saved_over_a_newer_one_is_refused()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var key = "tests.rel04." + Guid.NewGuid().ToString("N")[..12];
        var path = $"/api/v1/admin/settings/{key}";

        await admin.PutAsync(path, new SaveSettingRequest(JsonValue.Create(1), null, false, null));
        var first = await LoadAsync(admin, key);

        // Both administrators loaded version `first`; the first save wins and moves the version on.
        await admin.PutAsync(path, new SaveSettingRequest(JsonValue.Create(2), null, false, null, first.Version));
        var second = await LoadAsync(admin, key);
        second.Version.ShouldNotBe(first.Version);

        using (var stale = await admin.SendRawAsync(HttpMethod.Put, path, new SaveSettingRequest(JsonValue.Create(3), null, false, null, first.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await LoadAsync(admin, key)).Value!.GetValue<int>().ShouldBe(2);

        // Without a version, as older clients send it, the save goes through.
        await admin.PutAsync(path, new SaveSettingRequest(JsonValue.Create(4), null, false, null));
        (await LoadAsync(admin, key)).Value!.GetValue<int>().ShouldBe(4);
    }

    [Fact]
    public async Task A_print_template_saved_over_a_newer_one_is_refused()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        const string path = "/api/v1/admin/print-templates/bill";
        var bills = await BillsAsync(admin);
        var template = bills.Facility ?? bills.HospitalWide;
        try
        {
            await admin.PutAsync(path, new SavePrintTemplateRequest(template, ForThisFacility: true, bills.FacilityVersion));
            var loaded = (await BillsAsync(admin)).FacilityVersion.ShouldNotBeNull();

            await admin.PutAsync(path, new SavePrintTemplateRequest(template with { ClosingNote = "First" }, true, loaded));
            using (var stale = await admin.SendRawAsync(HttpMethod.Put, path, new SavePrintTemplateRequest(template with { ClosingNote = "Second" }, true, loaded)))
            {
                stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            }

            (await BillsAsync(admin)).Facility.ShouldNotBeNull().ClosingNote.ShouldBe("First");
        }
        finally
        {
            if (bills.Facility is null)
            {
                using var removed = await admin.SendRawAsync(HttpMethod.Delete, path + "/facility");
            }
            else
            {
                await admin.PutAsync(path, new SavePrintTemplateRequest(bills.Facility, true));
            }
        }
    }

    [Fact]
    public async Task A_feature_switch_changed_over_a_newer_change_is_refused()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var feature = (await admin.GetAsync<List<FeatureDto>>("/api/v1/admin/features")).First();
        var path = $"/api/v1/admin/features/{feature.Code}";

        try
        {
            // Saved as it already is first, so the switch has a stored value and a version.
            var saved = await admin.PutAsync<FeatureDto>(path, new SetFeatureRequest(feature.Enabled, feature.Version));
            var loaded = saved.Version.ShouldNotBeNull();
            (await admin.GetAsync<List<FeatureDto>>("/api/v1/admin/features")).Single(f => f.Key == feature.Key).Version.ShouldBe(loaded);

            // One administrator turns it over; another, still looking at version `loaded`, tries to turn it back.
            var changed = await admin.PutAsync<FeatureDto>(path, new SetFeatureRequest(!feature.Enabled, loaded));
            changed.Version.ShouldNotBe(loaded);

            using (var stale = await admin.SendRawAsync(HttpMethod.Put, path, new SetFeatureRequest(feature.Enabled, loaded)))
            {
                stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            }

            (await admin.GetAsync<List<FeatureDto>>("/api/v1/admin/features")).Single(f => f.Key == feature.Key).Enabled.ShouldBe(!feature.Enabled);
        }
        finally
        {
            await admin.PutAsync<FeatureDto>(path, new SetFeatureRequest(feature.Enabled));
        }
    }

    private static async Task<SettingDto> LoadAsync(ApiClient admin, string key) =>
        (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}")).Single(s => s.Key == key);

    private static async Task<PrintTemplateItem> BillsAsync(ApiClient admin) =>
        (await admin.GetAsync<PrintTemplateList>("/api/v1/admin/print-templates")).Templates.Single(t => t.Key == "bill");
}
