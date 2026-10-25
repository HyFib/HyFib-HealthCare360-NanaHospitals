using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupportServices;

/// <summary>The numbers the Support services module issues (administrators set their formats under Hospital setup).</summary>
public static class SupportServicesNumberSeries
{
    public static NumberSeriesDefinition CssdRequest { get; } =
        new("cssd-request", "CSSD request", "CSSD", "{FAC}CQ{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>Washer-disinfector cycles, which clean the trays before they are packed and sterilised.</summary>
    public static NumberSeriesDefinition WashCycle { get; } =
        new("cssd-wash", "Wash cycle", "CSSD", "{FAC}CW{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>Sterilisation cycles; each pack in the load is labelled with the cycle number and its own 3 digits.</summary>
    public static NumberSeriesDefinition SterilisationCycle { get; } =
        new("cssd-cycle", "Sterilisation cycle", "CSSD", "{FAC}CS{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition CanteenBill { get; } =
        new("canteen-bill", "Canteen bill", "Canteen", "{FAC}CN{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>The bar code on each bag of bio-medical waste, printed where it is weighed (BMW Rules 2016, as amended in 2018).</summary>
    public static NumberSeriesDefinition WasteBag { get; } =
        new("bmw-bag", "Bio-medical waste bag", "Bio-medical waste", "{FAC}BW{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24);

    /// <summary>A handover of bags to the common bio-medical waste treatment facility's vehicle.</summary>
    public static NumberSeriesDefinition WasteHandover { get; } =
        new("bmw-handover", "Bio-medical waste handover", "Bio-medical waste", "{FAC}BH{YY}{SEQ:5}", NumberResetPolicy.Yearly, MaxLength: 24);

    internal static IEnumerable<NumberSeriesDefinition> All { get; } = [CssdRequest, WashCycle, SterilisationCycle, CanteenBill, WasteBag, WasteHandover];
}
