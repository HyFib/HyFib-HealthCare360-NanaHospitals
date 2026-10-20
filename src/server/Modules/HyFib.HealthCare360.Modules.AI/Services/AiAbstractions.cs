using HyFib.HealthCare360.Modules.AI.Services.Privacy;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Services;

/// <summary>
/// Whether the AI text provider may be called for the current facility, which model it would use, and how long a call
/// may take (the chosen provider's time limit).
/// </summary>
internal sealed record AiAvailability(bool Enabled, string Provider, string? Model, string? Reason, TimeSpan Timeout);

/// <summary>
/// Async-first view of the configured AI text provider for this module, and the one way to call it. The platform's
/// <see cref="IAiTextService"/> exposes a synchronous <c>IsEnabled</c>, which cannot read settings without blocking.
/// </summary>
internal interface IAiGateway
{
    Task<AiAvailability> GetAvailabilityAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends <paramref name="call"/> to the chosen provider, the same way whichever it is: nothing is sent while AI is off
    /// or the provider is not configured; otherwise the provider gets the request with the identifiers removed, and the
    /// call is recorded whatever its outcome.
    /// </summary>
    Task<AiOutcome<T>> CompleteAsync<T>(AiCall<T> call, CancellationToken cancellationToken)
        where T : class;
}

/// <summary>
/// One AI call as a feature asks for it. <see cref="Request"/> holds the user's own words: the gateway removes the
/// identifiers, including those on file for <see cref="PatientId"/>, before any provider sees them. <see cref="Read"/>
/// turns the answer into the feature's result, with the placeholders that were used, or gives null when it cannot; the
/// call is then recorded as failed with <see cref="Unreadable"/>.
/// </summary>
internal sealed record AiCall<T>(AiRequest Request, Guid? PatientId, Func<string, RedactionResult, T?> Read, string Unreadable)
    where T : class;

/// <summary>
/// What came back: the value read from the answer (null when there is none), the provider's completion (its error, or
/// the reason nothing was sent), and how many identifiers were replaced before sending.
/// </summary>
internal sealed record AiOutcome<T>(T? Value, AiCompletion Completion, int IdentifiersRedacted)
    where T : class;

/// <summary>
/// One kind of AI text service, chosen by its <see cref="Key"/> in the <c>ai.provider</c> setting. A provider only reads
/// its own settings and sends what it is given. It is reached only through <see cref="AiTextServiceRouter"/>, which
/// decides whether AI may be used, hands it the request with identifiers already removed and records the call: the same
/// guards for every provider, which no provider can skip (SEC-12).
/// </summary>
internal interface IAiTextProvider
{
    /// <summary>The <c>ai.provider</c> value that selects this provider (lower case).</summary>
    string Key { get; }

    /// <summary>
    /// This provider's settings for the current facility. Secrets are decrypted only while <paramref name="enabled"/>,
    /// and the reason it cannot be used (no key, no address) is looked for only then.
    /// </summary>
    Task<AiTextSettings> ReadSettingsAsync(AiSettingsReader settings, bool enabled, CancellationToken cancellationToken);

    /// <summary>
    /// Sends the redacted request. Failures come back as a failed completion with a staff-facing message; logs carry the
    /// error category only, never text.
    /// </summary>
    Task<AiCompletion> CompleteAsync(RedactedAiRequest request, AiTextSettings settings, CancellationToken cancellationToken);
}

/// <summary>What is recorded for one AI call — never the text itself.</summary>
internal sealed record AiUsageRecord(
    string Purpose,
    string Provider,
    string? Model,
    int InputTokens,
    int OutputTokens,
    TimeSpan Latency,
    bool Success,
    string? Error,
    Guid? PatientId);

/// <summary>Writes the usage row and the clinical audit entry (<c>ai.&lt;purpose&gt;</c>) for an AI call.</summary>
internal interface IAiUsageRecorder
{
    Task RecordAsync(AiUsageRecord usage, CancellationToken cancellationToken);
}

/// <summary>Per-user AI quotas.</summary>
internal interface IAiQuota
{
    /// <summary>Null when the current user may make another AI call; otherwise the staff-facing reason.</summary>
    Task<string?> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>The few patient facts the AI module needs: UHID look-up and the identifiers to redact.</summary>
internal interface IPatientDirectory
{
    Task<Guid?> FindByUhidAsync(string uhid, CancellationToken cancellationToken);

    Task<RedactionContext> RedactionContextAsync(Guid? patientId, CancellationToken cancellationToken);
}
