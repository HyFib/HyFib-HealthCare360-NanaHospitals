using HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Outsourcing;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Portal;
using HyFib.HealthCare360.Modules.Diagnostics.Features.QualityControl;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Verification;
using HyFib.HealthCare360.Modules.Diagnostics.Services;

namespace HyFib.HealthCare360.Modules.Diagnostics;

public static class DiagnosticsPermissions
{
    public const string LabOrdersRead = "lab.orders.read";
    public const string LabOrdersCreate = "lab.orders.create";
    public const string LabSamplesCollect = "lab.samples.collect";
    public const string LabSamplesReceive = "lab.samples.receive";
    public const string LabResultsEnter = "lab.results.enter";
    public const string LabResultsVerify = "lab.results.verify";
    public const string LabQcManage = "lab.qc.manage";
    public const string LabMastersManage = "lab.masters.manage";
    public const string RadiologyRead = "radiology.studies.read";
    public const string RadiologyWorklist = "radiology.worklist";
    public const string RadiologyReportsWrite = "radiology.reports.write";
    public const string RadiologyReportsVerify = "radiology.reports.verify";
    public const string CardiologyRead = "cardiology.studies.read";
    public const string CardiologyStudiesManage = "cardiology.studies.manage";
    public const string CardiologyReportsVerify = "cardiology.reports.verify";

    /// <summary>Receives in-app alerts for verified critical lab values and critical imaging/cardiology findings.</summary>
    public const string CriticalAlerts = "diagnostics.critical.alerts";

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(LabOrdersRead, "Laboratory", "View lab orders, samples, results and reports", [SystemRoles.LabTechnician, SystemRoles.Pathologist, SystemRoles.Doctor, SystemRoles.Nurse]),
        new(LabOrdersCreate, "Laboratory", "Register walk-in and outside-referral lab orders; cancel tests", [SystemRoles.LabTechnician, SystemRoles.FrontOffice]),
        new(LabSamplesCollect, "Laboratory", "Collect samples and print barcode labels", [SystemRoles.LabTechnician, SystemRoles.Nurse]),
        new(LabSamplesReceive, "Laboratory", "Receive samples in the lab, reject them and send tests to reference labs", [SystemRoles.LabTechnician, SystemRoles.Pathologist]),
        new(LabResultsEnter, "Laboratory", "Enter results and record external reports", [SystemRoles.LabTechnician, SystemRoles.Pathologist]),
        new(LabResultsVerify, "Laboratory", "Verify, release and amend lab reports; document critical-value calls; override QC lockouts", [SystemRoles.Pathologist]),
        new(LabQcManage, "Laboratory", "Manage QC lots, record and review QC runs", [SystemRoles.LabTechnician, SystemRoles.Pathologist]),
        new(LabMastersManage, "Laboratory", "Manage the test master, reference ranges, analysers and reference labs", [SystemRoles.Pathologist, SystemRoles.HospitalAdmin]),
        new(RadiologyRead, "Radiology", "View imaging studies and released radiology reports", [SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.Radiographer, SystemRoles.Radiologist]),
        new(RadiologyWorklist, "Radiology", "Schedule and perform imaging studies; modality worklist and PACS reconciliation", [SystemRoles.Radiographer, SystemRoles.Radiologist]),
        new(RadiologyReportsWrite, "Radiology", "Write radiology reports", [SystemRoles.Radiologist]),
        new(RadiologyReportsVerify, "Radiology", "Sign off, release and add addenda to radiology reports; manage templates", [SystemRoles.Radiologist]),
        new(CardiologyRead, "Cardiology", "View cardiology studies and released reports", [SystemRoles.Doctor, SystemRoles.Nurse, SystemRoles.CardiologyTechnician, SystemRoles.Cardiologist]),
        new(CardiologyStudiesManage, "Cardiology", "Perform ECG/Echo/TMT/Holter/ABPM studies and upload device output", [SystemRoles.CardiologyTechnician]),

        // Hospitals where physicians report ECGs can grant this to Doctor under Roles.
        new(CardiologyReportsVerify, "Cardiology", "Report and sign off cardiology studies", [SystemRoles.Cardiologist]),
        new(CriticalAlerts, "Diagnostics", "Receive critical lab value and critical finding alerts", [SystemRoles.Doctor, SystemRoles.Nurse]),
    ];
}

/// <summary>
/// Diagnostics (layer 5): laboratory information system with quality control (LIS-01..08), radiology / medical
/// imaging with PACS (IMG-01..05) and cardiology (CAR-01..04). Work is created from clinicians' orders
/// (ServiceOrderPlaced) or at the lab counter, charged through IChargePoster, and released reports raise
/// DiagnosticReportReleased for the EHR and the patient portal.
/// </summary>
public sealed class DiagnosticsModule : IModule
{
    public string Name => "diagnostics";

    public IEnumerable<PermissionDefinition> Permissions => DiagnosticsPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<DiagnosticsLookups>();
        services.AddScoped<DiagnosticsRealtime>();
        services.AddScoped<ChargeService>();
        services.AddScoped<LabWorkflow>();
        services.AddScoped<ResultEvaluator>();
        services.AddScoped<CriticalValueNotifier>();
        services.AddScoped<ReportDelivery>();
        services.AddScoped<LabOrderService>();
        services.AddScoped<SampleService>();
        services.AddScoped<QualityControlService>();
        services.AddScoped<LabReportService>();
        services.AddScoped<LabReportBuilder>();
        services.AddScoped<LabResultService>();
        services.AddScoped<ImagingService>();
        services.AddScoped<RadiologyReportService>();
        services.AddScoped<CardiologyService>();

        // Lab-equipment gateway (Integrations) hands analyser results and host queries to the LIS.
        services.AddScoped<IInstrumentResultSink, InstrumentResultSink>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        LabMasterEndpoints.Map(endpoints);
        LabOrderEndpoints.Map(endpoints);
        SampleEndpoints.Map(endpoints);
        LabResultEndpoints.Map(endpoints);
        LabReportEndpoints.Map(endpoints);
        QcEndpoints.Map(endpoints);
        OutsourcingEndpoints.Map(endpoints);
        RadiologyEndpoints.Map(endpoints);
        RadiologyReportEndpoints.Map(endpoints);
        RadiologyMasterEndpoints.Map(endpoints);
        CardiologyEndpoints.Map(endpoints);
        PortalReportEndpoints.Map(endpoints);
        ReportVerificationEndpoints.Map(endpoints);
    }
}
