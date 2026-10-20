using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Services;

namespace HyFib.HealthCare360.Modules.Interop;

/// <summary>
/// HL7 FHIR R4 read facade (EHR-05): Patient, Encounter, Observation, Condition, AllergyIntolerance, MedicationRequest,
/// DiagnosticReport and DocumentReference for other providers, subject to the patient's record-sharing consent; and ABDM
/// health records (EHR-08): signed discharge summaries and prescriptions as NRCeS FHIR document bundles for staff to
/// download, behind the ABDM feature switch. It reads the Patients, Clinical, Diagnostics and Inpatient modules' data and
/// owns no tables.
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
        services.AddScoped<AbdmAuditTrail>();
        services.AddScoped<AbdmDocumentService>();
        FhirAuthorizationResultHandler.Register(services);
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        FhirEndpoints.Map(endpoints);
        AbdmEndpoints.Map(endpoints);
    }
}
