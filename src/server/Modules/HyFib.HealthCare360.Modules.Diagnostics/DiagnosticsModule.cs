using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.HealthCheckups;
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
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// View a study's images from PACS, in HC360 and in the PACS viewer. Granted to doctors, who review imaging results
    /// in the EMR, and to radiographers and radiologists, who check and report them; not to nurses, who act on the
    /// report (still readable with <see cref="RadiologyRead"/>) rather than on the images.
    /// </summary>
    public const string RadiologyImages = "radiology.images.view";

    /// <summary>Record the woman's PCPNDT Form F details and declaration before an ultrasound in pregnancy.</summary>
    public const string FormFRecord = "radiology.form_f.record";

    /// <summary>
    /// Complete a Form F with the result and the declaration of the doctor who performed the ultrasound. Radiologists hold it;
    /// grant it to gynaecologists who scan, under Roles.
    /// </summary>
    public const string FormFSign = "radiology.form_f.sign";

    /// <summary>The month's Form F register and its export for the Appropriate Authority.</summary>
    public const string FormFRegister = "radiology.form_f.register";

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
        new(RadiologyImages, "Radiology", "View radiology images from PACS, in HC360 and in the PACS viewer", [SystemRoles.Doctor, SystemRoles.Radiographer, SystemRoles.Radiologist]),
        new(FormFRecord, "Radiology", "Record the woman's PCPNDT Form F details and declaration before an ultrasound in pregnancy", [SystemRoles.Radiographer, SystemRoles.Radiologist]),
        new(FormFSign, "Radiology", "Complete PCPNDT Form F with the result and the performing doctor's declaration", [SystemRoles.Radiologist]),
        new(FormFRegister, "Radiology", "View and export the monthly PCPNDT Form F register for the Appropriate Authority", [SystemRoles.Radiologist, SystemRoles.HospitalAdmin]),
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

    public IEnumerable<NumberSeriesDefinition> NumberSeries => DiagnosticsNumberSeries.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        DiagnosticsRealtimeTopics.Register(services);
        services.AddScoped<DiagnosticsLookups>();
        services.AddScoped<ReportDocumentFrame>();
        services.AddScoped<INotificationDocumentSource, LabReportDocumentSource>();
        services.AddScoped<INotificationDocumentSource, RadiologyReportDocumentSource>();
        services.AddScoped<INotificationDocumentSource, CardiologyReportDocumentSource>();
        services.AddScoped<HealthCheckupResultsBuilder>();
        services.AddScoped<DiagnosticsRealtime>();
        services.AddScoped<ChargeService>();
        services.AddScoped<LabWorkflow>();
        services.AddScoped<AnalyserOrders>();
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
        services.AddScoped<StudyImageService>();
        services.AddOptions<ConcurrencyLimit>(RadiologyImageLimits.Policy)
            .Configure(RadiologyImageLimits.Defaults)
            .Bind(configuration.GetSection(RadiologyImageLimits.SectionName))
            .Validate(RadiologyImageLimits.IsValid, $"{RadiologyImageLimits.SectionName} needs a PermitLimit of 1 or more and a QueueLimit of 0 or more.")
            .ValidateOnStart();
        services.AddOptions<RateLimiterOptions>().Configure<IOptionsMonitor<ConcurrencyLimit>>((options, limits) =>
        {
            var limit = limits.Get(RadiologyImageLimits.Policy);
            options.AddPolicy(RadiologyImageLimits.Policy, http => RadiologyImageLimits.Partition(http, limit));
        });
        services.AddScoped<RadiologyReportService>();
        services.AddScoped<FormFService>();
        services.AddScoped<CardiologyService>();

        // Lab-equipment gateway (Integrations) hands analyser results and host queries to the LIS.
        services.AddScoped<IInstrumentResultSink, InstrumentResultSink>();

        // The DICOM modality worklist server (Integrations) reads the studies still to be done.
        services.AddScoped<IModalityWorklistSource, ModalityWorklistSource>();

        // Links new studies with their images in PACS every few minutes (jobs nodes only).
        services.AddSingleton<PacsLinkAttempts>();
        services.AddHostedService<PacsLinkJob>();
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
        FormFEndpoints.Map(endpoints);
        CardiologyEndpoints.Map(endpoints);
        PortalReportEndpoints.Map(endpoints);
        ReportVerificationEndpoints.Map(endpoints);
        HealthCheckupReportEndpoints.Map(endpoints);
    }
}
