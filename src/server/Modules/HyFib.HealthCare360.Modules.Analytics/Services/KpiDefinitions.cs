namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>Who sees a KPI: operational figures need MIS or clinical reading, money needs finance reading.</summary>
public enum KpiAudience
{
    Operations = 0,
    Finance,
}

/// <summary>
/// What one executive KPI means (BI-01..05): how it is calculated, where its data comes from, which catalogue report it
/// drills into and its target. The dashboard's tooltips and the KPI catalogue both read these, so the two never differ.
/// </summary>
/// <param name="Unit">count, percent, days, minutes, score or inr, as the dashboard formats it.</param>
/// <param name="Formula">How the value is calculated, in the words the dashboard shows.</param>
/// <param name="Source">Where the figures are recorded in HC360.</param>
/// <param name="ReportId">The catalogue report computed with the same query, for the detail behind the figure.</param>
/// <param name="Target">The hospital's target, when one is set; none is set for these KPIs yet.</param>
public sealed record KpiDefinition(
    string Key,
    string Label,
    string Unit,
    bool HigherIsBetter,
    string Formula,
    string Source,
    string ReportId,
    KpiAudience Audience,
    string? Target = null);

/// <summary>The executive KPI set, in the order the dashboard shows it.</summary>
internal static class KpiDefinitions
{
    public static readonly KpiDefinition OpVisits = new(
        "opVisits", "OP visits", "count", true,
        "Out-patient, emergency, day-care, telehealth and health-checkup check-ins, excluding cancelled visits.",
        "Visits checked in at the front office", "op-visit-trend", KpiAudience.Operations);

    public static readonly KpiDefinition Admissions = new(
        "admissions", "Admissions", "count", true,
        "In-patient admissions in the period (cancelled admissions excluded).",
        "Admissions recorded at the admission desk", "ip-admissions-discharges", KpiAudience.Operations);

    public static readonly KpiDefinition Occupancy = new(
        "occupancy", "Bed occupancy", "percent", true,
        "Inpatient days (midnight census) ÷ (beds in service × days) × 100.",
        "Admissions and discharges, and the beds in service", "bed-occupancy", KpiAudience.Operations);

    public static readonly KpiDefinition Alos = new(
        "alos", "Average length of stay", "days", false,
        "Total length of stay of patients discharged in the period ÷ discharges (IST calendar days, at least one).",
        "Admissions and discharges", "alos", KpiAudience.Operations);

    public static readonly KpiDefinition LabTat = new(
        "labTat", "Lab turnaround (median)", "minutes", false,
        "Median minutes from lab order to report release for tests ordered in the period.",
        "Laboratory orders and report release", "lab-volume-tat", KpiAudience.Operations);

    public static readonly KpiDefinition Nps = new(
        "nps", "Net Promoter Score", "score", true,
        "% promoters (9–10) − % detractors (0–6) among feedback responses in the period.",
        "Patient feedback responses", "feedback-nps", KpiAudience.Operations);

    public static readonly KpiDefinition Revenue = new(
        "revenue", "Revenue", "inr", true,
        "Net billed (bills finalized in the period, after discounts, incl. GST) plus pharmacy sales less returns.",
        "Finalised bills and pharmacy sales", "daily-revenue", KpiAudience.Finance);

    public static readonly KpiDefinition Collections = new(
        "collections", "Collections", "inr", true,
        "Money received at billing and pharmacy counters (all modes except advance adjustments and credit) less refunds paid.",
        "Billing and pharmacy receipts and refunds", "collections-by-mode", KpiAudience.Finance);

    public static readonly IReadOnlyList<KpiDefinition> All = [OpVisits, Admissions, Occupancy, Alos, LabTat, Nps, Revenue, Collections];
}
