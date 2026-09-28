using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>Who may follow a facility's operation theatre topic (checked by <see cref="RealtimeTopicAuthorizer"/>).</summary>
internal static class SpecialtyCareRealtimeTopics
{
    public static void Register(IServiceCollection services) =>
        services.AddRealtimeTopic(new RealtimeTopicPolicy("ot", RealtimeTopicScope.Facility, [SpecialtyCarePermissions.OtRead]));
}
