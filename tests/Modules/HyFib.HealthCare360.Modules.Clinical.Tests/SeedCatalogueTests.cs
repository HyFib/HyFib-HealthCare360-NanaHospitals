using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Clinical.Seed;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>Integrity of the reference data seeded at start-up (codes, safety tables, templates and packages).</summary>
public sealed class SeedCatalogueTests
{
    /// <summary>Service codes the seed may reference: Diagnostics' catalogue plus the platform consultation service.</summary>
    private static readonly HashSet<string> KnownServiceCodes =
    [
        "LAB-CBC", "LAB-FBS", "LAB-PPBS", "LAB-HBA1C", "LAB-LIPID", "LAB-LFT", "LAB-KFT", "LAB-TFT", "LAB-URINE-RE", "LAB-ESR", "LAB-CRP", "LAB-VITD",
        "LAB-VITB12", "LAB-PSA", "LAB-ELECTROLYTES", "RAD-XR-CHEST", "RAD-USG-ABD", "RAD-USG-PELVIS", "RAD-MAMMO", "CARD-ECG", "CARD-ECHO", "CARD-TMT",
        "CONS-NEW",
    ];

    [Fact]
    public void Icd_catalogue_has_at_least_four_hundred_unique_codes()
    {
        var codes = IcdCodeCatalogue.All.Select(e => e.Code).ToList();

        codes.Count.ShouldBeGreaterThanOrEqualTo(400);
        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(codes.Count);
        IcdCodeCatalogue.All.ShouldAllBe(e => e.Title.Length > 0 && e.Title.Length <= 300);
    }

    [Fact]
    public void Every_seeded_icd_code_is_classified()
    {
        foreach (var entry in IcdCodeCatalogue.All)
        {
            IcdClassification.ChapterOf(entry.Code).ShouldNotBeNull(entry.Code);
            IcdClassification.BlockOf(entry.Code).ShouldNotBeNull(entry.Code);
        }
    }

    [Fact]
    public void Icd_catalogue_spans_every_chapter()
    {
        var chapters = IcdCodeCatalogue.All.Select(e => IcdClassification.ChapterOf(e.Code)!.Number).ToHashSet();

        chapters.Count.ShouldBe(IcdClassification.Chapters.Count);
    }

    [Fact]
    public void Non_billable_categories_have_seeded_subdivisions()
    {
        foreach (var category in IcdCodeCatalogue.All.Where(e => !e.Billable))
        {
            IcdCodeCatalogue.All.ShouldContain(e => e.Code.StartsWith(category.Code + ".", StringComparison.Ordinal), category.Code);
        }
    }

    [Fact]
    public void Common_indian_codes_are_present()
    {
        var codes = IcdCodeCatalogue.All.Select(e => e.Code).ToHashSet();

        string[] expected = ["E11.9", "I10", "I21.4", "I50.9", "J18.9", "J45.9", "A09.0", "A90", "B54", "A01.0", "N39.0", "O80.0", "T60.0", "T63.0", "Z00.0"];
        expected.ShouldAllBe(code => codes.Contains(code));
    }

    [Fact]
    public void Procedure_catalogue_has_at_least_eighty_valid_pcs_codes()
    {
        var entries = ProcedureCodeCatalogue.All;

        entries.Count.ShouldBeGreaterThanOrEqualTo(80);
        entries.Select(e => e.Code).Distinct(StringComparer.Ordinal).Count().ShouldBe(entries.Count);
        entries.ShouldAllBe(e => Regex.IsMatch(e.Code, "^[0-9A-HJ-NP-Z]{7}$") && e.Title.Length > 0 && e.Category.Length > 0);
    }

    [Fact]
    public void Interaction_table_has_forty_or_more_distinct_pairs()
    {
        var pairs = DrugSafetyCatalogue.Interactions.Select(i => ClinicalSeeder.CanonicalPair(i.DrugA, i.DrugB)).ToList();

        pairs.Count.ShouldBeGreaterThanOrEqualTo(40);
        pairs.Distinct().Count().ShouldBe(pairs.Count);
        pairs.ShouldAllBe(p => p.A.Length > 0 && p.B.Length > 0 && p.A != p.B);
        DrugSafetyCatalogue.Interactions.Count(i => i.Severity == InteractionSeverity.Major).ShouldBeGreaterThanOrEqualTo(20);
        DrugSafetyCatalogue.Interactions.ShouldAllBe(i => i.Effect.Length > 0 && i.Management.Length > 0);
    }

