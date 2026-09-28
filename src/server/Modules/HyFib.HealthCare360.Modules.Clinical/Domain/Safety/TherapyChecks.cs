namespace HyFib.HealthCare360.Modules.Clinical.Domain.Safety;

/// <summary>Pairs to compare: every new line with every other new line and with every current medication.</summary>
internal static class DrugPairs
{
    public static IEnumerable<(SafetyDrug First, SafetyDrug Second)> Of(IReadOnlyList<SafetyDrug> items, IReadOnlyList<SafetyDrug> current)
    {
        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                yield return (items[i], items[j]);
            }

            foreach (var medication in current)
            {
                yield return (items[i], medication);
            }
        }
    }
}

/// <summary>
/// Duplicate therapy (ERX-02): the same active ingredient twice — common with Indian combination brands, for example
/// paracetamol alone plus an ibuprofen–paracetamol combination — or two NSAIDs together. Warnings, not blocks.
/// </summary>
public static class DuplicateTherapyCheck
{
    public static IEnumerable<SafetyAlert> Run(IReadOnlyList<SafetyDrug> items, IReadOnlyList<SafetyDrug> current)
    {
        foreach (var (first, second) in DrugPairs.Of(items, current))
        {
            var shared = first.Components.FirstOrDefault(c => second.Components.Contains(c, StringComparer.Ordinal));
            if (shared is not null)
            {
                yield return new SafetyAlert($"duplicate:{shared}:{first.Id}:{second.Id}", SafetyAlertKind.DuplicateTherapy, SafetyAlertSeverity.Moderate, IsBlocking: false,
                    $"Duplicate therapy: {shared} is in both {first.Label} and {second.Label}.", null, Ids(first, second));
                continue;
            }

            var firstNsaid = first.Components.Any(c => DrugClasses.ClassesOf(c).Any(k => k.Key == DrugClasses.Nsaids));
            var secondNsaid = second.Components.Any(c => DrugClasses.ClassesOf(c).Any(k => k.Key == DrugClasses.Nsaids));
            if (firstNsaid && secondNsaid)
            {
                yield return new SafetyAlert($"duplicate-class:nsaids:{first.Id}:{second.Id}", SafetyAlertKind.DuplicateTherapy, SafetyAlertSeverity.Moderate, IsBlocking: false,
                    $"Two NSAIDs prescribed together: {first.Label} and {second.Label}.", "Combining NSAIDs adds gastrointestinal bleeding and kidney risk without extra benefit.",
                    Ids(first, second));
            }
        }
    }

    internal static IReadOnlyList<Guid> Ids(SafetyDrug first, SafetyDrug second) =>
        second.IsCurrentMedication ? [first.Id] : [first.Id, second.Id];
}

/// <summary>
/// Drug–drug interactions from the configurable interaction table (ERX-02). Major interactions block signing unless
/// an override reason is given; moderate and minor ones are warnings. Ingredients inside one combination product are
/// not checked against each other (the combination is intentional).
/// </summary>
public static class InteractionCheck
{
    public static IEnumerable<SafetyAlert> Run(IReadOnlyList<SafetyDrug> items, IReadOnlyList<SafetyDrug> current, IReadOnlyList<SafetyInteractionRule> rules)
    {
        var normalized = rules
            .Select(r => (Rule: r, A: Canonical(r.DrugA), B: Canonical(r.DrugB)))
            .Where(r => r.A.Length > 0 && r.B.Length > 0)
            .ToList();

        foreach (var (first, second) in DrugPairs.Of(items, current))
        {
            foreach (var (rule, a, b) in normalized)
            {
                if (!((Matches(first, a) && Matches(second, b)) || (Matches(first, b) && Matches(second, a))))
                {
                    continue;
                }

                var severity = rule.Severity switch
                {
                    InteractionSeverity.Major => SafetyAlertSeverity.Major,
                    InteractionSeverity.Moderate => SafetyAlertSeverity.Moderate,
                    _ => SafetyAlertSeverity.Minor,
                };
                var label = rule.Severity == InteractionSeverity.Major ? "MAJOR interaction" : $"{rule.Severity} interaction";
                yield return new SafetyAlert($"interaction:{a}|{b}:{first.Id}:{second.Id}", SafetyAlertKind.Interaction, severity,
                    IsBlocking: rule.Severity == InteractionSeverity.Major,
                    $"{label}: {first.Label} + {second.Label} — {rule.Effect}", rule.Management, DuplicateTherapyCheck.Ids(first, second));
            }
        }
    }

    private static string Canonical(string term) => DrugNames.Components(term).FirstOrDefault() ?? string.Empty;

    private static bool Matches(SafetyDrug drug, string term) => drug.Components.Any(c => DrugNames.ContainsTerm(c, term));
}
