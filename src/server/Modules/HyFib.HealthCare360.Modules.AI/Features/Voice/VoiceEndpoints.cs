using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.AI.Services;
using HyFib.HealthCare360.Modules.AI.Services.Speech;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.AspNetCore.Mvc;

namespace HyFib.HealthCare360.Modules.AI.Features.Voice;

/// <summary>
/// /api/v1/ai/voice/transcribe — server speech-to-text for browsers and tablets without offline speech. Multipart
/// field <c>audio</c> (16-bit PCM WAV, at most 10 MB, content type must match the file signature), optional
/// <c>language</c> (ISO 639-1) and <c>patientId</c> (for the audit trail). Nothing is sent unless the facility's AI
/// processing agreement is recorded (<see cref="AiSettingKeys.ProcessingAgreement"/>); then only the speech is sent,
/// silence taken out, and at most <see cref="PcmAudio.MaxSpeech"/> of it (<see cref="PcmAudio"/>).
/// </summary>
internal static partial class VoiceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/ai/voice/transcribe", Transcribe)
            .WithoutStoredResponse()
            .WithTags("AI assistant")
            .RequireAnyPermission(AIPermissions.DictationUse, AIPermissions.AssistantUse)
            .RequireRateLimiting(AiRateLimits.Policy)
            // Bearer-token API called by the SPA/Desktop (no cookie auth), so antiforgery tokens do not apply.
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(AudioFormat.MaxBytes + (64 * 1024)));
    }

    private static async Task<IResult> Transcribe(
        IFormFile? audio,
        [FromForm] string? language,
        [FromForm] Guid? patientId,
        SpeechToTextRouter speech,
        IAiQuota quota,
        IAiUsageRecorder usage,
        ISettingsService settings,
        ICurrentUser user,
        TimeProvider time,
        CancellationToken ct)
    {
        var config = await speech.GetConfigAsync(ct);
        if (!config.IsUsable)
        {
            return AiProblems.SpeechDisabled(config.Reason);
        }

        // The facility's agreement with the speech service comes before anything about the recording is looked at.
        if (await settings.GetAsync<bool?>(AiSettingKeys.ProcessingAgreement, user.FacilityId, ct) != true)
        {
            return AiProblems.NoProcessingAgreement();
        }

        if (audio is null || audio.Length == 0)
        {
            return Error.Field("audio", "Attach the recorded audio.").ToProblem();
        }

        if (audio.Length > AudioFormat.MaxBytes)
        {
            return Error.Field("audio", "Recordings must be 10 MB or smaller.").ToProblem();
        }

        var mediaType = AudioFormat.Normalize(audio.ContentType);
        if (mediaType is null)
        {
            return Error.Field("audio", "Use WAV audio.").ToProblem();
        }

        var lang = string.IsNullOrWhiteSpace(language) ? config.Settings.Language : language.Trim().ToLowerInvariant();
        if (!LanguagePattern().IsMatch(lang))
        {
            return Error.Field("language", "Use a two-letter language code such as en or hi.").ToProblem();
        }

        await using (var probe = audio.OpenReadStream())
        {
            var header = new byte[12];
            var read = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
            if (!AudioFormat.MatchesSignature(mediaType, header.AsSpan(0, read)))
            {
                return Error.Field("audio", "The file is not valid audio of the declared type.").ToProblem();
            }
        }

        // Compressed audio cannot be looked inside for silence here, so only PCM WAV is sent (the staff app converts).
        if (mediaType != "audio/wav")
        {
            return Error.Field("audio", "Send the recording as WAV: the server takes the silence out before any of it is sent, and cannot in compressed audio.").ToProblem();
        }

        byte[] recorded;
        await using (var read = audio.OpenReadStream())
        using (var buffer = new MemoryStream((int)audio.Length))
        {
            await read.CopyToAsync(buffer, ct);
            recorded = buffer.ToArray();
        }

        if (!PcmAudio.TryRead(recorded, out var clip, out var problem))
        {
            return Error.Field("audio", problem).ToProblem();
        }

        var spoken = PcmAudio.StripSilence(clip);
        if (spoken.Samples.Length == 0)
        {
            // Nobody spoke: nothing is sent, and nothing is counted against the quota.
            return TypedResults.Ok(new TranscriptionResponse(string.Empty, AiProviders.Whisper, lang, 0));
        }

        if (spoken.Duration > PcmAudio.MaxSpeech)
        {
            return Error.Field("audio", $"Dictate at most {PcmAudio.MaxSpeech.TotalMinutes:0} minutes at a time. Nothing was sent.").ToProblem();
        }

        if (await quota.CheckAsync(ct) is { } limited)
        {
            return AiProblems.QuotaExceeded(limited);
        }

        var started = time.GetTimestamp();
        string? text = null;
        string? error = null;
        try
        {
            await using var upload = new MemoryStream(PcmAudio.ToWav(spoken));
            text = await speech.TranscribeAsync(upload, mediaType, lang, ct);
        }
        catch (SpeechToTextException ex)
        {
            error = ex.Message;
        }

        var elapsed = time.GetElapsedTime(started);
        await usage.RecordAsync(new AiUsageRecord(AiPrompts.TranscribePurpose, AiProviders.Whisper, config.Settings.Model, 0, 0, elapsed,
            error is null, error, patientId), ct);
        return error is null
            ? TypedResults.Ok(new TranscriptionResponse(text ?? string.Empty, AiProviders.Whisper, lang, (int)elapsed.TotalMilliseconds,
                (int)spoken.Duration.TotalMilliseconds))
            : AiProblems.ProviderFailed(error);
    }

    [GeneratedRegex("^[a-z]{2}$")]
    private static partial Regex LanguagePattern();
}
