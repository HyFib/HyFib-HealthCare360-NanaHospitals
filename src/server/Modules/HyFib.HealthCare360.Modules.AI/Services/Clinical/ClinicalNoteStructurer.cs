using HyFib.HealthCare360.Modules.AI.Features.Clinical;

namespace HyFib.HealthCare360.Modules.AI.Services.Clinical;

/// <summary>Either a draft or the staff-facing reason there is none.</summary>
internal sealed record StructureOutcome(ClinicalNoteDraft? Draft, string? Failure);

/// <summary>
/// Clinical dictation → SOAP draft with ICD-10 suggestions (AIV-04..06). The gateway redacts the dictation before it is
/// sent to the AI provider and records the call; the result is always a draft that says so, and nothing is stored: the
/// clinician reviews, edits and saves it through the EMR, which is where signing and auditing of the note itself happen.
/// Whatever the provider answers, the status and the review flag are set here, never taken from the answer.
/// </summary>
internal sealed class ClinicalNoteStructurer(IAiGateway ai, TimeProvider time)
{
    public const string ReviewWarning = "AI-generated draft: review and correct every section before saving. Nothing has been saved or signed.";

    public async Task<StructureOutcome> StructureAsync(StructureNoteRequest request, CancellationToken cancellationToken)
    {
        var outcome = await ai.CompleteAsync(new AiCall<ClinicalNoteContent>(
            AiPrompts.StructureNote(request.Transcript.Trim(), request.Specialty),
            request.PatientId,
            (answer, _) => ClinicalNoteParser.TryParse(answer),
            "The draft could not be read."), cancellationToken);

        if (outcome.Value is not { } content)
        {
            return new StructureOutcome(null, outcome.Completion.Success ? "The AI service returned a draft HC360 could not read." : outcome.Completion.Error);
        }

        var warnings = new List<string> { ReviewWarning };
        if (outcome.IdentifiersRedacted > 0)
        {
            warnings.Add($"{outcome.IdentifiersRedacted} identifier(s) were replaced with placeholders such as [NAME_1] before AI processing.");
        }

        warnings.AddRange(content.Warnings);
        var draft = new ClinicalNoteDraft("draft", content.Subjective, content.Objective, content.Assessment, content.Plan, content.SuggestedIcd,
            warnings, RequiresClinicianReview: true, outcome.Completion.Model, time.GetUtcNow(), outcome.IdentifiersRedacted);
        return new StructureOutcome(draft, null);
    }
}
