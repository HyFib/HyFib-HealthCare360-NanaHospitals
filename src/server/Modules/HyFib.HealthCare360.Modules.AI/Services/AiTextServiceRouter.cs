using HyFib.HealthCare360.Modules.AI.Services.Privacy;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Services;

/// <summary>
/// This server's <see cref="IAiTextService"/> and the module's <see cref="IAiGateway"/>: the one way to an AI text
/// provider, so the guards every provider is subject to (SEC-12, ADR-011) sit here, around the provider call, and no
/// provider can skip them:
/// <list type="number">
/// <item>Nothing is sent while AI is off (<c>ai.enabled</c>, off by default) or the chosen provider is not configured.</item>
/// <item>The provider is handed a <see cref="RedactedAiRequest"/>: identifiers are removed from the system prompt and every
/// message, with those on file for the patient in context.</item>
/// <item>Every call that reached a provider is recorded (usage row and clinical audit entry), whatever happened: an
/// answer, a failure, an unreadable answer, an exception or a caller that gave up.</item>
/// <item>The answer goes only to the feature's reader, which treats it as untrusted input; a clinical answer becomes a
/// draft the clinician must review (<see cref="Clinical.ClinicalNoteStructurer"/>). Nothing is saved or signed here.</item>
/// </list>
/// The provider is chosen by <c>ai.provider</c> (<see cref="AiTextProviderRegistry"/>). Settings are read once per scope
/// (the settings service caches them), so switching AI off takes effect on the next request.
/// </summary>
internal sealed class AiTextServiceRouter(
    AiSettingsReader settings, AiTextProviderRegistry providers, IPatientDirectory patients, IAiUsageRecorder usage, TimeProvider time)
    : IAiTextService, IAiGateway
{
    private AiTextSelection? _selection;

    /// <remarks>
    /// The platform contract is synchronous. The first read in a scope blocks on the (memory-cached) settings lookup;
    /// this module's own code uses <see cref="GetAvailabilityAsync"/> instead.
    /// </remarks>
    public bool IsEnabled => (_selection ??= providers.SelectAsync(settings, CancellationToken.None).GetAwaiter().GetResult()).Settings.IsUsable;

    public async Task<AiAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        var current = (await SelectAsync(cancellationToken)).Settings;
        return new AiAvailability(current.IsUsable, current.Provider, current.IsUsable ? current.Model : null, current.Reason, current.Timeout);
    }

    public async Task<AiOutcome<T>> CompleteAsync<T>(AiCall<T> call, CancellationToken cancellationToken)
        where T : class
    {
        var (provider, current) = await SelectAsync(cancellationToken);
        if (provider is null || !current.IsUsable)
        {
            return new AiOutcome<T>(null, DisabledAiTextService.Refuse(current.Reason), 0);
        }

        var request = RedactedAiRequest.Redact(call.Request, await patients.RedactionContextAsync(call.PatientId, cancellationToken));
        var started = time.GetTimestamp();
        AiCompletion? completion = null;
        T? value = null;
        try
        {
            completion = await provider.CompleteAsync(request, current, cancellationToken);
            value = completion.Success ? call.Read(completion.Text!, request.Redaction) : null;
            return new AiOutcome<T>(value, completion, request.Redaction.Total);
        }
        finally
        {
            var error = completion is null ? "The call ended before the AI service answered."
                : !completion.Success ? completion.Error
                : value is null ? call.Unreadable
                : null;
            await usage.RecordAsync(new AiUsageRecord(
                call.Request.Purpose,
                current.Provider,
                completion?.Model ?? current.Model,
                completion?.InputTokens ?? 0,
                completion?.OutputTokens ?? 0,
                time.GetElapsedTime(started),
                value is not null,
                error,
                call.PatientId), CancellationToken.None);
        }
    }

    /// <summary>
    /// The platform contract, for other modules: the same guards. No patient is known here, so the redaction removes the
    /// identifiers it recognises by their form (phone numbers, UHIDs, ABHA and Aadhaar numbers, e-mail addresses).
    /// </summary>
    async Task<AiCompletion> IAiTextService.CompleteAsync(AiRequest request, CancellationToken cancellationToken) =>
        (await CompleteAsync(new AiCall<string>(request, null, (answer, _) => answer, "The answer could not be read."), cancellationToken)).Completion;

    private async Task<AiTextSelection> SelectAsync(CancellationToken cancellationToken) =>
        _selection ??= await providers.SelectAsync(settings, cancellationToken);
}
