using System.Globalization;
using HyFib.HealthCare360.Modules.AI.Services.Assistant;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.AI.Services;

/// <summary>
/// Prompts and output schemas. System prompts are fixed text (volatile values such as today's date go in the user
/// message) and every answer is constrained by a JSON schema, so parsing never depends on free text. Schemas use only
/// the structured-output subset: typed properties, enums, all fields required, no additional properties.
/// </summary>
internal static class AiPrompts
{
    public const string CommandPurpose = "assistant.command";
    public const string StructureNotePurpose = "clinical.structure-note";
    public const string TranscribePurpose = "voice.transcribe";

    public static AiRequest Command(string redactedCommand, string? route, bool patientOpen, DateOnly today) => new(
        CommandPurpose,
        CommandSystem,
        [new AiMessage("user", string.Create(CultureInfo.InvariantCulture,
            $"Today: {today:yyyy-MM-dd} ({today.DayOfWeek}). Current screen: {route ?? "unknown"}. A patient is open: {(patientOpen ? "yes" : "no")}.\nCommand: {redactedCommand}"))],
        MaxTokens: 2048,
        JsonSchema: CommandSchema);

    public static AiRequest StructureNote(string redactedTranscript, string? specialty) => new(
        StructureNotePurpose,
        NoteSystem,
        [new AiMessage("user", $"Specialty: {(string.IsNullOrWhiteSpace(specialty) ? "general medicine" : specialty.Trim())}\n\nDictation:\n{redactedTranscript}")],
        MaxTokens: 16_000,
        JsonSchema: NoteSchema);

    internal static readonly string CommandSystem = $"""
        You map one short command, spoken or typed by hospital staff in the HC360 hospital information system, to exactly one intent.

        Intents:
        - navigate: open a screen; set "route" to one of the routes listed below.
        - search_patient: find patients by name, phone number or other details; put the words to search in "query" and any phone number in "phone".
        - open_patient: open one patient's record by UHID (hospital number); put it in "uhid".
        - list_appointments: show appointments for a day; put the day in "date" as YYYY-MM-DD.
        - book_follow_up: book a follow-up visit; put the number of days from today in "followUpDays".
        - create_note: start a clinical note; put any dictated note text in "noteText".
        - start_dictation: start voice dictation.
        - unknown: anything else, including requests unrelated to using HC360.

        Routes:
        {string.Join("\n", NavigationTargets.All.Select(t => $"- {t.Route} ({t.Label})"))}

        Identifiers were replaced with placeholders such as [UHID_1] or [PHONE_1] for privacy; copy them exactly into the matching field. Leave fields that do not apply empty ("" or 0).
        Latency-sensitive; begin your visible answer immediately.
        """;

    internal const string CommandSchema = """
        {
          "type": "object",
          "properties": {
            "intent": { "type": "string", "enum": ["navigate", "search_patient", "open_patient", "list_appointments", "book_follow_up", "create_note", "start_dictation", "unknown"] },
            "route": { "type": "string" },
            "query": { "type": "string" },
            "uhid": { "type": "string" },
            "phone": { "type": "string" },
            "date": { "type": "string" },
            "followUpDays": { "type": "integer" },
            "noteText": { "type": "string" }
          },
          "required": ["intent", "route", "query", "uhid", "phone", "date", "followUpDays", "noteText"],
          "additionalProperties": false
        }
        """;

    internal const string NoteSystem = """
        You turn a clinician's dictation into a draft SOAP note for the EMR of an Indian hospital. A doctor reviews and edits the draft before anything is saved.

        - Use only what the dictation states. Never add findings, vitals, doses, test results or diagnoses that were not dictated. If something is unclear, leave it out and add a warning instead.
        - subjective: complaints and history as reported. objective: examination findings, vitals and results that were dictated. assessment: the clinician's impression or diagnoses. plan: investigations, medicines exactly as dictated (name, dose, route, frequency, duration), advice and follow-up.
        - Write each section as short clinical lines separated by newlines, using standard medical abbreviations. No headings and no preamble. Use "" for a section with nothing dictated.
        - suggestedIcd: up to 5 WHO ICD-10 codes supported by the assessment, most specific first, each with a confidence between 0 and 1. Use an empty list when no diagnosis was dictated.
        - warnings: short notes for the reviewing doctor, such as an uncertain drug name or dose, a likely transcription error, a contradiction or a missing unit.
        - Identifiers were replaced with placeholders such as [NAME_1] or [PHONE_1]; keep them exactly as written.
        """;

    internal const string NoteSchema = """
        {
          "type": "object",
          "properties": {
            "subjective": { "type": "string" },
            "objective": { "type": "string" },
            "assessment": { "type": "string" },
            "plan": { "type": "string" },
            "suggestedIcd": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "code": { "type": "string" },
                  "title": { "type": "string" },
                  "confidence": { "type": "number" }
                },
                "required": ["code", "title", "confidence"],
                "additionalProperties": false
              }
            },
            "warnings": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["subjective", "objective", "assessment", "plan", "suggestedIcd", "warnings"],
          "additionalProperties": false
        }
        """;
}
