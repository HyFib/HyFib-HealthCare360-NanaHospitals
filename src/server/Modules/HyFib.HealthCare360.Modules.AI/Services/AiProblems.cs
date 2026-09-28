namespace HyFib.HealthCare360.Modules.AI.Services;

/// <summary>Problem Details for AI-specific outcomes (with a machine-readable <c>code</c> the UI switches on).</summary>
internal static class AiProblems
{
    /// <summary>503: AI is off or not configured — the UI hides AI actions and falls back to manual entry.</summary>
    public static IResult Disabled(string? reason) =>
        Problem(StatusCodes.Status503ServiceUnavailable, "AI features are not available", reason ?? "AI features are turned off.", "ai.disabled");

    public static IResult SpeechDisabled(string? reason) =>
        Problem(StatusCodes.Status503ServiceUnavailable, "Server speech-to-text is not available",
            reason ?? "Use the Desktop's offline dictation instead.", "ai.speech_disabled");

    public static IResult QuotaExceeded(string detail) =>
        Problem(StatusCodes.Status429TooManyRequests, "AI usage limit reached", detail, "ai.quota");

    /// <summary>502: the provider failed or answered with something unusable; nothing was saved.</summary>
    public static IResult ProviderFailed(string? detail) =>
        Problem(StatusCodes.Status502BadGateway, "The AI service did not return a usable answer",
            detail ?? "Please try again, or continue without AI.", "ai.provider_failed");

    private static IResult Problem(int status, string title, string detail, string code) =>
        TypedResults.Problem(detail: detail, statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}
