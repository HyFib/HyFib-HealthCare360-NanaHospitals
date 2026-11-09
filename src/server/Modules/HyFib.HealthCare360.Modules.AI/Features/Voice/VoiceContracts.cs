namespace HyFib.HealthCare360.Modules.AI.Features.Voice;

/// <summary>Transcribed dictation; the text is returned to the caller only and never stored or logged.</summary>
/// <param name="SentMs">How much audio was sent to the speech service, in milliseconds: the speech alone, silence taken out (0 when nobody spoke and nothing was sent).</param>
public sealed record TranscriptionResponse(string Text, string Provider, string Language, int DurationMs, int SentMs = 0);
