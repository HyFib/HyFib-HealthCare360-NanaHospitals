namespace HyFib.HealthCare360.Modules.Clinical.Domain.Idsp;

/// <summary>
/// A disease reported to the district surveillance unit under the Integrated Disease Surveillance Programme (IDSP), on
/// the IHIP presumptive (P) form when a doctor diagnoses it and the laboratory (L) form when a test confirms it.
/// <see cref="IcdPrefixes"/> are ICD-10 codes or categories as the catalogue writes them ("A01", "A92.0");
/// <see cref="LabTests"/> are lab-master test codes, <see cref="Loinc"/> codes of tests the hospital adds itself, and
/// <see cref="Organisms"/> the growth on a culture that confirms the disease. <see cref="Immediate"/> diseases are
/// outbreak-prone: the surveillance officer is alerted at once, not at the end of the week. A <see cref="Nikshay"/>
/// disease (tuberculosis) is notified on Ni-kshay, case by case, instead of the IHIP forms. Within
/// <see cref="EpisodeDays"/> of a case's last event, the same disease in the same patient is the same case.
/// </summary>
public sealed record IdspDisease(
    string Code,
    string Name,
    IReadOnlyList<string> IcdPrefixes,
    IReadOnlyList<string> LabTests,
    IReadOnlyList<string> Loinc,
    IReadOnlyList<string> Organisms,
    bool Immediate,
    bool Nikshay = false,
    int EpisodeDays = NotifiableCase.EpisodeDays);

/// <summary>
/// The IDSP diseases a hospital reports, after the IHIP P and L forms. The district unit's own list governs: check
/// this one against it before go-live. Not on the forms: tuberculosis, notified on Ni-kshay (<see cref="Tuberculosis"/>),
/// and HIV, which is not an IDSP disease.
/// </summary>
public static class IdspDiseases
{
    public const string TuberculosisCode = "TB";

    /// <summary>
    /// Tuberculosis is notifiable to Ni-kshay, the National TB Elimination Programme's case register: since 2012, and
    /// by the Health Ministry's notification of 16 March 2018 every clinical establishment notifies each TB patient it
    /// diagnoses or treats, failing which the provisions on spreading an infectious disease apply (IPC s.269 and 270,
    /// now BNS s.271 and 272). A case is caught by its diagnosis (A15–A19) or by M. tuberculosis grown, and stays to be
    /// notified until its Ni-kshay ID is recorded. A course of treatment runs six months or longer, so a diagnosis within
    /// a year of the last belongs to the same case.
    /// </summary>
    public static IdspDisease Tuberculosis { get; } = new(
        TuberculosisCode, "Tuberculosis", ["A15", "A16", "A17", "A18", "A19"], [], [], ["Mycobacterium tuberculosis"], Immediate: false, Nikshay: true,
        EpisodeDays: 365);

