namespace HyFib.HealthCare360.Modules.AI.Features.Status;

/// <summary>
/// What the UI needs to decide which AI actions to show (no secrets). <see cref="TimeoutSeconds"/> is how long an AI
/// call may take on the server (<c>ai.timeoutSeconds</c>, retries included), so the UI waits a little longer than that.
/// </summary>
public sealed record AiStatusDto(
    bool Enabled,
    string Provider,
    string? Model,
    string? Reason,
    bool ServerSpeechToText,
    string SpeechProvider,
    string? SpeechReason,
    int TimeoutSeconds);

/// <summary>One recorded AI call (never contains text).</summary>
public sealed record AiUsageDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    Guid? UserId,
    Guid? PatientId,
    string Purpose,
    string Provider,
    string? Model,
    int InputTokens,
    int OutputTokens,
    int LatencyMs,
    bool Success,
    string? Error,
    string? UserName = null);
