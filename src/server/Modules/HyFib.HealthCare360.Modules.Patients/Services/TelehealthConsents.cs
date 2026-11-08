using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// The patient's consent to be seen by video (<see cref="ConsentType.Telehealth"/>), which the Telemedicine Practice
/// Guidelines 2020 (Board of Governors in supersession of the MCI, s.3.3) ask the doctor to have before a teleconsultation.
/// It can be given in three places: on the portal when the patient books a video consultation, at the desk (the consents
/// panel), or by voice to the doctor at the start of the call, which the doctor records with what was said. It is a
/// standing permission: it lasts until the patient withdraws it or refuses it when asked again, and either ends every
/// earlier grant (<see cref="PatientConsent.IsStandingPermission"/>), so a grant not revoked is in force.
/// </summary>
public static class TelehealthConsents
{
    public const string RequiredCode = "telehealth.consent_required";

    public const string RequiredMessage =
        "The patient has not consented to a video consultation. Record their consent (taken by voice at the start of the call, at the desk or on the portal) before admitting them.";

    /// <summary>Whether the patient's consent to be seen by video is on record and in force.</summary>
    public static Task<bool> OnRecordAsync(HospitalDbContext db, Guid patientId, CancellationToken ct) =>
        db.Set<PatientConsent>().AnyAsync(c => c.PatientId == patientId && c.Type == ConsentType.Telehealth && c.Granted && c.RevokedAt == null, ct);

    /// <summary>When the consent in force was given; null when there is none.</summary>
    public static Task<DateTimeOffset?> GivenAtAsync(HospitalDbContext db, Guid patientId, CancellationToken ct) =>
        db.Set<PatientConsent>()
            .Where(c => c.PatientId == patientId && c.Type == ConsentType.Telehealth && c.Granted && c.RevokedAt == null)
            .OrderByDescending(c => c.CapturedAt)
            .Select(c => (DateTimeOffset?)c.CapturedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>The refusal a call is met with when the patient's consent is not on record.</summary>
    public static Error Required() => Error.Rule(RequiredCode, RequiredMessage);

    /// <summary>
    /// Adds a grant (the caller saves), given against the privacy notice in force as the desk's are: by voice to the doctor
    /// at the start of a call (<paramref name="notes"/> says what the patient was told and agreed to), or on the portal.
    /// </summary>
    public static async Task<PatientConsent> GrantAsync(
        HospitalDbContext db, ISettingsService settings, Guid patientId, Guid? capturedByUserId, Guid? facilityId, string notes, DateTimeOffset now,
        CancellationToken ct)
    {
        var consent = new PatientConsent
        {
            PatientId = patientId,
            Type = ConsentType.Telehealth,
            ConsentVersion = await PrivacyNotice.CurrentVersionAsync(settings, facilityId, ct),
            Granted = true,
            CapturedAt = now,
            CapturedByUserId = capturedByUserId,
            Notes = notes,
        };
        db.Add(consent);
        return consent;
    }
}
