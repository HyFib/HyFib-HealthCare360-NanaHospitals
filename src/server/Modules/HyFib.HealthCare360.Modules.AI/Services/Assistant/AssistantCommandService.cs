using HyFib.HealthCare360.Modules.AI.Features.Assistant;
using HyFib.HealthCare360.Modules.AI.Services.Privacy;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.AI.Services.Assistant;

/// <summary>
/// Voice/typed commands (AIV-01..03): the offline rules answer first; only a command they do not recognise is sent —
/// redacted — to the AI provider, when AI is enabled and the user is within quota. A spoken UHID is resolved to the
/// patient id so the UI can open the chart directly. The assistant only interprets: every action is executed by the
/// UI after the user confirms, through the normal permission-checked APIs.
/// </summary>
internal sealed class AssistantCommandService(IAiGateway ai, IAiQuota quota, IAiUsageRecorder usage, IPatientDirectory patients, TimeProvider time)
{
    public async Task<AssistantCommandResponse> ExecuteAsync(AssistantCommandRequest request, CancellationToken cancellationToken)
    {
        var today = time.Today();
        var patientId = request.Context?.PatientId;
        var parsed = AssistantIntentParser.Parse(request.Text, patientId, today);
        if (parsed.Intent != AssistantIntents.Unknown)
        {
            return await CompleteAsync(parsed, "rules", cancellationToken);
        }

        var availability = await ai.GetAvailabilityAsync(cancellationToken);
        if (!availability.Enabled)
        {
            return Respond(parsed, "rules");
        }

        if (await quota.CheckAsync(cancellationToken) is { } limited)
        {
            return Respond(parsed with { ConfirmationText = limited }, "rules");
        }

        var redaction = PhiRedactor.Redact(request.Text.Trim(), await patients.RedactionContextAsync(patientId, cancellationToken));
        var started = time.GetTimestamp();
        var completion = await ai.CompleteAsync(AiPrompts.Command(redaction.Text, request.Context?.Route, patientId is not null, today), cancellationToken);
        var interpreted = completion.Success ? AssistantAiInterpreter.TryParse(completion.Text!, redaction, today, patientId) : null;
        await usage.RecordAsync(new AiUsageRecord(
            AiPrompts.CommandPurpose,
            availability.Provider,
            completion.Model ?? availability.Model,
            completion.InputTokens,
            completion.OutputTokens,
            time.GetElapsedTime(started),
            interpreted is not null,
            completion.Success ? interpreted is null ? "The answer could not be interpreted." : null : completion.Error,
            patientId), cancellationToken);

        return interpreted is null ? Respond(parsed, "rules") : await CompleteAsync(interpreted, "ai", cancellationToken);
    }

    private async Task<AssistantCommandResponse> CompleteAsync(ParsedIntent intent, string source, CancellationToken cancellationToken)
    {
        if (intent.Intent == AssistantIntents.OpenPatient && intent.Parameters.TryGetValue("uhid", out var uhid) && !intent.Parameters.ContainsKey("patientId"))
        {
            var found = await patients.FindByUhidAsync(uhid, cancellationToken);
            var parameters = new Dictionary<string, string>(intent.Parameters, StringComparer.Ordinal) { ["patientFound"] = found is null ? "false" : "true" };
            if (found is { } id)
            {
                parameters["patientId"] = id.ToString();
            }

            intent = intent with
            {
                Parameters = parameters,
                ConfirmationText = found is null ? $"No patient was found with UHID {uhid}." : intent.ConfirmationText,
            };
        }

        return Respond(intent, source);
    }

    private static AssistantCommandResponse Respond(ParsedIntent intent, string source) =>
        new(intent.Intent, intent.Parameters, intent.ConfirmationText, intent.RequiresConfirmation, source);
}
