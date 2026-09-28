namespace HyFib.HealthCare360.Modules.AI.Features.Voice;

/// <summary>Transcribed dictation; the text is returned to the caller only and never stored or logged.</summary>
public sealed record TranscriptionResponse(string Text, string Provider, string Language, int DurationMs);
