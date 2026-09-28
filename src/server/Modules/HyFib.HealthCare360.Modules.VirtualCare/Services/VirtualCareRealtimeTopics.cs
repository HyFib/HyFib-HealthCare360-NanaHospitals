using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>Who may follow a telehealth session's topic (checked by <see cref="RealtimeTopicAuthorizer"/>).</summary>
internal static class VirtualCareRealtimeTopics
{
    public static void Register(IServiceCollection services) =>
        services.AddRealtimeTopic(new RealtimeTopicPolicy(
            "tele",
            RealtimeTopicScope.Record,
            [VirtualCarePermissions.TelehealthRead, VirtualCarePermissions.TelehealthManage, VirtualCarePermissions.TelehealthConsult],
            SessionAsync,
            AllowPortal: true));

    /// <summary>Staff follow sessions of their own facility; a patient only their own sessions.</summary>
    private static Task<bool> SessionAsync(IServiceProvider services, RealtimeSubscriber subscriber, Guid sessionId, CancellationToken ct)
    {
        var sessions = services.GetRequiredService<HospitalDbContext>().Set<TelehealthSession>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);
        return subscriber.IsPortalUser
            ? sessions.AnyAsync(s => s.Id == sessionId && s.PatientId == subscriber.PatientId, ct)
            : sessions.AnyAsync(s => s.Id == sessionId && s.FacilityId == subscriber.FacilityId, ct);
    }
}