    [Fact]
    public void Dose_limits_are_positive_unique_and_canonical()
    {
        var limits = DrugSafetyCatalogue.DoseLimits;

        limits.ShouldAllBe(l => l.MaxDailyDoseMg > 0);
        limits.Select(l => l.GenericName).Distinct().Count().ShouldBe(limits.Count);
        foreach (var limit in limits)
        {
            DrugNames.Components(limit.GenericName).ShouldHaveSingleItem().ShouldBe(limit.GenericName);
        }
    }

    [Fact]
    public void Seeded_safety_tables_are_understood_by_the_checker()
    {
        var rules = DrugSafetyCatalogue.Interactions.Select(i => new SafetyInteractionRule(i.DrugA, i.DrugB, i.Severity, i.Effect, i.Management)).ToList();
        var limits = DrugSafetyCatalogue.DoseLimits.Select(l => new SafetyDoseLimit(l.GenericName, l.MaxDailyDoseMg, l.Route)).ToList();
        SafetyDrug Drug(string generic, string strength, DoseFrequency frequency) =>
            new(Guid.CreateVersion7(), generic, generic, strength, "Oral", "1 tab", frequency, null, null, false);

        var result = PrescriptionSafetyChecker.Check(new SafetyCheckInput(
            [Drug("Warfarin", "5 mg", DoseFrequency.OD), Drug("Metronidazole", "400 mg", DoseFrequency.TDS), Drug("Paracetamol", "1000 mg", DoseFrequency.QID)],
            [Drug("Paracetamol", "500 mg", DoseFrequency.TDS) with { IsCurrentMedication = true }], [], rules, limits));

        result.Alerts.ShouldContain(a => a.Kind == SafetyAlertKind.Interaction && a.IsBlocking);
        result.Alerts.ShouldContain(a => a.Kind == SafetyAlertKind.MaxDose);
    }

    [Fact]
    public void Templates_cover_the_required_specialities()
    {
        var templates = TemplateCatalogue.Templates;

        templates.Select(t => t.Specialty).Distinct().OrderBy(s => s).ShouldBe(["CARD", "ENT", "GM", "OBG", "ORTH", "PED"]);
        templates.Select(t => t.Code).Distinct().Count().ShouldBe(templates.Count);
        templates.ShouldAllBe(t => Regex.IsMatch(t.Code, "^[A-Z0-9][A-Z0-9-]*$") && t.Complaints.Length > 0);
        templates.SelectMany(t => t.OrderCodes).ShouldAllBe(code => KnownServiceCodes.Contains(code));
        templates.SelectMany(t => t.Medications).ShouldAllBe(m => m.DurationDays > 0 && DoseCalculator.ParseDose(m.Dose) != null);
    }

    [Fact]
    public void Order_sets_reference_known_services()
    {
        TemplateCatalogue.OrderSets.Select(s => s.Code).Distinct().Count().ShouldBe(TemplateCatalogue.OrderSets.Count);
        TemplateCatalogue.OrderSets.SelectMany(s => s.ServiceCodes).ShouldAllBe(code => KnownServiceCodes.Contains(code));
    }

    [Fact]
    public void Health_packages_match_the_brief()
    {
        var packages = HealthPackageCatalogue.Packages;

        packages.Select(p => p.Code).ShouldBe(["HC-BASIC", "HC-EXEC", "HC-WOMEN", "HC-SENIOR", "HC-DIABETES", "HC-CARDIAC"]);
        packages.ShouldAllBe(p => Regex.IsMatch(p.Code, "^HC-[A-Z0-9-]+$") && p.Price > 0 && p.Items.Length > 0);
        packages.SelectMany(p => p.Items).ShouldAllBe(i => KnownServiceCodes.Contains(i.ServiceCode));
        packages.Single(p => p.Code == "HC-WOMEN").Gender.ShouldBe(PackageGender.Female);
        packages.Single(p => p.Code == "HC-SENIOR").MinAgeYears.ShouldBe(60);
        packages.Single(p => p.Code == "HC-WOMEN").Items.Single(i => i.ServiceCode == "RAD-MAMMO").MinAgeYears.ShouldBe(40);
        packages.SelectMany(p => p.Items).Where(i => i.IsConsultation).ShouldAllBe(i => i.ServiceCode == "CONS-NEW");
    }
}
