using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Integration credentials are encrypted at rest (SRS §5) whatever the caller asks for: a <c>*.secret</c> key saved
/// without the secret flag, over the API or by a module, is still stored encrypted and never shown back.
/// </summary>
public sealed class SecretSettingsTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_secret_key_saved_over_the_api_without_the_flag_is_stored_encrypted()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var key = $"integrations.test{Guid.NewGuid():N}.secret";
        const string credential = "plain-api-key-0000";

        await admin.SaveSettingAsync(key,
            new SaveSettingRequest(JsonNode.Parse($$"""{"apiKey":"{{credential}}"}"""), null, false, null));
        try
        {
            var row = await RowAsync(key);
            row.IsSecret.ShouldBeTrue();
            row.Value.ShouldNotContain(credential);
            var listed = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}")).ShouldHaveSingleItem();
            listed.IsSecret.ShouldBeTrue();
            listed.Value.ShouldBeNull();
            listed.HasValue.ShouldBeTrue();

            await using var scope = server.Services.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            (await settings.GetAsync<JsonObject>(key, null, Ct))!["apiKey"]!.GetValue<string>().ShouldBe(credential);
        }
        finally
        {
            await RemoveAsync(key);
        }
    }

    [Fact]
    public async Task A_module_saving_a_secret_key_without_the_flag_still_encrypts_it()
    {
        var key = $"ai.test{Guid.NewGuid():N}.secret";
        try
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
                await settings.SetAsync(key, "sk-test-credential", secret: false, cancellationToken: Ct);
                (await settings.GetAsync<string>(key, null, Ct)).ShouldBe("sk-test-credential");
            }

            var row = await RowAsync(key);
            row.IsSecret.ShouldBeTrue();
            row.Value.ShouldNotContain("sk-test-credential");
        }
        finally
        {
            await RemoveAsync(key);
        }
    }

    private async Task<SystemSetting> RowAsync(string key)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Set<SystemSetting>().AsNoTracking().SingleAsync(s => s.Key == key, Ct);
    }

    private async Task RemoveAsync(string key)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(key, null, Ct);
    }
}
