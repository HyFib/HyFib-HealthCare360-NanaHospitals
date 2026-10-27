using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Services;

namespace HyFib.HealthCare360.Modules.Interop;

/// <summary>
/// HL7 FHIR R4 read facade (EHR-05): Patient, Encounter, Observation, Condition, AllergyIntolerance, MedicationRequest,
/// DiagnosticReport and DocumentReference for other providers, subject to the patient's record-sharing consent. ABDM
/// health records (EHR-08, behind the <c>features.abdm</c> switch): discharge summaries, lab reports, consultation notes
/// and prescriptions as FHIR documents of the NRCeS implementation guide, and the ABDM gateway client: records linked to a
/// patient's ABHA (HIP-initiated and user-initiated linking) and sent, encrypted, to other providers under the
/// patient's consent. It reads the Patients, Clinical, Diagnostics and Inpatient modules' data; its own tables (schema
/// <c>interop</c>) hold the link requests, the link tokens the gateway issued, the patients' searches from their PHR
/// apps, the care contexts linked, the consents notified and the transfers made.
/// </summary>
public sealed class InteropModule : IModule
{
    public string Name => "interop";

    public IEnumerable<PermissionDefinition> Permissions => InteropPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ReferenceDirectoryLoader>();
        services.AddScoped<PatientSharingService>();
        services.AddScoped<FhirAuditTrail>();
        services.AddScoped<PatientRecords>();
        services.AddScoped<IFhirResourceProvider, EncounterProvider>();
        services.AddScoped<IFhirResourceProvider, ObservationProvider>();
        services.AddScoped<IFhirResourceProvider, ConditionProvider>();
        services.AddScoped<IFhirResourceProvider, AllergyIntoleranceProvider>();
        services.AddScoped<IFhirResourceProvider, MedicationRequestProvider>();
        services.AddScoped<IFhirResourceProvider, DiagnosticReportProvider>();
        services.AddScoped<IFhirResourceProvider, DocumentReferenceProvider>();
        services.AddScoped<FhirApi>();
        FhirAuthorizationResultHandler.Register(services);

        services.AddScoped<AbdmRecordLoader>();
        services.AddScoped<AbdmBundleService>();
        services.AddScoped<AbdmGatewayConfiguration>();
        services.AddScoped<DisabledAbdmGatewayClient>();
        services.AddSingleton<AbdmGatewaySessions>();
        services.AddSingleton<AbdmGatewayKeyCache>();
        services.AddHttpClient<HttpAbdmGatewayClient>().ConfigurePrimaryHttpMessageHandler(OutboundHttp.CreateHandler);
        services.AddHttpClient<AbdmGatewayKeys>().ConfigurePrimaryHttpMessageHandler(OutboundHttp.CreateHandler);
        services.AddScoped<IAbdmGatewayClient>(sp => sp.GetRequiredService<HttpAbdmGatewayClient>());
        services.AddScoped<AbdmLinkingService>();
        services.AddScoped<AbdmUserLinkingService>();
        services.AddHttpClient<AbdmDataPush>().ConfigurePrimaryHttpMessageHandler(OutboundHttp.CreateHandler);
        services.AddScoped<AbdmDataFlowService>();
        services.AddScoped<AbdmProfileShareService>();
        services.AddHttpClient<AbhaClient>().ConfigurePrimaryHttpMessageHandler(OutboundHttp.CreateHandler);
        services.AddScoped<AbhaVerificationService>();
        services.AddScoped<AbdmConsentRequestService>();
        services.AddScoped<AbdmHealthInformationService>();
        services.AddHostedService<AbdmReceivedRecordEraser>();
        services.AddSingleton<IFideliusCipher, FideliusCipher>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        FhirEndpoints.Map(endpoints);
        AbdmEndpoints.Map(endpoints);
        AbdmGatewayCallbackEndpoints.Map(endpoints);
    }
}
