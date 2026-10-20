using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Services.Privacy;

/// <summary>
/// An <see cref="AiRequest"/> with the identifiers removed from the system prompt and from every message: the only form
/// in which an AI text provider is handed a request (<see cref="IAiTextProvider.CompleteAsync"/>). Its constructor is
/// private and <see cref="Redact"/> always runs <see cref="PhiRedactor"/>, so no provider can be given text that was not
/// redacted (SEC-12). The JSON schema is the module's own fixed text and is sent as written.
/// </summary>
internal sealed class RedactedAiRequest
{
    private RedactedAiRequest(AiRequest request, RedactionResult redaction)
    {
        Request = request;
        Redaction = redaction;
    }

    /// <summary>The request as it may be sent.</summary>
    public AiRequest Request { get; }

    /// <summary>What was replaced, to put command parameters back and to count; kept in memory only, never sent.</summary>
    public RedactionResult Redaction { get; }

    /// <summary>
    /// Redacts every text of <paramref name="request"/> with one set of placeholders, so <c>[NAME_1]</c> is the same
    /// person in the system prompt and in each message.
    /// </summary>
    public static RedactedAiRequest Redact(AiRequest request, RedactionContext context)
    {
        var redaction = PhiRedactor.Redact([request.SystemPrompt, .. request.Messages.Select(m => m.Content)], context);
        var messages = request.Messages.Select((message, index) => message with { Content = redaction.Texts[index + 1] }).ToList();
        return new RedactedAiRequest(request with { SystemPrompt = redaction.Texts[0], Messages = messages }, redaction);
    }
}
