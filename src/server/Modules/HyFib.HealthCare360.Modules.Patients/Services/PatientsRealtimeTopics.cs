using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>Who may follow the Patients module's realtime topics (checked by <see cref="RealtimeTopicAuthorizer"/>).</summary>
internal static class PatientsRealtimeTopics
{
    public static void Register(IServiceCollection services)
    {
        // A patient's record: staff who may read patients (restricted records need restricted access); a portal user
        // only their own record.
        services.AddRealtimeTopic(new RealtimeTopicPolicy(
            "patient", RealtimeTopicScope.Record, [PatientsPermissions.Read], PatientAsync, AllowPortal: true));

        // A department's queue: the desk, the doctors' consoles and the waiting-area display of the same facility.
        services.AddRealtimeTopic(new RealtimeTopicPolicy(
            "queue",
            RealtimeTopicScope.Record,
            [PatientsPermissions.QueueManage, PatientsPermissions.QueueDisplay, PatientsPermissions.VisitsRead],
            DepartmentAsync));
    }

    private static async Task<bool> PatientAsync(IServiceProvider services, RealtimeSubscriber subscriber, Guid patientId, CancellationToken ct)
    {
        if (subscriber.IsPortalUser)
        {
            return subscriber.PatientId == patientId;
        }

        var restricted = await services.GetRequiredService<HospitalDbContext>().Set<Patient>().AsNoTracking()
            .Where(p => p.Id == patientId)
            .Select(p => (bool?)p.IsVip)
            .FirstOrDefaultAsync(ct);
        return restricted switch
        {
            null => false,
            false => true,
            true => subscriber.UserId is { } userId
                    && await services.GetRequiredService<IPermissionService>().HasPermissionAsync(userId, PatientsPermissions.ReadRestricted, ct),
        };
    }

    private static Task<bool> DepartmentAsync(IServiceProvider services, RealtimeSubscriber subscriber, Guid departmentId, CancellationToken ct) =>
        services.GetRequiredService<HospitalDbContext>().Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AnyAsync(d => d.Id == departmentId && d.FacilityId == subscriber.FacilityId, ct);
}
