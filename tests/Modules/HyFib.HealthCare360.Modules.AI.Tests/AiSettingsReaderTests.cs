using System.Text.Json;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Claude;
using HyFib.HealthCare360.Platform.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.AI.Tests;

/// <summary>
/// AI configuration: off by default, the provider chosen by <c>ai.provider</c> per facility, a typo disables rather than
/// breaks or sends text elsewhere, and a key is read only when it would be used.
/// </summary>
public sealed class AiSettingsReaderTests
{
    private const string Off = "AI features are turned off for this facility (setting ai.enabled).";

    private readonly AiHarness _ai = new();

    private readonly AiTextProviderRegistry _registry =
        new([new ClaudeTextService(Substitute.For<IAnthropicClientProvider>(), NullLogger<ClaudeTextService>.Instance)]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<AiTextSettings> Settings() => (await _registry.SelectAsync(_ai.Reader, Ct)).Settings;

    private Task SecretNotRead(string key) =>
        _ai.Settings.DidNotReceive().GetAsync<JsonElement>(key, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());

    [Fact]
    public async Task Ai_is_off_until_an_administrator_enables_it()
    {
        var settings = await Settings();

        settings.ShouldBe(new AiTextSettings(false, "anthropic", ClaudeDefaults.Model, null, TimeSpan.FromSeconds(90)));
        settings.Reason.ShouldBe(Off);
        await SecretNotRead(AiSettingKeys.AnthropicSecret);
    }

    [Theory]
    [InlineData(null, "anthropic")]
    [InlineData("", "anthropic")]
    [InlineData("  ", "anthropic")]
    [InlineData("anthropic", "anthropic")]
    [InlineData("Anthropic", "anthropic")]
    public async Task Ai_provider_chooses_the_provider_and_unset_keeps_anthropic(string? configured, string expected)
    {
        _ai.TurnOn(configured);

        var selection = await _registry.SelectAsync(_ai.Reader, Ct);

        selection.Provider.ShouldNotBeNull().Key.ShouldBe(expected);
        selection.Settings.Provider.ShouldBe(expected);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("none")]
    [InlineData("openai-compatible")]
    [InlineData("azure")]
    public async Task A_provider_this_server_does_not_have_turns_ai_off_instead_of_falling_back(string configured)
    {
        _ai.TurnOn(configured);
        _ai.Secret(AiSettingKeys.AnthropicSecret, "\"sk-ant-api03-test\"");

        var selection = await _registry.SelectAsync(_ai.Reader, Ct);

        selection.Provider.ShouldBeNull();
        selection.Settings.IsUsable.ShouldBeFalse();
        selection.Settings.Reason.ShouldBe($"AI provider '{configured}' is not available.");
        await SecretNotRead(AiSettingKeys.AnthropicSecret);
    }

    [Fact]
    public async Task Each_facility_may_choose_its_own_provider()
    {
        var withoutAi = Guid.Parse("0199a1b2-0000-7000-8000-0000000000a1");
        _ai.TurnOn();
        // The settings service answers a facility's own value, or else the one for all facilities (unset here).
        _ai.Settings.GetAsync<string>(AiSettingKeys.Provider, withoutAi, Arg.Any<CancellationToken>()).Returns("disabled");

        _ai.User.FacilityId.Returns(withoutAi);
        (await Settings()).Provider.ShouldBe("disabled");

        _ai.User.FacilityId.Returns(Guid.Parse("0199a1b2-0000-7000-8000-0000000000b2"));
        (await Settings()).Provider.ShouldBe("anthropic");
    }

    [Theory]
    [InlineData("\"sk-ant-api03-test\"")]
    [InlineData("""{ "apiKey": " sk-ant-api03-test " }""")]
    public async Task Reads_the_api_key_as_a_string_or_an_object(string secret)
    {
        _ai.TurnOn();
        _ai.Secret(AiSettingKeys.AnthropicSecret, secret);

        var settings = await Settings();

        settings.ApiKey.ShouldBe("sk-ant-api03-test");
        settings.IsUsable.ShouldBeTrue();
    }

    [Fact]
    public async Task Enabled_without_a_key_explains_what_is_missing()
    {
        _ai.TurnOn();

        (await Settings()).Reason.ShouldBe("No Anthropic API key is configured (secret setting ai.anthropic.secret).");
    }

    [Theory]
    [InlineData("claude-sonnet-5", "claude-sonnet-5")]
    [InlineData("  claude-haiku-4-5  ", "claude-haiku-4-5")]
    [InlineData("Claude Opus!", ClaudeDefaults.Model)]
    [InlineData("../../v1/admin", ClaudeDefaults.Model)]
    public async Task Uses_a_valid_configured_model_or_the_default(string configured, string expected)
    {
        _ai.Setting(AiSettingKeys.Model, configured);

        (await Settings()).Model.ShouldBe(expected);
    }

    [Theory]
    [InlineData(5, 10)]
    [InlineData(60, 60)]
    [InlineData(1000, 300)]
    public async Task Bounds_the_request_timeout(int configured, int expected)
    {
        _ai.Setting<int?>(AiSettingKeys.TimeoutSeconds, configured);

        (await Settings()).Timeout.ShouldBe(TimeSpan.FromSeconds(expected));
    }

    [Fact]
    public async Task A_malformed_setting_disables_ai_instead_of_failing()
    {
        _ai.Settings.GetAsync<bool?>(AiSettingKeys.Enabled, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns<bool?>(_ => throw new JsonException("bad"));

        (await Settings()).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Per_user_limits_default_to_60_an_hour_and_300_a_day()
    {
        var limits = await _ai.Reader.GetLimitsAsync(Ct);

        limits.PerUserPerHour.ShouldBe(60);
        limits.PerUserPerDay.ShouldBe(300);
    }

    [Fact]
    public void Two_providers_cannot_share_a_key() =>
        Should.Throw<InvalidOperationException>(() => new AiTextProviderRegistry([new FakeTextProvider("same"), new FakeTextProvider("same")]));

    [Fact]
    public void The_disabled_service_never_sends_anything()
    {
        var service = new DisabledAiTextService();

        service.IsEnabled.ShouldBeFalse();
        DisabledAiTextService.Refuse(null).ShouldBe(new AiCompletion(false, null, null, 0, 0, "AI features are turned off."));
    }
}
