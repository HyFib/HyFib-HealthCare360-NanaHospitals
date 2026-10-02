using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Pharmacy;

/// <summary>The numbers the Pharmacy module issues (administrators set their formats under Hospital setup).</summary>
public static class PharmacyNumberSeries
{
    public static NumberSeriesDefinition Invoice { get; } =
        new("pharmacy.invoice", "Pharmacy invoice", "Pharmacy", "{FAC}PH{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition SaleReturn { get; } =
        new("pharmacy.return", "Sale return", "Pharmacy", "{FAC}PHR{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition RefillRequest { get; } =
        new("pharmacy.refill", "Refill request", "Pharmacy", "{FAC}RF{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition WardIndent { get; } =
        new("pharmacy.ward_indent", "Ward indent", "Pharmacy", "{FAC}WI{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition WardIssue { get; } =
        new("pharmacy.ip_issue", "Ward issue", "Pharmacy", "{FAC}IPI{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    public static NumberSeriesDefinition WardReturn { get; } =
        new("pharmacy.ward_return", "Ward return", "Pharmacy", "{FAC}WR{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    internal static IEnumerable<NumberSeriesDefinition> All { get; } = [Invoice, SaleReturn, RefillRequest, WardIndent, WardIssue, WardReturn];
}
