using HyFib.HealthCare360.Integrations;
using HyFib.HealthCare360.Modules.AI;
using HyFib.HealthCare360.Modules.Analytics;
using HyFib.HealthCare360.Modules.Billing;
using HyFib.HealthCare360.Modules.Clinical;
using HyFib.HealthCare360.Modules.Diagnostics;
using HyFib.HealthCare360.Modules.Engagement;
using HyFib.HealthCare360.Modules.HR;
using HyFib.HealthCare360.Modules.Inpatient;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Pharmacy;
using HyFib.HealthCare360.Modules.Quality;
using HyFib.HealthCare360.Modules.Scheduling;
using HyFib.HealthCare360.Modules.SpecialtyCare;
using HyFib.HealthCare360.Modules.SupplyChain;
using HyFib.HealthCare360.Modules.SupportServices;
using HyFib.HealthCare360.Modules.VirtualCare;
using HyFib.HealthCare360.Platform.Modules;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// The single list of bounded-context modules composed into this server (see docs/architecture/ARCHITECTURE.md §4).
/// Order does not matter: dependencies are expressed through project references and platform contracts.
/// </summary>
public static class ServerModules
{
    public static IReadOnlyList<IModule> All { get; } =
    [
        new PatientsModule(),
        new SchedulingModule(),
        new ClinicalModule(),
        new DiagnosticsModule(),
        new SupplyChainModule(),
        new PharmacyModule(),
        new InpatientModule(),
        new VirtualCareModule(),
        new SpecialtyCareModule(),
        new SupportServicesModule(),
        new BillingModule(),
        new HRModule(),
        new EngagementModule(),
        new QualityModule(),
        new AIModule(),
        new AnalyticsModule(),
        new IntegrationsModule(),
    ];
}
