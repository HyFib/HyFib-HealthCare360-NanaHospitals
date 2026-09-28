using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>Who may follow the bed board and ward topics (checked by <see cref="RealtimeTopicAuthorizer"/>).</summary>
internal static class InpatientRealtimeTopics
{
    private static readonly string[] WardStaff =
    [
        InpatientPermissions.AdmissionsRead, InpatientPermissions.BedsManage, InpatientPermissions.BedsHousekeeping,
        InpatientPermissions.NursingChart, InpatientPermissions.NursingMar,
    ];

    public static void Register(IServiceCollection services)
    {
        services.AddRealtimeTopic(new RealtimeTopicPolicy("beds", RealtimeTopicScope.Facility, WardStaff));
        services.AddRealtimeTopic(new RealtimeTopicPolicy("ward", RealtimeTopicScope.Record, WardStaff, WardAsync));
    }

    private static Task<bool> WardAsync(IServiceProvider services, RealtimeSubscriber subscriber, Guid wardId, CancellationToken ct) =>
        services.GetRequiredService<HospitalDbContext>().Set<Ward>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AnyAsync(w => w.Id == wardId && w.FacilityId == subscriber.FacilityId, ct);
}
