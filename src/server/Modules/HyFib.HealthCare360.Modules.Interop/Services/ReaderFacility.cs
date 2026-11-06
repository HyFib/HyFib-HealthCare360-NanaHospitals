using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Identity;
using ClinicalEncounter = HyFib.HealthCare360.Modules.Clinical.Domain.Encounter;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// The facility a FHIR reader reads for, decided by the hospital (#251): the facility of the reader's session, which
/// must be one its account is assigned to. A partner reads that facility's records only, and its patients: a patient
/// registered there, or with a visit, an encounter or a stay there. Allergies are the patient's, not a facility's, and are
/// read whole. The ABDM export reads past this, as a staff download for the patient's own health locker.
/// </summary>
internal static class ReaderFacility
{
    /// <summary>The facility read for. With none, no facility's records match (the API refuses such a reader first).</summary>
    public static Guid Of(HospitalDbContext db) => db.CurrentFacilityId ?? Guid.Empty;

    /// <summary>
    /// Whether the account reads for the facility of its session because it is assigned to it. An account with no
    /// facility assignment may sign in to any facility, so it is not a partner of any one of them.
    /// </summary>
    public static async Task<bool> IsAssignedAsync(HospitalDbContext db, Guid? userId, CancellationToken ct) =>
        userId is { } user && db.CurrentFacilityId is { } facility
        && await db.Set<UserFacility>().AsNoTracking().AnyAsync(f => f.UserId == user && f.FacilityId == facility, ct);

    /// <summary>The patients of the reader's facility, as a filter on a patient query.</summary>
    public static Expression<Func<Patient, bool>> SeesPatient(HospitalDbContext db)
    {
        var facility = Of(db);
        var visits = db.Set<Visit>();
        var encounters = db.Set<ClinicalEncounter>();
        var admissions = db.Set<Admission>();
        return p => p.RegisteredFacilityId == facility
                    || visits.Any(v => v.PatientId == p.Id && v.FacilityId == facility)
                    || encounters.Any(e => e.PatientId == p.Id && e.FacilityId == facility)
                    || admissions.Any(a => a.PatientId == p.Id && a.FacilityId == facility);
    }

    /// <summary>
    /// Whether the patient is one of the reader's facility's, read in queries of their own: the patient row it is asked
    /// about may be a removed one, read past its soft-delete filter, and the visits, encounters and stays must keep theirs.
    /// </summary>
    public static async Task<bool> SeesPatientAsync(HospitalDbContext db, Guid patientId, Guid registeredFacilityId, CancellationToken ct)
    {
        var facility = Of(db);
        return registeredFacilityId == facility
               || await db.Set<Visit>().AsNoTracking().AnyAsync(v => v.PatientId == patientId && v.FacilityId == facility, ct)
               || await db.Set<ClinicalEncounter>().AsNoTracking().AnyAsync(e => e.PatientId == patientId && e.FacilityId == facility, ct)
               || await db.Set<Admission>().AsNoTracking().AnyAsync(a => a.PatientId == patientId && a.FacilityId == facility, ct);
    }
}
