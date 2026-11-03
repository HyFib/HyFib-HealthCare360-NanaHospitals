namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>How a vaccine is given, as the immunisation form offers it.</summary>
public enum VaccineRoute
{
    /// <summary>Into the skin (BCG, fractional IPV).</summary>
    Intradermal = 0,

    /// <summary>Into a muscle.</summary>
    Intramuscular,

    /// <summary>Under the skin.</summary>
    Subcutaneous,

    /// <summary>By mouth (drops).</summary>
    Oral,
}

/// <summary>
/// One vaccine as an Indian immunisation card names it: the card's short code, the name staff read, the SNOMED CT vaccine
/// product other providers' systems read (always one of the ABDM implementation guide's vaccine codes,
/// <c>https://nrces.in/ndhm/fhir/r4/ValueSet/ndhm-vaccine-codes</c>, with its display), and how it is usually given, when
/// that does not depend on the product.
/// </summary>
public sealed record VaccineCode(string Code, string Name, string Snomed, string SnomedDisplay, VaccineRoute? Route);

/// <summary>
/// The vaccines of the Universal Immunisation Programme's National Immunisation Schedule, and the adult, travel and
/// private-market vaccines given in Indian hospitals, by the code staff choose on the immunisation form. A dose recorded
/// with another code, or none, is sent to other providers by its name only: HC360 never guesses a code.
/// </summary>
public static class VaccineCodes
{
    public static IReadOnlyList<VaccineCode> All { get; } =
    [
        // The National Immunisation Schedule (infants, children and pregnant women).
        new("BCG", "BCG", "1861000221106", "BCG (Bacillus Calmette-Guerin) vaccine", VaccineRoute.Intradermal),
        new("HEPB", "Hepatitis B", "871822003", "Hepatitis B vaccine", VaccineRoute.Intramuscular),
        new("OPV", "Oral polio vaccine (bOPV)", "1051000221104", "Live attenuated poliovirus serotypes 1 and 3 vaccine in oral dose form", VaccineRoute.Oral),
        new("PENTA", "Pentavalent (DPT, hepatitis B, Hib)", "871886002",
            "Diphtheria and Haemophilus influenzae type B and hepatitis B and pertussis and tetanus vaccine", VaccineRoute.Intramuscular),
        new("ROTA", "Rotavirus", "1081000221109", "Live attenuated rotavirus vaccine", VaccineRoute.Oral),
        new("FIPV", "Fractional inactivated polio vaccine (fIPV)", "871740006", "Inactivated polio vaccine", VaccineRoute.Intradermal),
        new("IPV", "Inactivated polio vaccine (IPV)", "871740006", "Inactivated polio vaccine", VaccineRoute.Intramuscular),
        new("PCV", "Pneumococcal conjugate vaccine (PCV)", "981000221107", "Pneumococcal vaccine", VaccineRoute.Intramuscular),
        new("MR", "Measles and rubella (MR)", "2231000221105", "Live attenuated measles and rubella vaccine", VaccineRoute.Subcutaneous),
        new("JE", "Japanese encephalitis", "871724008", "Japanese encephalitis virus vaccine", null),
        new("DPT", "DPT booster", "871875004", "Diphtheria and pertussis and tetanus vaccine", VaccineRoute.Intramuscular),
        new("TD", "Td (tetanus and adult diphtheria)", "871827009", "Low dose diphtheria and tetanus vaccine", VaccineRoute.Intramuscular),
        new("TT", "Tetanus toxoid", "2021000221101", "Tetanus toxoid vaccine", VaccineRoute.Intramuscular),

        // Other childhood, adult and travel vaccines.
        new("HEXA", "Hexavalent (DTaP, IPV, hepatitis B, Hib)", "871895005",
            "Diphtheria and Haemophilus influenzae type B and hepatitis B and pertussis and poliomyelitis and tetanus", VaccineRoute.Intramuscular),
        new("DTAP", "DTaP", "871876003", "Diphtheria and acellular pertussis and tetanus vaccine", VaccineRoute.Intramuscular),
        new("TDAP", "Tdap", "462321000124107", "Tdap - Low dose diphtheria and low dose acellular pertussis and tetanus vaccine", VaccineRoute.Intramuscular),
        new("HIB", "Haemophilus influenzae type b (Hib)", "871764007", "Haemophilus influenzae type B vaccine", VaccineRoute.Intramuscular),
        new("MMR", "Measles, mumps and rubella (MMR)", "2241000221103", "Live attenuated measles, mumps, and rubella vaccine", VaccineRoute.Subcutaneous),
        new("VARICELLA", "Chickenpox (varicella)", "2221000221107", "Live attenuated Varicella-zoster vaccine", VaccineRoute.Subcutaneous),
        new("HEPA", "Hepatitis A", "871751006", "Hepatitis A vaccine", VaccineRoute.Intramuscular),
        new("TYPHOID", "Typhoid (conjugate or polysaccharide)", "961000221100", "Typhoid vaccine", VaccineRoute.Intramuscular),
        new("HPV", "Human papillomavirus (HPV)", "911000221103", "Human papillomavirus vaccine", VaccineRoute.Intramuscular),
        new("FLU", "Influenza", "1181000221105", "Influenza vaccine", VaccineRoute.Intramuscular),
        new("PPSV23", "Pneumococcal polysaccharide (PPSV23)", "1801000221105", "Pneumococcal polysaccharide vaccine", VaccineRoute.Intramuscular),
        new("MENACWY", "Meningococcal ACWY", "871873006", "Meningitis A, C, W135 and Y vaccine", VaccineRoute.Intramuscular),
        new("RABIES", "Rabies", "871726005", "Rabies vaccine", null),
        new("COVID19", "COVID-19", "1119305005", "COVID-19 antigen vaccine", VaccineRoute.Intramuscular),
        new("YF", "Yellow fever", "871717007", "Yellow fever vaccine", VaccineRoute.Subcutaneous),
        new("CHOLERA", "Oral cholera vaccine", "2181000221101", "Cholera vaccine in oral dose form", VaccineRoute.Oral),
    ];

    private static readonly Dictionary<string, VaccineCode> ByCode = All.ToDictionary(v => v.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>The vaccine of a card code as recorded (any case, spaces around it ignored); null for any other code.</summary>
    public static VaccineCode? Find(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : ByCode.GetValueOrDefault(code.Trim());
}