    public static IReadOnlyList<IdspDisease> All { get; } =
    [
        new("ADD", "Acute diarrhoeal disease (including gastroenteritis)", ["A04", "A08", "A09"], [], [], [], false),
        new("BACILLARY_DYSENTERY", "Bacillary dysentery", ["A03"], [], [], ["Shigella"], false),
        new("CHOLERA", "Cholera", ["A00"], [], [], ["Vibrio cholerae"], true),
        new("ENTERIC_FEVER", "Enteric fever (typhoid and paratyphoid)", ["A01"], ["WIDAL-TO", "WIDAL-TH", "WIDAL-AH", "WIDAL-BH"], [],
            ["Salmonella Typhi", "Salmonella Paratyphi", "Salmonella enterica serovar Typhi", "Salmonella enterica serovar Paratyphi"], false),
        new("VIRAL_HEPATITIS", "Acute viral hepatitis", ["B15", "B16", "B17", "B19"], ["LAB-HBSAG", "LAB-HCV"], ["5196-1", "13955-0"], [], false),
        new("MALARIA", "Malaria", ["B50", "B51", "B52", "B53", "B54"], ["LAB-MALARIA"], [], ["Plasmodium"], false),
        new("DENGUE", "Dengue", ["A90", "A91", "A97"], ["LAB-DENGUE-NS1"], ["75377-2"], [], false),
        new("CHIKUNGUNYA", "Chikungunya", ["A92.0"], [], [], [], false),
        new("AES", "Acute encephalitis syndrome (including Japanese encephalitis)", ["A83.0", "A86", "G04", "G05"], [], [], [], true),
        new("MENINGITIS", "Meningitis", ["A39", "G00", "G01", "G02", "G03"], [], [], ["Neisseria meningitidis"], true),
        new("MEASLES", "Measles", ["B05"], [], [], [], true),
        new("RUBELLA", "Rubella", ["B06"], [], [], [], false),
        new("DIPHTHERIA", "Diphtheria", ["A36"], [], [], ["Corynebacterium diphtheriae"], true),
        new("PERTUSSIS", "Pertussis", ["A37"], [], [], ["Bordetella pertussis"], false),
        new("CHICKENPOX", "Chickenpox", ["B01"], [], [], [], false),
        new("MUMPS", "Mumps", ["B26"], [], [], [], false),
        new("AFP", "Acute flaccid paralysis (under 15 years)", [], [], [], [], true),
        new("LEPTOSPIROSIS", "Leptospirosis", ["A27"], [], [], ["Leptospira"], false),
        new("SCRUB_TYPHUS", "Scrub typhus", ["A75.3"], [], [], ["Orientia tsutsugamushi"], false),
        new("INFLUENZA", "Influenza (including H1N1)", ["J09", "J10"], [], [], [], false),
        new("PNEUMONIA", "Pneumonia", ["J12", "J13", "J14", "J15", "J16", "J17", "J18"], [], [], [], false),
        new("KALA_AZAR", "Kala-azar (visceral leishmaniasis)", ["B55.0"], [], [], ["Leishmania"], false),
        new("RABIES", "Rabies", ["A82"], [], [], [], true),
        new("PLAGUE", "Plague", ["A20"], [], [], ["Yersinia pestis"], true),
        new("ANTHRAX", "Anthrax", ["A22"], [], [], ["Bacillus anthracis"], true),
        new("MPOX", "Mpox", ["B04"], [], [], [], true),
        new("SNAKE_BITE", "Snake bite", ["T63.0"], [], [], [], false),
    ];

    /// <summary>Every disease a case is opened for: the IDSP list and tuberculosis.</summary>
    public static IReadOnlyList<IdspDisease> Notifiable { get; } = [.. All, Tuberculosis];

    private static readonly Dictionary<string, IdspDisease> ByCode = Notifiable.ToDictionary(d => d.Code, StringComparer.Ordinal);

    public static IdspDisease? Find(string code) => ByCode.GetValueOrDefault(code);

    /// <summary>The disease an ICD-10 code (or any code under it) is notified as; null if none.</summary>
    public static IdspDisease? ForIcd(string? icdCode)
    {
        if (string.IsNullOrWhiteSpace(icdCode))
        {
            return null;
        }

        var code = icdCode.Trim().ToUpperInvariant();
        return Notifiable.FirstOrDefault(d => d.IcdPrefixes.Any(p => Under(code, p)));
    }

    // "A01" covers A01 and A01.0..A01.4; "A92.0" covers itself.
    private static bool Under(string code, string prefix) =>
        code == prefix || (!prefix.Contains('.', StringComparison.Ordinal) && code.StartsWith(prefix + ".", StringComparison.Ordinal));

    /// <summary>The disease a positive result of this test confirms: by the test's code or LOINC, or by the organism grown.</summary>
    public static IdspDisease? ForLabResult(string testCode, string? loinc, string? organism)
    {
        var byTest = Notifiable.FirstOrDefault(d => d.LabTests.Contains(testCode, StringComparer.OrdinalIgnoreCase)
                                                    || (loinc is not null && d.Loinc.Contains(loinc, StringComparer.Ordinal)));
        if (byTest is not null)
        {
            return byTest;
        }

        return string.IsNullOrWhiteSpace(organism)
            ? null
            : Notifiable.FirstOrDefault(d => d.Organisms.Any(o => organism.Contains(o, StringComparison.OrdinalIgnoreCase)));
    }
}
