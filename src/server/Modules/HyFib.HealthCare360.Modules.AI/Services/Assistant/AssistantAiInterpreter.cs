using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.AI.Services.Privacy;

namespace HyFib.HealthCare360.Modules.AI.Services.Assistant;

/// <summary>
/// Turns the model's structured answer into a <see cref="ParsedIntent"/>. The model's output is treated as untrusted
/// input: the intent must be one of the known ones, routes must be real screens, dates and follow-up intervals must be
/// plausible, and confirmations are generated here, not taken from the model. Placeholders the model copied back
/// (<c>[UHID_1]</c>) are restored to the values the user actually said.
/// </summary>
internal static class AssistantAiInterpreter
{
    public static ParsedIntent? TryParse(string json, RedactionResult redaction, DateOnly today, Guid? contextPatientId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string Field(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? redaction.Restore(value.GetString() ?? string.Empty).Trim() : string.Empty;

            switch (Field("intent"))
            {
                case AssistantIntents.Navigate:
                    return NavigationTargets.ByRoute(Field("route")) is { } target ? AssistantIntentParser.ForNavigation(target) : null;

                case AssistantIntents.OpenPatient:
                    var uhid = new string(Field("uhid").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
                    return uhid.Length is >= 3 and <= 20 ? AssistantIntentParser.ForOpenPatient(uhid)
                        : contextPatientId is { } id ? AssistantIntentParser.ForOpenCurrentPatient(id)
                        : null;

                case AssistantIntents.SearchPatient:
                    var phone = SharedKernel.Phone.Normalize(Field("phone"));
                    var query = Field("query");
                    return phone is null && query.Length == 0
                        ? null
                        : AssistantIntentParser.ForSearch(query.Length == 0 ? null : query[..Math.Min(query.Length, 100)], phone, openIfSingleMatch: false);

                case AssistantIntents.ListAppointments:
                    var date = DateOnly.TryParseExact(Field("date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                               && Math.Abs(parsed.DayNumber - today.DayNumber) <= 366
                        ? parsed
                        : today;
                    return AssistantIntentParser.ForAppointments(date, today, null);

                case AssistantIntents.BookFollowUp:
                    int? days = root.TryGetProperty("followUpDays", out var interval) && interval.ValueKind == JsonValueKind.Number
                                && interval.TryGetInt32(out var value) && value is > 0 and <= 365
                        ? value
                        : null;
                    return AssistantIntentParser.ForFollowUp(days, days is { } d ? AssistantIntentParser.DescribeDays(d) : null, today, contextPatientId);

                case AssistantIntents.CreateNote:
                    var text = Field("noteText");
                    return AssistantIntentParser.ForNote(null, text.Length == 0 ? null : text, contextPatientId);

                case AssistantIntents.StartDictation:
                    return AssistantIntentParser.ForDictation(contextPatientId);

                case AssistantIntents.Unknown:
                    return AssistantIntentParser.ForUnknown();

                default:
                    return null;
            }
        }
    }
}
