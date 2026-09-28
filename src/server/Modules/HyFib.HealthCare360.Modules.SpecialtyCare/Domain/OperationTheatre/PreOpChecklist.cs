using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>One item of a checklist as defined by the hospital (key, wording, whether "not applicable" is allowed).</summary>
public sealed record ChecklistItemDefinition(string Key, string Text, bool AllowsNotApplicable);

/// <summary>A recorded answer to a checklist item, with the attesting staff member and time.</summary>
public sealed record ChecklistEntry(
    string Key,
    ChecklistItemState State,
    string? Remarks,
    Guid? RecordedByUserId,
    string? RecordedByName,
    DateTimeOffset? RecordedAt);

/// <summary>What a user submits for one item.</summary>
public sealed record ChecklistInput(string Key, ChecklistItemState State, string? Remarks);

/// <summary>
/// Pre-operative checklist (OT-02) completed on the ward / in pre-op holding. It must be complete before the
/// WHO Sign-in, because Sign-in re-confirms several of these items at the table.
/// </summary>
public static class PreOpChecklist
{
    public static IReadOnlyList<ChecklistItemDefinition> Items { get; } =
    [
        new("consent", "Informed consent for surgery and anaesthesia signed", false),
        new("pac", "Pre-anaesthesia check-up (PAC) done and patient declared fit", false),
        new("npo", "Nil-by-mouth (NPO) status confirmed and time of last intake recorded", false),
        new("identity_band", "Identity band on the patient matches the case sheet", false),
        new("allergies", "Allergies checked and documented", false),
        new("site_marked", "Operative site marked by the operating surgeon", true),
        new("investigations", "Investigations reviewed (CBC, coagulation, renal function, ECG, imaging as applicable)", false),
        new("blood_arranged", "Blood grouped, cross-matched and reserved", true),
        new("valuables_removed", "Jewellery, dentures, hearing aids and prostheses removed", true),
        new("premedication", "Pre-medication / prophylactic antibiotic given as ordered", true),
    ];

    public static List<ChecklistEntry> NewEntries() =>
        Items.Select(i => new ChecklistEntry(i.Key, ChecklistItemState.Pending, null, null, null, null)).ToList();

    public static bool IsComplete(IReadOnlyCollection<ChecklistEntry> entries) =>
        Items.All(i => entries.Any(e => e.Key == i.Key && e.State != ChecklistItemState.Pending));

    /// <summary>
    /// Applies submitted answers. "Not applicable" is refused for items that always apply (consent, PAC, NPO…) and
    /// needs a remark otherwise, so an auditor can see why an item was skipped.
    /// </summary>
    public static Result Apply(List<ChecklistEntry> entries, IReadOnlyList<ChecklistInput> inputs, StaffStamp by) =>
        ChecklistRules.Apply(Items, entries, inputs, by, allowReset: true);
}

/// <summary>Shared mechanics of the pre-op and WHO checklists.</summary>
internal static class ChecklistRules
{
    public static Result Apply(
        IReadOnlyList<ChecklistItemDefinition> definitions,
        List<ChecklistEntry> entries,
        IReadOnlyList<ChecklistInput> inputs,
        StaffStamp by,
        bool allowReset)
    {
        foreach (var input in inputs)
        {
            var definition = definitions.FirstOrDefault(d => d.Key == input.Key);
            if (definition is null)
            {
                return Error.Field("items", $"'{input.Key}' is not an item of this checklist.");
            }

            if (input.State == ChecklistItemState.NotApplicable)
            {
                if (!definition.AllowsNotApplicable)
                {
                    return Error.Rule("checklist.not_applicable_refused", $"\"{definition.Text}\" always applies and cannot be marked not applicable.");
                }

                if (string.IsNullOrWhiteSpace(input.Remarks))
                {
                    return Error.Field("items", $"Give a reason for marking \"{definition.Text}\" as not applicable.");
                }
            }

            if (input.State == ChecklistItemState.Pending && !allowReset)
            {
                return Error.Rule("checklist.cannot_reset", "Confirmed safety checklist items cannot be withdrawn.");
            }
        }

        foreach (var input in inputs)
        {
            var index = entries.FindIndex(e => e.Key == input.Key);
            var entry = input.State == ChecklistItemState.Pending
                ? new ChecklistEntry(input.Key, ChecklistItemState.Pending, null, null, null, null)
                : new ChecklistEntry(input.Key, input.State, Clean(input.Remarks), by.UserId, by.Name, by.At);
            if (index < 0)
            {
                entries.Add(entry);
            }
            else
            {
                entries[index] = entry;
            }
        }

        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
