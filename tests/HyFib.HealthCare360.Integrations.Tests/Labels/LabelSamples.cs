using HyFib.HealthCare360.Integrations.Labels;

namespace HyFib.HealthCare360.Integrations.Tests.Labels;

/// <summary>Typical labels from each department, and the media they print on, shared by the label tests.</summary>
internal static class LabelSamples
{
    public static readonly DateTimeOffset Collected = new(2026, 9, 26, 5, 2, 0, TimeSpan.Zero); // 10:32 IST

    public static LabTubeLabel Tube(bool stat = false) => new(
        "LS2600000123", "Sharma Ravi Kumar", "NH00012345", "46Y/M", "LA2600123", stat, "EDTA", "HAEM",
        ["CBC", "ESR", "Peripheral smear"], Collected);

    public static WristbandLabel Band(WristbandAllergies allergies = WristbandAllergies.Recorded) => new(
        "NH00012345", "Sharma Ravi Kumar", "46Y/M", "IP2600123", "Ward 3", "Bed 12", allergies);

    public static DispensingLabel Medicine() => new(
        "Sharma Ravi Kumar", "Paracetamol 500 mg", "10 tab", "1 tab · twice a day · 5 days · after food", "AB1234",
        new DateOnly(2027, 9, 30), new DateOnly(2026, 10, 2), "Nana Hospitals");

    public static BloodUnitLabel Unit(bool cleared = true) => new(
        "NH-BU-2600123", "AB−", "PRBC", 350, cleared, new DateTimeOffset(2026, 11, 5, 8, 30, 0, TimeSpan.Zero), "Store at 2–6 °C");

    public static CssdPackLabel Pack(bool released = true) => new(
        "CS2600000123", "General surgery set", "Steam", released ? new DateOnly(2026, 10, 30) : null);

    public static AssetTagLabel Asset() => new("AST-2600123", "Multipara monitor, ICU bed 4", "Nana Hospitals");

    public static PharmacyTagLabel PharmacyTag(bool batch = true) => batch
        ? new("PCM500#AB1234", "Paracetamol 500 mg tab", "AB1234", new DateOnly(2027, 9, 30), 2.5m, "tab")
        : new("PCM500", "Paracetamol 500 mg tab", null, null, null);

    public static LabelMedia Label(LabelLanguage language = LabelLanguage.Zpl, int dpi = 203, decimal width = 50, decimal height = 25) =>
        new(language, dpi, width, height);

    public static LabelMedia Wristband(LabelLanguage language = LabelLanguage.Zpl, int dpi = 203, decimal width = 25, decimal length = 254) =>
        new(language, dpi, width, length);

    /// <summary>Both languages at both resolutions.</summary>
    public static TheoryData<LabelLanguage, int> Printers() => new()
    {
        { LabelLanguage.Zpl, 203 },
        { LabelLanguage.Zpl, 300 },
        { LabelLanguage.Tspl, 203 },
        { LabelLanguage.Tspl, 300 },
    };
}
