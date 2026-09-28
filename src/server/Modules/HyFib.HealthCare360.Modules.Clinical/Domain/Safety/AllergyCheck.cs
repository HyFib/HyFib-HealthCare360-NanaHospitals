namespace HyFib.HealthCare360.Modules.Clinical.Domain.Safety;

/// <summary>
/// Allergy screening before signing (ERX-02). A medicine is blocked when it contains the allergen itself or belongs to
/// the same class (a "penicillin" allergy covers amoxicillin; an aspirin allergy covers other NSAIDs). Known
/// cross-sensitivities (penicillin → cephalosporin) are warnings that need acknowledgement. Only active drug/other
/// allergies are considered; food and environmental allergies do not apply to medicine names.
/// </summary>
public static class AllergyCheck
{
    public static IEnumerable<SafetyAlert> Run(IReadOnlyList<SafetyDrug> items, IReadOnlyList<SafetyAllergy> allergies)
    {
        var relevant = allergies.Where(a => a.Type is AllergyType.Drug or AllergyType.Other).ToList();
        if (relevant.Count == 0)
        {
            yield break;
        }

        foreach (var item in items)
        {
            var itemName = DrugNames.Normalize(item.DrugName);
            var itemClasses = item.Components.SelectMany(DrugClasses.ClassesOf).DistinctBy(c => c.Key).ToList();

            foreach (var allergy in relevant)
            {
                var allergen = DrugNames.Normalize(allergy.Allergen);
                if (allergen.Length == 0)
                {
                    continue;
                }

                var history = Describe(allergy);
                var direct = DrugNames.Components(allergy.Allergen)
                    .FirstOrDefault(a => item.Components.Any(c => DrugNames.Overlaps(c, a)) || DrugNames.ContainsTerm(itemName, a));
                if (direct is not null)
                {
                    yield return new SafetyAlert($"allergy:{allergen}:{item.Id}", SafetyAlertKind.Allergy, SafetyAlertSeverity.Major, IsBlocking: true,
                        $"ALLERGY: {item.DrugName} contains {direct}; the patient is allergic to {allergy.Allergen}{history}.", null, [item.Id]);
                    continue;
                }

                var allergenClasses = DrugClasses.ClassesForAllergen(allergen);
                var shared = itemClasses.FirstOrDefault(c => allergenClasses.Any(a => a.Key == c.Key));
                if (shared is not null)
                {
                    yield return new SafetyAlert($"class-allergy:{allergen}:{item.Id}", SafetyAlertKind.ClassAllergy, SafetyAlertSeverity.Major, IsBlocking: true,
                        $"ALLERGY: {item.DrugName} belongs to {shared.Name}; the patient is allergic to {allergy.Allergen}{history}.", null, [item.Id]);
                    continue;
                }

                var cross = DrugClasses.CrossSensitivities.FirstOrDefault(x =>
                    allergenClasses.Any(a => a.Key == x.FromClass) && itemClasses.Any(c => c.Key == x.ToClass));
                if (cross is not null)
                {
                    var toClass = DrugClasses.All.First(c => c.Key == cross.ToClass).Name;
                    yield return new SafetyAlert($"cross-sensitivity:{allergen}:{item.Id}", SafetyAlertKind.CrossSensitivity, cross.Severity, IsBlocking: false,
                        $"Possible cross-sensitivity: {item.DrugName} ({toClass}) for a patient allergic to {allergy.Allergen}{history}.", cross.Note, [item.Id]);
                }
            }
        }
    }

    private static string Describe(SafetyAllergy allergy)
    {
        var severity = allergy.Severity == AllergySeverity.Anaphylaxis ? "ANAPHYLAXIS" : allergy.Severity.ToString().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(allergy.Reaction) ? $" ({severity})" : $" ({allergy.Reaction.Trim()}; {severity})";
    }
}
