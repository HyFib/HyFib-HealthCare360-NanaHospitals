using System.Text.Json;
using Anthropic;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Claude;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.AI.Tests;

/// <summary>AI configuration: off by default, a typo disables rather than breaks, and the key is read only when used.</summary>
public sealed class AiSettingsReaderTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IAnthropicClientProvider _clients = Substitute.For<IAnthropicClientProvider>();

    public AiSettingsReaderTests()
    {
        // The real settings service answers null for missing keys (NSubstitute would answer "" for strings).
        Setting<bool?>(AiSettingKeys.Enabled, null);
        Setting<string>(AiSettingKeys.Provider, null);
        Setting<string>(AiSettingKeys.Model, null);
        Setting<int?>(AiSettingKeys.TimeoutSeconds, null);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AiSettingsReader Reader => new(_settings, Substitute.For<ICurrentUser>(), NullLogger<AiSettingsReader>.Instance);

    private void Setting<T>(string key, T? value) =>
        _settings.GetAsync<T>(key, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(value);

    private void Secret(string json) => Setting(AiSettingKeys.AnthropicSecret, JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public async Task Ai_is_off_until_an_administrator_enables_it()
    {
        var settings = await Reader.GetTextSettingsAsync(Ct);

        settings.ShouldBe(new AiTextSettings(false, "anthropic", "claude-opus-5-5", null, TimeSpan.FromSeconds(90)));
        settings.Reason.ShouldBe("AI features are turned off for this facility (setting ai.enabled).");
        await _settings.DidNotReceive().GetAsync<JsonElement>(AiSettingKeys.AnthropicSecret, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("\"sk-ant-api03-test\"")]
    [InlineData("""{ "apiKey": " sk-ant-api03-test " }""")]
    public async Task Reads_the_api_key_as_a_string_or_an_object(string secret)
    {
        Setting<bool?>(AiSettingKeys.Enabled, true);
        Secret(secret);

        var settings = await Reader.GetTextSettingsAsync(Ct);

        settings.ApiKey.ShouldBe("sk-ant-api03-test");
        settings.IsUsable.ShouldBeTrue();
    }

    [Fact]
    public async Task Enabled_without_a_key_explains_what_is_missing()
    {
        Setting<bool?>(AiSettingKeys.Enabled, true);

        (await Reader.GetTextSettingsAsync(Ct)).Reason.ShouldBe("No Anthropic API key is configured (secret setting ai.anthropic.secret).");
    }

    [Theory]
    [InlineData("claude-sonnet-5", "claude-sonnet-5")]
    [InlineData("  claude-haiku-4-5  ", "claude-haiku-4-5")]
    [InlineData("Claude Opus!", "claude-opus-5-5")]
    [InlineData("../../v1/admin", "claude-opus-5-5")]
    public async Task Uses_a_valid_configured_model_or_the_default(string configured, string expected)
    {
        Setting<string>(AiSettingKeys.Model, configured);

        (await Reader.GetTextSettingsAsync(Ct)).Model.ShouldBe(expected);
    }

    [Theory]
    [InlineData(5, 10)]
    [InlineData(60, 60)]
    [InlineData(1000, 300)]
    public async Task Bounds_the_request_timeout(int configured, int expected)
    {
        Setting<int?>(AiSettingKeys.TimeoutSeconds, configured);

        (await Reader.GetTextSettingsAsync(Ct)).Timeout.ShouldBe(TimeSpan.FromSeconds(expected));
    }

    [Fact]
    public async Task Another_provider_never_decrypts_the_key()
    {
        Setting<bool?>(AiSettingKeys.Enabled, true);
        Setting<string>(AiSettingKeys.Provider, "None");

        var settings = await Reader.GetTextSettingsAsync(Ct);

        settings.Reason.ShouldBe("AI provider 'none' is not available.");
        await _settings.DidNotReceive().GetAsync<JsonElement>(AiSettingKeys.AnthropicSecret, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_malformed_setting_disables_ai_instead_of_failing()
    {
        _settings.GetAsync<bool?>(AiSettingKeys.Enabled, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns<bool?>(_ => throw new JsonException("bad"));

        (await Reader.GetTextSettingsAsync(Ct)).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Per_user_limits_default_to_60_an_hour_and_300_a_day()
    {
        var limits = await Reader.GetLimitsAsync(Ct);

        limits.PerUserPerHour.ShouldBe(60);
        limits.PerUserPerDay.ShouldBe(300);
    }

    [Fact]
    public async Task The_router_refuses_without_calling_claude_while_ai_is_off()
    {
        var router = new AiTextServiceRouter(Reader, new ClaudeTextService(_clients, NullLogger<ClaudeTextService>.Instance));

        var availability = await router.GetAvailabilityAsync(Ct);
        var completion = await router.CompleteAsync(AiPrompts.StructureNote("fever for two days", null), Ct);

        availability.ShouldBe(new AiAvailability(false, "anthropic", null, "AI features are turned off for this facility (setting ai.enabled)."));
        completion.Success.ShouldBeFalse();
        completion.Error.ShouldBe("AI features are turned off for this facility (setting ai.enabled).");
        router.IsEnabled.ShouldBeFalse();
        _clients.DidNotReceiveWithAnyArgs().GetClient(default!, default);
    }

    [Fact]
    public async Task The_router_reports_the_model_when_ai_is_usable()
    {
        Setting<bool?>(AiSettingKeys.Enabled, true);
        Secret("\"sk-ant-api03-test\"");
        _clients.GetClient(default!, default).ReturnsForAnyArgs(Substitute.For<IAnthropicClient>());
        var router = new AiTextServiceRouter(Reader, new ClaudeTextService(_clients, NullLogger<ClaudeTextService>.Instance));

        (await router.GetAvailabilityAsync(Ct)).ShouldBe(new AiAvailability(true, "anthropic", "claude-opus-5-5", null));
        router.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void The_disabled_service_never_sends_anything()
    {
        var service = new DisabledAiTextService();

        service.IsEnabled.ShouldBeFalse();
        DisabledAiTextService.Refuse(null).ShouldBe(new AiCompletion(false, null, null, 0, 0, "AI features are turned off."));
    }
}
