using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// The history of an admission, for staff who may read admissions: the admission (its consultants, discharge advice and
/// discharge) and each bed the patient was put in or moved to, who made each change and what it changed.
/// </summary>
internal sealed class AdmissionRecordHistory(HospitalDbContext db, PatientAccessGuard guard) : IRecordHistorySource
{
    public IReadOnlyCollection<string> EntityTypes { get; } = [nameof(Admission)];

    public IReadOnlyCollection<string> ReadPermissions(string entityType) => [InpatientPermissions.AdmissionsRead];

    public async Task<Result<HistoryRecord>> OpenAsync(string entityType, Guid id, CancellationToken ct)
    {
        var admission = await db.Set<Admission>().Where(a => a.Id == id).Select(a => new { a.PatientId, a.AdmissionNumber }).FirstOrDefaultAsync(ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), id);
        }

        var open = await guard.EnsureCanOpenAsync(admission.PatientId, ct);
        if (open.IsFailure)
        {
            return open.Error!;
        }

        // Deleted ones too: their deletion is the admission's history.
        var beds = await (
            from assignment in db.Set<BedAssignment>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            where assignment.AdmissionId == id
            join bed in db.Set<Bed>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]) on assignment.BedId equals bed.Id into found
            from bed in found.DefaultIfEmpty()
            select new { assignment.Id, Bed = bed == null ? null : bed.Label })
            .ToListAsync(ct);
        return new HistoryRecord($"admission {admission.AdmissionNumber}", admission.PatientId,
            [.. beds.Select(b => new HistoryPart(nameof(BedAssignment), b.Id, b.Bed is { } label ? $"Bed {label}" : "Bed"))]);
    }
}
