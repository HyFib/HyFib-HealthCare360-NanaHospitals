using HyFib.HealthCare360.Modules.AI.Features.Clinical;
using HyFib.HealthCare360.Modules.AI.Services.Privacy;

namespace HyFib.HealthCare360.Modules.AI.Services.Clinical;

/// <summary>Either a draft or the staff-facing reason there is none.</summary>
internal sealed record StructureOutcome(ClinicalNoteDraft? Draft, string? Failure);

/// <summary>
/// Clinical dictation → SOAP draft with ICD-10 suggestions (AIV-04..06). The transcript is redacted before it leaves
/// the hospital, the result is always a draft that says so, and nothing is stored: the clinician reviews, edits and
/// saves it through the EMR, which is where signing and auditing of the note itself happen.
/// </summary>
internal sealed class ClinicalNoteStructurer(IAiGateway ai, IAiUsageRecorder usage, IPatientDirectory patients, TimeProvider time)
{
    public const string ReviewWarning = "AI-generated draft: review and correct every section before saving. Nothing has been saved or signed.";

    public async Task<StructureOutcome> StructureAsync(StructureNoteRequest request, CancellationToken cancellationToken)
    {
        var availability = await ai.GetAvailabilityAsync(cancellationToken);
        if (!availability.Enabled)
        {
            return new StructureOutcome(null, availability.Reason ?? "AI features are turned off.");
        }

        var redaction = PhiRedactor.Redact(request.Transcript.Trim(), await patients.RedactionContextAsync(request.PatientId, cancellationToken));
        var started = time.GetTimestamp();
        var completion = await ai.CompleteAsync(AiPrompts.StructureNote(redaction.Text, request.Specialty), cancellationToken);
        var content = completion.Success ? ClinicalNoteParser.TryParse(completion.Text!) : null;
        await usage.RecordAsync(new AiUsageRecord(
            AiPrompts.StructureNotePurpose,
            availability.Provider,
            completion.Model ?? availability.Model,
            completion.InputTokens,
            completion.OutputTokens,
            time.GetElapsedTime(started),
            content is not null,
            completion.Success ? content is null ? "The draft could not be read." : null : completion.Error,
            request.PatientId), cancellationToken);

        if (content is null)
        {
            return new StructureOutcome(null, completion.Success ? "The AI service returned a draft HC360 could not read." : completion.Error);
        }

        var warnings = new List<string> { ReviewWarning };
        if (redaction.Total > 0)
        {
            warnings.Add($"{redaction.Total} identifier(s) were replaced with placeholders such as [NAME_1] before AI processing.");
        }

        warnings.AddRange(content.Warnings);
        var draft = new ClinicalNoteDraft("draft", content.Subjective, content.Objective, content.Assessment, content.Plan, content.SuggestedIcd,
            warnings, RequiresClinicianReview: true, completion.Model, time.GetUtcNow(), redaction.Total);
        return new StructureOutcome(draft, null);
    }
}
