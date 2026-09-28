namespace HyFib.HealthCare360.Modules.AI.Features.Assistant;

/// <summary>Where the user is: the current UI route and the patient on screen (if any).</summary>
public sealed record AssistantContext(string? Route, Guid? PatientId);

public sealed record AssistantCommandRequest(string Text, AssistantContext? Context);

/// <summary>
/// The interpreted command. <c>Intent</c> is one of navigate, search_patient, open_patient, list_appointments,
/// book_follow_up, create_note, start_dictation or unknown; <c>Source</c> is <c>rules</c> (offline parser) or <c>ai</c>.
/// Actions with side effects come back with <c>RequiresConfirmation</c> — the UI asks before doing anything.
/// </summary>
public sealed record AssistantCommandResponse(
    string Intent,
    IReadOnlyDictionary<string, string> Parameters,
    string ConfirmationText,
    bool RequiresConfirmation,
    string Source);

public sealed class AssistantCommandRequestValidator : AbstractValidator<AssistantCommandRequest>
{
    public AssistantCommandRequestValidator()
    {
        RuleFor(x => x.Text).NotEmpty().WithMessage("Say or type a command.").SafeText(500);
        RuleFor(x => x.Context!.Route).SafeText(200).When(x => x.Context is not null);
    }
}
