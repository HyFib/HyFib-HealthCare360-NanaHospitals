using System.Text.Json;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Claude;
using HyFib.HealthCare360.Modules.AI.Services.OpenAi;
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

    private readonly AiTextProviderRegistry _registry = new([
        new ClaudeTextService(Substitute.For<IAnthropicClientProvider>(), NullLogger<ClaudeTextService>.Instance),
        new OpenAiCompatibleTextService(new HttpClient(new StubHttpHandler()), NullLogger<OpenAiCompatibleTextService>.Instance),
    ]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<AiTextSettings> Settings() => (await _registry.SelectAsync(_ai.Reader, Ct)).Settings;

    private void OpenAi(string? baseUrl = "https://llm.hospital.lan/v1", string? model = "local-model", int timeoutSeconds = 90) =>
        _ai.Setting(AiSettingKeys.OpenAi, new OpenAiSettings { BaseUrl = baseUrl, Model = model, TimeoutSeconds = timeoutSeconds });

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
    [InlineData("openai", "openai")]
    [InlineData(" OpenAI ", "openai")]
    public async Task Ai_provider_chooses_the_provider_and_unset_keeps_anthropic(string? configured, string expected)
    {
        _ai.TurnOn(configured);
        OpenAi();

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
        OpenAi();

        var selection = await _registry.SelectAsync(_ai.Reader, Ct);

        selection.Provider.ShouldBeNull();
        selection.Settings.IsUsable.ShouldBeFalse();
        selection.Settings.Reason.ShouldBe($"AI provider '{configured}' is not available.");
        await SecretNotRead(AiSettingKeys.AnthropicSecret);
        await SecretNotRead(AiSettingKeys.OpenAiSecret);
    }

    [Fact]
    public async Task Each_facility_may_choose_its_own_provider()
    {
        var withOwnServer = Guid.Parse("0199a1b2-0000-7000-8000-0000000000a1");
        _ai.TurnOn();
        OpenAi();
        // The settings service answers a facility's own value, or else the one for all facilities (unset here).
        _ai.Settings.GetAsync<string>(AiSettingKeys.Provider, withOwnServer, Arg.Any<CancellationToken>()).Returns("openai");

        _ai.User.FacilityId.Returns(withOwnServer);
        (await Settings()).Provider.ShouldBe("openai");

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
    public async Task Reads_the_openai_compatible_server_and_its_key_while_ai_is_on()
    {
        _ai.TurnOn("openai");
        OpenAi(timeoutSeconds: 240);
        _ai.Secret(AiSettingKeys.OpenAiSecret, """{ "apiKey": "server-key" }""");

        var settings = await Settings();

        settings.ShouldBe(new AiTextSettings(true, "openai", "local-model", "server-key", TimeSpan.FromSeconds(240),
            new Uri("https://llm.hospital.lan/v1/chat/completions")));
        settings.IsUsable.ShouldBeTrue();
        await SecretNotRead(AiSettingKeys.AnthropicSecret);
    }

    [Fact]
    public async Task The_openai_compatible_key_is_optional_and_never_read_while_ai_is_off()
    {
        _ai.TurnOn("openai");
        OpenAi("http://localhost:8000/v1/");

        var settings = await Settings();
        settings.IsUsable.ShouldBeTrue("A server on this computer may need no key.");
        settings.ApiKey.ShouldBeNull();

        _ai.Setting<bool?>(AiSettingKeys.Enabled, false);
        _ai.Settings.ClearReceivedCalls();
        (await Settings()).Reason.ShouldBe(Off);
        await SecretNotRead(AiSettingKeys.OpenAiSecret);
    }

    [Theory]
    [InlineData("https://llm.hospital.lan/v1", "https://llm.hospital.lan/v1/chat/completions")]
    [InlineData("https://res.openai.azure.com/openai/v1/", "https://res.openai.azure.com/openai/v1/chat/completions")]
    [InlineData("https://llm.hospital.lan:8443/v1/chat/completions", "https://llm.hospital.lan:8443/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://127.0.0.1:8080/v1", "http://127.0.0.1:8080/v1/chat/completions")]
    [InlineData("http://[::1]:8000/v1", "http://[::1]:8000/v1/chat/completions")]
    public async Task Sends_to_chat_completions_under_the_configured_address(string baseUrl, string expected)
    {
        _ai.TurnOn("openai");
        OpenAi(baseUrl);

        (await Settings()).Endpoint.ShouldBe(new Uri(expected));
    }

    [Theory]
    [InlineData("http://llm.hospital.lan/v1")]
    [InlineData("http://10.0.0.5:8000/v1")]
    [InlineData("http://192.168.1.20:11434/v1")]
    [InlineData("https://user:secret@llm.hospital.lan/v1")]
    [InlineData("https://llm.hospital.lan/v1?api-key=secret")]
    [InlineData("https://llm.hospital.lan/v1#models")]
    [InlineData("ftp://llm.hospital.lan/v1")]
    [InlineData("llm.hospital.lan/v1")]
    [InlineData(null)]
    public async Task Text_goes_only_to_an_https_address_or_to_this_computer(string? baseUrl)
    {
        _ai.TurnOn("openai");
        OpenAi(baseUrl);
        _ai.Secret(AiSettingKeys.OpenAiSecret, "\"server-key\"");

        var settings = await Settings();

        settings.Endpoint.ShouldBeNull();
        settings.Reason.ShouldBe(
            "The AI server address (setting ai.openai.baseUrl) must be an https address without a user name, password or query (plain http only to localhost).");
        await SecretNotRead(AiSettingKeys.OpenAiSecret);
    }

    [Theory]
    [InlineData("local-model", "local-model")]
    [InlineData(" Org/Model-7B-Instruct ", "Org/Model-7B-Instruct")]
    [InlineData("model:8b", "model:8b")]
    [InlineData("ward_notes.v2", "ward_notes.v2")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("two words", null)]
    [InlineData("-leading-dash", null)]
    public async Task The_openai_compatible_server_needs_the_name_of_its_model(string? configured, string? expected)
    {
        _ai.TurnOn("openai");
        OpenAi(model: configured);

        var settings = await Settings();

        if (expected is null)
        {
            settings.Reason.ShouldBe("No model is set for the AI server (setting ai.openai.model).");
        }
        else
        {
            settings.Model.ShouldBe(expected);
            settings.IsUsable.ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(5, 10)]
    [InlineData(1000, 300)]
    public async Task Bounds_the_openai_compatible_time_limit(int configured, int expected)
    {
        _ai.TurnOn("openai");
        OpenAi(timeoutSeconds: configured);

        (await Settings()).Timeout.ShouldBe(TimeSpan.FromSeconds(expected));
    }

    [Fact]
    public async Task A_malformed_setting_disables_ai_instead_of_failing()
    {
        _ai.Settings.GetAsync<bool?>(AiSettingKeys.Enabled, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns<bool?>(_ => throw new JsonException("bad"));

        (await Settings()).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_malformed_server_setting_disables_ai_instead_of_failing()
    {
        _ai.TurnOn("openai");
        _ai.Settings.GetAsync<OpenAiSettings>(AiSettingKeys.OpenAi, Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns<OpenAiSettings?>(_ => throw new JsonException("bad"));

        (await Settings()).IsUsable.ShouldBeFalse();
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
