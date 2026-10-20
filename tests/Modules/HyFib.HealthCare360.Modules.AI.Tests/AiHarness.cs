using System.Text.Json;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Privacy;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.AI.Tests;

/// <summary>
/// The gateway as the server builds it (the real settings reader, provider registry and router) over substituted
/// settings, patient directory and usage recorder. AI is off, as on a new server, until a test turns it on.
/// </summary>
internal sealed class AiHarness
{
    public AiHarness()
    {
        // The real settings service answers null for missing keys (NSubstitute would answer "" for strings).
        Setting<bool?>(AiSettingKeys.Enabled, null);
        Setting<string>(AiSettingKeys.Provider, null);
        Setting<string>(AiSettingKeys.Model, null);
        Setting<int?>(AiSettingKeys.TimeoutSeconds, null);
        Patients.RedactionContextAsync(default, default).ReturnsForAnyArgs(RedactionContext.Empty);
    }

    public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();

    public ICurrentUser User { get; } = Substitute.For<ICurrentUser>();

    public IPatientDirectory Patients { get; } = Substitute.For<IPatientDirectory>();

    public IAiUsageRecorder Usage { get; } = Substitute.For<IAiUsageRecorder>();

    public TestClock Clock { get; } = new();

    public AiSettingsReader Reader => new(Settings, User, NullLogger<AiSettingsReader>.Instance);

    public void Setting<T>(string key, T? value) =>
        Settings.GetAsync<T>(key, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(value);

    public void Secret(string key, string json) => Setting(key, JsonDocument.Parse(json).RootElement.Clone());

    /// <summary>Allows AI, with <paramref name="provider"/> chosen (null leaves <c>ai.provider</c> unset).</summary>
    public AiHarness TurnOn(string? provider = null)
    {
        Setting<bool?>(AiSettingKeys.Enabled, true);
        Setting(AiSettingKeys.Provider, provider);
        return this;
    }

    public AiTextServiceRouter Router(params IAiTextProvider[] providers) =>
        new(Reader, new AiTextProviderRegistry(providers), Patients, Usage, Clock);

    /// <summary>The usage records written, in order.</summary>
    public IReadOnlyList<AiUsageRecord> Recorded =>
        [.. Usage.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IAiUsageRecorder.RecordAsync)).Select(c => (AiUsageRecord)c.GetArguments()[0]!)];
}

/// <summary>
/// A text provider that keeps what it is handed and answers as told: a stand-in for any provider, for testing the
/// features and the gateway's guards on their own.
/// </summary>
internal sealed class FakeTextProvider(string key = AiProviders.Anthropic) : IAiTextProvider
{
    private Func<CancellationToken, Task<AiCompletion>> _answer = _ => Task.FromResult(new AiCompletion(true, "{}", "test-model", 10, 5, null));

    public string Key => key;

    /// <summary>What it was handed, in order.</summary>
    public List<RedactedAiRequest> Received { get; } = [];

    /// <summary>Its reason it cannot be used while AI is on (a missing key, say), or null.</summary>
    public string? Problem { get; set; }

    public FakeTextProvider Answers(AiCompletion completion)
    {
        _answer = _ => Task.FromResult(completion);
        return this;
    }

    public FakeTextProvider Answers(string text, int inputTokens = 1200, int outputTokens = 300) =>
        Answers(new AiCompletion(true, text, "test-model", inputTokens, outputTokens, null));

    public FakeTextProvider Throws(Exception exception)
    {
        _answer = _ => Task.FromException<AiCompletion>(exception);
        return this;
    }

    /// <summary>Never answers: the call waits until the caller gives up.</summary>
    public FakeTextProvider Hangs()
    {
        _answer = async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        };
        return this;
    }

    public Task<AiTextSettings> ReadSettingsAsync(AiSettingsReader settings, bool enabled, CancellationToken cancellationToken) =>
        Task.FromResult(new AiTextSettings(enabled, key, "test-model", null, TimeSpan.FromSeconds(60), Problem: enabled ? Problem : null));

    public Task<AiCompletion> CompleteAsync(RedactedAiRequest request, AiTextSettings settings, CancellationToken cancellationToken)
    {
        Received.Add(request);
        return _answer(cancellationToken);
    }
}
