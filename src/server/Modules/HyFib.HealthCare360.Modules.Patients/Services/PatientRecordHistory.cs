using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// The history of a patient's registration record and of a visit, for staff who may read them: who registered, corrected
/// or merged the record, who checked the visit in, moved it or closed it, and what each changed. A restricted (VIP)
/// patient's opens as the record does, and it keeps from its reader what the record keeps from them: contact details are
/// masked for staff who see them masked (PRV-03), and the record's sensitive categories (HIV, mental health, MTP, sexual
/// violence), and each change of them, are left out for staff who may not manage them.
/// </summary>
internal sealed class PatientRecordHistory(
    HospitalDbContext db, PatientAccessGuard guard, PatientContactAccess contacts, ICurrentUser user, IPermissionService permissions)
    : IRecordHistorySource
{
    /// <summary>The fields <see cref="PatientContacts"/> masks for staff without the full contact details, as the trail names them.</summary>
    private static readonly string[] ContactFields =
        ["phone", "alternatePhone", "email", "emergencyContactPhone", "address.line1", "address.line2", "address.pinCode"];

    /// <summary>The record's sensitive categories, as the trail names them, and the events that record a change of them.</summary>
    private static readonly string[] SensitiveFields = ["sensitiveCategories"];

    private static readonly string[] SensitiveActions = ["patient.sensitive_changed", "patient.sensitive_flagged"];

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
        if (open.IsFailure)
        {
            return open.Error!;
        }

        if (entityType == nameof(Visit))
        {
            return new HistoryRecord(found.Title, found.PatientId);
        }

        var fullContacts = await contacts.FullAsync(ct);
        var managesSensitive = user.UserId is { } userId && await permissions.HasPermissionAsync(userId, PatientsPermissions.SensitiveManage, ct);
        return new HistoryRecord(found.Title, found.PatientId)
        {
            Redaction = new HistoryRedaction(
                fullContacts ? [] : ContactFields,
                managesSensitive ? [] : SensitiveFields,
                managesSensitive ? [] : SensitiveActions),
        };
    }
}
