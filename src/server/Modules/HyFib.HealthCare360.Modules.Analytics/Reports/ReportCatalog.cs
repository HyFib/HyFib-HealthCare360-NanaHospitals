using HyFib.HealthCare360.Modules.Analytics.Reports.Definitions;

namespace HyFib.HealthCare360.Modules.Analytics.Reports;

/// <summary>Who is asking for a report: identity, current facility and effective permissions.</summary>
public sealed record ReportActor(Guid UserId, string? UserName, string DisplayName, Guid? FacilityId, IReadOnlySet<string> Permissions)
{
    public bool IsSuperUser => Permissions.Contains(PermissionRegistry.Wildcard);

    public bool Has(string permission) => IsSuperUser || Permissions.Contains(permission);

    public bool HasAny(IEnumerable<string> permissions) => IsSuperUser || permissions.Any(Permissions.Contains);
}

/// <summary>
/// The report catalogue (MIS-01..05): every report the module offers, listed explicitly so the catalogue is easy to
/// review. A report is visible when the user holds any of its permissions; reports that list patients additionally
/// require <see cref="AnalyticsPermissions.ClinicalRead"/>.
/// </summary>
internal sealed class ReportCatalog
{
    private readonly Dictionary<string, ReportDefinition> _byId;

    // The only constructor: with a second one taking the reports, the server's container chose it and, having no
    // reports registered, made an empty catalogue.
    public ReportCatalog()
    {
        All = CreateAll().ToList();
        _byId = All.ToDictionary(r => r.Descriptor.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ReportDefinition> All { get; }

    public ReportDefinition? Find(string? id) => id is not null && _byId.TryGetValue(id, out var report) ? report : null;

    public IEnumerable<ReportDefinition> VisibleTo(ReportActor actor) => All.Where(r => CanRead(r.Descriptor, actor));

    public static bool CanRead(ReportDescriptor report, ReportActor actor) =>
        actor.HasAny(report.Permissions) && (!report.ContainsPatientIdentifiers || actor.Has(AnalyticsPermissions.ClinicalRead));

    public static string CategoryLabel(ReportCategory category) => category switch
    {
        ReportCategory.OutPatient => "Out-patient",
        ReportCategory.InPatient => "In-patient and beds",
        ReportCategory.Revenue => "Revenue and collections",
        ReportCategory.Diagnostics => "Laboratory and radiology",
        ReportCategory.Pharmacy => "Pharmacy and stores",
        ReportCategory.SpecialtyCare => "OT, dialysis and blood bank",
        ReportCategory.Quality => "Quality and safety",
        ReportCategory.Experience => "Patient experience",
        ReportCategory.People => "People",
        _ => category.ToString(),
    };

    private static IEnumerable<ReportDefinition> CreateAll() =>
    [
        // Out-patient
        new OpCensusReport(),
        new OpVisitTrendReport(),
        new OpHourlyFootfallReport(),
        new AppointmentNoShowReport(),

        // In-patient and beds
        new AdmissionsDischargesReport(),
        new BedOccupancyReport(),
        new BedTurnoverReport(),
        new AlosReport(),
        new WardCensusReport(),
        new DischargeOutcomesReport(),
        new AdverseDischargesReport(),

        // Revenue and collections
        new RevenueByDepartmentReport(),
        new RevenueByDoctorReport(),
        new RevenueByServiceReport(),
        new RevenueByPayerReport(),
        new DailyRevenueReport(),
        new CollectionsByModeReport(),
        new OutstandingAgeingReport(),
        new DiscountsReport(),
        new RefundsReport(),
        new DoctorFeeSummaryReport(),

        // Laboratory and radiology
        new LabVolumeTatReport(),
        new RadiologyVolumeTatReport(),
        new CriticalValuesReport(),

        // Pharmacy and stores
        new PharmacySalesMarginReport(),
        new NearExpiryStockReport(),
        new DeadStockReport(),
        new PurchaseVsConsumptionReport(),

        // OT, dialysis and blood bank
        new OtUtilisationReport(),
        new DialysisSessionsReport(),
        new BloodBankStockReport(),
        new BloodUsageReport(),

        // Quality and safety
        new IncidentRatesReport(),
        new CapaClosureReport(),
        new NabhIndicatorsReport(),
        new TopDiagnosesReport(),

        // Patient experience and people
        new FeedbackNpsReport(),
        new HrHeadcountReport(),
        new HrAttendanceReport(),
    ];
}
