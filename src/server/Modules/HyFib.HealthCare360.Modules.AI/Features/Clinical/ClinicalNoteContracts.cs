namespace HyFib.HealthCare360.Modules.AI.Features.Clinical;

/// <summary>A dictation transcript (from Desktop offline speech or the server transcription endpoint) to structure.</summary>
public sealed record StructureNoteRequest(string Transcript, string? Specialty, Guid? PatientId);

public sealed record SuggestedIcdCode(string Code, string Title, double Confidence);

/// <summary>
/// An AI-generated SOAP draft. <c>Status</c> is always <c>draft</c> and <c>RequiresClinicianReview</c> always true:
/// the draft is never saved by this endpoint. Identifiers appear as placeholders ([NAME_1]) because they were removed
/// before the text was sent to the AI provider.
/// </summary>
public sealed record ClinicalNoteDraft(
    string Status,
    string Subjective,
    string Objective,
    string Assessment,
    string Plan,
    IReadOnlyList<SuggestedIcdCode> SuggestedIcd,
    IReadOnlyList<string> Warnings,
    bool RequiresClinicianReview,
    string? Model,
    DateTimeOffset GeneratedAt,
    int IdentifiersRedacted);

public sealed class StructureNoteRequestValidator : AbstractValidator<StructureNoteRequest>
{
    public StructureNoteRequestValidator()
    {
        RuleFor(x => x.Transcript).NotEmpty().WithMessage("The dictation is empty.")
            .MinimumLength(10).WithMessage("The dictation is too short to structure.")
            .SafeText(20_000);
        RuleFor(x => x.Specialty).SafeText(100);
    }
}
