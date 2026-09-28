using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>Who may follow a facility's pharmacy queue topic (checked by <see cref="RealtimeTopicAuthorizer"/>).</summary>
internal static class PharmacyRealtimeTopics
{
    public static void Register(IServiceCollection services) =>
        services.AddRealtimeTopic(new RealtimeTopicPolicy(
            "pharmacy",
            RealtimeTopicScope.Facility,
            [PharmacyPermissions.QueueRead, PharmacyPermissions.Dispense, PharmacyPermissions.Sales, PharmacyPermissions.IpIssue, PharmacyPermissions.IpIndent]));
}
