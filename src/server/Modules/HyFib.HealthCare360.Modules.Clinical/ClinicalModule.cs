using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;
using HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Flags;
using HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups;
using HyFib.HealthCare360.Modules.Clinical.Features.Immunizations;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Clinical.Features.Notes;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Clinical.Features.Portal;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Features.Templates;
using HyFib.HealthCare360.Modules.Clinical.Features.Vitals;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Clinical;

/// <summary>
/// EHR / EMR physician desktop, e-prescribing, clinical orders, medical records with ICD coding and master health
/// checkup (SRS EHR-01..08, EMR-01..07, ERX-01..06, MRD-01..06, MHC-01..05).
/// </summary>
public sealed class ClinicalModule : IModule
{
    public string Name => "clinical";

    public IEnumerable<PermissionDefinition> Permissions => ClinicalPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ClinicalAccess>();
        services.AddScoped<EncounterService>();
        services.AddScoped<ClinicalOrderService>();
        services.AddScoped<PrescriptionSafetyService>();
        services.AddScoped<PrescriptionService>();
        services.AddScoped<PrescriptionPrintBuilder>();
        services.AddScoped<PatientRecordQueries>();
        services.AddScoped<TimelineQueries>();
        services.AddScoped<WorklistQueries>();
        services.AddScoped<DischargeSummaryService>();
        services.AddScoped<CodingService>();
        services.AddScoped<MedicalRecordService>();
        services.AddScoped<HealthCheckupService>();

        // The PDFs that messages to patients carry (the prescription with the "prescription shared" message).
        services.AddScoped<INotificationDocumentSource, PrescriptionDocumentSource>();
        services.AddScoped<INotificationDocumentSource, DischargeSummaryDocumentSource>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        WorklistEndpoints.Map(endpoints);
        PatientRecordEndpoints.Map(endpoints);
        EncounterEndpoints.Map(endpoints);
        VitalEndpoints.Map(endpoints);
        FlagEndpoints.Map(endpoints);
        DiagnosisEndpoints.Map(endpoints);
        NoteEndpoints.Map(endpoints);
        ImmunizationEndpoints.Map(endpoints);
        TemplateEndpoints.Map(endpoints);
        OrderEndpoints.Map(endpoints);
        PrescriptionEndpoints.Map(endpoints);
        DrugSearchEndpoints.Map(endpoints);
        DrugSafetyEndpoints.Map(endpoints);
        PrescriptionVerificationEndpoints.Map(endpoints);
        DischargeSummaryEndpoints.Map(endpoints);
        CodeCatalogueEndpoints.Map(endpoints);
        CodingEndpoints.Map(endpoints);
        ChartEndpoints.Map(endpoints);
        PackageEndpoints.Map(endpoints);
        BookingEndpoints.Map(endpoints);
        PortalEndpoints.Map(endpoints);
    }
}
