using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// The history of a patient's registration record and of a visit, for staff who may read them: who registered, corrected
/// or merged the record, who checked the visit in, moved it or closed it, and what each changed. A restricted (VIP)
/// patient's opens as the record does.
/// </summary>
internal sealed class PatientRecordHistory(HospitalDbContext db, PatientAccessGuard guard) : IRecordHistorySource
{
    public IReadOnlyCollection<string> EntityTypes { get; } = [nameof(Patient), nameof(Visit)];

    public IReadOnlyCollection<string> ReadPermissions(string entityType) =>
        entityType == nameof(Visit) ? [PatientsPermissions.VisitsRead] : [PatientsPermissions.Read];

    public async Task<Result<HistoryRecord>> OpenAsync(string entityType, Guid id, CancellationToken ct)
    {
        var found = entityType == nameof(Visit)
            ? await db.Set<Visit>().Where(v => v.Id == id).Select(v => new { v.PatientId, Title = "visit " + v.VisitNumber }).FirstOrDefaultAsync(ct)
            : await db.Set<Patient>().Where(p => p.Id == id).Select(p => new { PatientId = p.Id, Title = "patient " + p.Uhid }).FirstOrDefaultAsync(ct);
        if (found is null)
        {
            return Error.NotFound(entityType, id);
        }

        var open = await guard.EnsureCanOpenAsync(found.PatientId, ct);
        return open.IsFailure ? open.Error! : new HistoryRecord(found.Title, found.PatientId);
    }
}
