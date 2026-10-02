using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Services.Handlers;

/// <summary>What the handlers that start follow-ups by themselves share (CRM-02).</summary>
internal static class AutomaticFollowUps
{
    /// <summary>Only a registered, living patient whose record was not merged into another is followed up.</summary>
    public static Task<bool> CanFollowUpAsync(HospitalDbContext db, Guid patientId, CancellationToken ct) =>
        db.Set<Patient>().AnyAsync(p => p.Id == patientId && p.IsActive && !p.IsDeceased && p.MergedIntoPatientId == null, ct);

    /// <summary>The patient's open tasks in every facility, tracked so a handler can close them.</summary>
    public static Task<List<FollowUpTask>> OpenTasksAsync(HospitalDbContext db, Guid patientId, CancellationToken ct) =>
        db.Set<FollowUpTask>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(t => t.PatientId == patientId && (t.Status == FollowUpStatus.Open || t.Status == FollowUpStatus.InProgress))
            .ToListAsync(ct);

    /// <summary>Adds an automatic task unless one with the same key and type was already added, so a replayed event adds nothing.</summary>
    public static async Task AddOnceAsync(HospitalDbContext db, FollowUpTask task, CancellationToken ct)
    {
        if (db.Set<FollowUpTask>().Local.Any(t => t.Source == task.Source && t.SourceReference == task.SourceReference && t.Type == task.Type)
            || await db.Set<FollowUpTask>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .AnyAsync(t => t.Source == task.Source && t.SourceReference == task.SourceReference && t.Type == task.Type, ct))
        {
            return;
        }

        db.Add(task);
    }
}
