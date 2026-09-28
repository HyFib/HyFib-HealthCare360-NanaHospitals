namespace HyFib.HealthCare360.Modules.Analytics;

/// <summary>
/// Analytics permissions (MIS-01..05, BI-01..05). Reading is split by audience so each report states who may see it:
/// operational MIS, finance (revenue, collections, doctor fees) and clinical quality (which also unlocks the few reports
/// that list individual patients).
/// </summary>
public static class AnalyticsPermissions
{
    public const string MisRead = "analytics.mis.read";
    public const string FinanceRead = "analytics.finance.read";
    public const string ClinicalRead = "analytics.clinical.read";
    public const string DashboardsManage = "analytics.dashboards.manage";
    public const string SchedulesManage = "analytics.schedules.manage";

    /// <summary>Any analytics reader (the catalogue, KPIs and dashboards are filtered per report afterwards).</summary>
    public static readonly string[] AnyRead = [MisRead, FinanceRead, ClinicalRead];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(MisRead, "Analytics", "View MIS reports: census, occupancy, diagnostics, pharmacy stock, quality and HR", [SystemRoles.MisAnalyst, SystemRoles.HospitalAdmin, SystemRoles.Accountant]),
        new(FinanceRead, "Analytics", "View revenue, collections, dues, discounts, refunds and doctor-fee reports", [SystemRoles.Accountant, SystemRoles.HospitalAdmin]),
        new(ClinicalRead, "Analytics", "View clinical quality reports, including reports that list individual patients (audited)", [SystemRoles.HospitalAdmin, SystemRoles.QualityManager, SystemRoles.MrdOfficer]),
        new(DashboardsManage, "Analytics", "Build, share and edit BI dashboards", [SystemRoles.HospitalAdmin, SystemRoles.MisAnalyst]),
        new(SchedulesManage, "Analytics", "Schedule reports to be e-mailed as Excel or CSV files", [SystemRoles.HospitalAdmin, SystemRoles.MisAnalyst]),
    ];
}
