using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>Reference checks shared by the OT, dialysis, physiotherapy and blood bank services.</summary>
internal static class SpecialtyLookups
{
    /// <summary>Work may only be booked for a live, active, un-merged patient record.</summary>
    public static async Task<Result> EnsurePatientAsync(HospitalDbContext db, Guid patientId, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == patientId)
            .Select(p => new { p.IsActive, p.IsDeceased, p.MergedIntoPatientId })
            .FirstOrDefaultAsync(ct);

        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        if (patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.merged", "This record was merged into another UHID. Use the surviving record.");
        }

        return patient.IsDeceased || !patient.IsActive
            ? Error.Rule("patient.inactive", "This patient record is inactive or marked deceased.")
            : Result.Success();
    }

    /// <summary>Every non-null practitioner id must be an active practitioner.</summary>
    public static async Task<Result> EnsurePractitionersAsync(HospitalDbContext db, IEnumerable<(string Field, Guid? Id)> practitioners, CancellationToken ct)
    {
        var wanted = practitioners.Where(p => p.Id is not null).ToList();
        if (wanted.Count == 0)
        {
            return Result.Success();
        }

        var ids = wanted.Select(p => p.Id!.Value).Distinct().ToList();
        var active = await db.Set<Practitioner>().AsNoTracking().Where(p => ids.Contains(p.Id) && p.IsActive).Select(p => p.Id).ToListAsync(ct);
        var missing = wanted.FirstOrDefault(p => !active.Contains(p.Id!.Value));
        return missing.Field is null ? Result.Success() : Error.Field(missing.Field, "Choose an active doctor.");
    }

    public static async Task<Dictionary<Guid, string>> PractitionerNamesAsync(HospitalDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return list.Count == 0
            ? []
            : await db.Set<Practitioner>().AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
    }

    /// <summary>The patient's current admission (from the PatientAdmitted projection), used to route charges to the IP bill.</summary>
    public static Task<AdmissionRecord?> ActiveAdmissionAsync(HospitalDbContext db, Guid patientId, CancellationToken ct) =>
        db.Set<AdmissionRecord>().AsNoTracking()
            .Where(a => a.PatientId == patientId && a.DischargedAt == null)
            .OrderByDescending(a => a.AdmittedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>Uses the ids the caller gave; otherwise attaches the patient's current admission, if any.</summary>
    public static async Task<(Guid? AdmissionId, Guid? VisitId)> EncounterAsync(HospitalDbContext db, Guid patientId, Guid? admissionId, Guid? visitId, CancellationToken ct)
    {
        if (admissionId is not null || visitId is not null)
        {
            return (admissionId, visitId);
        }

        var admission = await ActiveAdmissionAsync(db, patientId, ct);
        return admission is null ? (null, null) : (admission.Id, admission.VisitId);
    }

    public static StaffStamp Stamp(this ICurrentUser user, DateTimeOffset at) =>
        new(user.UserId, user.DisplayName ?? user.UserName ?? "Unknown user", at);
}
