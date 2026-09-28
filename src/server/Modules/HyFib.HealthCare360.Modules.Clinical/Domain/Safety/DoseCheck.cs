using System.Globalization;

namespace HyFib.HealthCare360.Modules.Clinical.Domain.Safety;

/// <summary>
/// Maximum daily dose check (ERX-02). Daily amounts are summed per ingredient across the new prescription and the
/// patient's current medications, because the real danger is the total (for example paracetamol from a plain tablet
/// plus a combination brand). When a limit exists but the dose text cannot be converted to mg, the prescriber is told
/// the dose was not verified instead of the check being silently skipped. Daily methotrexate is always flagged.
/// </summary>
public static class DoseCheck
{
    private const string Methotrexate = "methotrexate";

    public static IEnumerable<SafetyAlert> Run(IReadOnlyList<SafetyDrug> items, IReadOnlyList<SafetyDrug> current, IReadOnlyList<SafetyDoseLimit> limits)
    {
        foreach (var item in items.Where(i => i.Components.Any(c => DrugNames.ContainsTerm(c, Methotrexate)) && IsDailySchedule(i)))
        {
            yield return new SafetyAlert($"high-alert:methotrexate:{item.Id}", SafetyAlertKind.HighAlertMedication, SafetyAlertSeverity.Major, IsBlocking: true,
                $"{item.DrugName}: methotrexate for arthritis/psoriasis is taken ONCE A WEEK. A daily schedule has caused fatal overdoses — check the frequency.",
                "Use the Weekly frequency and write the day of the week in the instructions.", [item.Id]);
        }

        var normalizedLimits = limits
            .Select(l => (Limit: l, Name: DrugNames.Components(l.GenericName).FirstOrDefault() ?? string.Empty, Route: DrugNames.Normalize(l.Route)))
            .Where(l => l.Name.Length > 0 && l.Limit.MaxDailyDoseMg > 0)
            .ToList();
        if (normalizedLimits.Count == 0)
        {
            yield break;
        }

        var totals = new Dictionary<string, (SafetyDoseLimit Limit, decimal Mg, List<(SafetyDrug Drug, decimal Mg)> Parts)>(StringComparer.Ordinal);
        foreach (var drug in items.Concat(current))
        {
            foreach (var (component, dailyMg) in DailyMgByComponent(drug))
            {
                var route = DrugNames.Normalize(drug.Route);
                var match = normalizedLimits.FirstOrDefault(l =>
                    DrugNames.ContainsTerm(component, l.Name) && (l.Route.Length == 0 || route.Length == 0 || DrugNames.ContainsTerm(route, l.Route)));
                if (match.Limit is null)
                {
                    continue;
                }

                if (dailyMg is null)
                {
                    if (!drug.IsCurrentMedication)
                    {
                        yield return new SafetyAlert($"dose-unverified:{match.Name}:{drug.Id}", SafetyAlertKind.DoseUnverified, SafetyAlertSeverity.Minor, IsBlocking: false,
                            $"The daily dose of {component} in {drug.DrugName} could not be checked automatically. Confirm it is within {Format(match.Limit.MaxDailyDoseMg)} mg/day.",
                            "Write the dose as tablets or mg (for example \"1 tab\" or \"500 mg\") with the strength and frequency to enable the check.", [drug.Id]);
                    }

                    continue;
                }

                if (!totals.TryGetValue(match.Name, out var entry))
                {
                    entry = (match.Limit, 0m, new List<(SafetyDrug Drug, decimal Mg)>());
                }

                entry.Parts.Add((drug, dailyMg.Value));
                totals[match.Name] = (entry.Limit, entry.Mg + dailyMg.Value, entry.Parts);
            }
        }

        foreach (var (name, total) in totals)
        {
            if (total.Mg <= total.Limit.MaxDailyDoseMg || total.Parts.All(p => p.Drug.IsCurrentMedication))
            {
                continue;
            }

            var newIds = total.Parts.Where(p => !p.Drug.IsCurrentMedication).Select(p => p.Drug.Id).Distinct().ToList();
            var breakdown = string.Join("; ", total.Parts.Select(p => $"{p.Drug.Label}: {Format(p.Mg)} mg/day"));
            yield return new SafetyAlert($"max-dose:{name}:{string.Join(',', newIds)}", SafetyAlertKind.MaxDose, SafetyAlertSeverity.Major, IsBlocking: true,
                $"MAXIMUM DOSE: total {name} would be {Format(total.Mg)} mg/day, above the maximum of {Format(total.Limit.MaxDailyDoseMg)} mg/day.",
                breakdown, newIds);
        }
    }

    /// <summary>Daily mg per ingredient; a null value means the ingredient's dose cannot be computed from the text.</summary>
    public static IReadOnlyList<(string Component, decimal? DailyMg)> DailyMgByComponent(SafetyDrug drug)
    {
        var components = drug.Components;
        if (components.Count == 0)
        {
            return [];
        }

        // The limit is per day, so a weekly or one-off dose counts once on the day it is taken.
        var perDay = drug.Frequency is DoseFrequency.Weekly or DoseFrequency.STAT
            ? 1m
            : DoseCalculator.DosesPerDay(drug.Frequency, drug.CustomFrequency, drug.DosesPerDay);
        var perAdministration = PerAdministrationMg(drug, components.Count);

        return components
            .Select((c, index) => (Component: c, DailyMg: perDay is > 0 && perAdministration is { } doses ? doses[index] * perDay : (decimal?)null))
            .ToList();
    }

    private static IReadOnlyList<decimal>? PerAdministrationMg(SafetyDrug drug, int componentCount)
    {
        if (DoseCalculator.ParseDose(drug.Dose) is not { } dose)
        {
            return null;
        }

        if (dose.Unit == AmountUnit.Mg)
        {
            // A mass dose of a combination product ("500 mg" of what?) is ambiguous.
            return componentCount == 1 ? [dose.Value] : null;
        }

        var perUnit = dose.Unit switch
        {
            AmountUnit.Count => DoseCalculator.ParseStrengthMg(drug.Strength),
            AmountUnit.Ml => DoseCalculator.ParseConcentrationMgPerMl(drug.Strength),
            _ => [],
        };
        return perUnit.Count == componentCount ? perUnit.Select(mg => mg * dose.Value).ToList() : null;
    }

    private static bool IsDailySchedule(SafetyDrug drug) => drug.Frequency switch
    {
        DoseFrequency.Weekly or DoseFrequency.STAT => false,
        DoseFrequency.Custom => DoseCalculator.DosesPerDay(drug.Frequency, drug.CustomFrequency, drug.DosesPerDay) is not { } perDay || perDay >= 1m,
        _ => true,
    };

    private static string Format(decimal value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
