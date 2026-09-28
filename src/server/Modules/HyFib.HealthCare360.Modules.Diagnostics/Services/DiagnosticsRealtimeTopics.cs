using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>Who may follow the diagnostics worklist topic of a facility (checked by <see cref="RealtimeTopicAuthorizer"/>).</summary>
internal static class DiagnosticsRealtimeTopics
{
    public static void Register(IServiceCollection services) =>
        services.AddRealtimeTopic(new RealtimeTopicPolicy(
            "lab",
            RealtimeTopicScope.Facility,
            [
                DiagnosticsPermissions.LabOrdersRead, DiagnosticsPermissions.LabSamplesCollect, DiagnosticsPermissions.LabSamplesReceive,
                DiagnosticsPermissions.LabResultsEnter, DiagnosticsPermissions.LabResultsVerify, DiagnosticsPermissions.RadiologyRead,
                DiagnosticsPermissions.RadiologyWorklist, DiagnosticsPermissions.CardiologyRead,
            ]));
}
