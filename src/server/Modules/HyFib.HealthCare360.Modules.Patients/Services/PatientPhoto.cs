using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Files;

namespace HyFib.HealthCare360.Modules.Patients.Services;

/// <summary>
/// The rules for a patient's photo (FO-01, photo capture). The photo is a document in the encrypted file store,
/// uploaded by the staff app in the <see cref="Category"/> category and referenced from the patient by id.
/// </summary>
internal static class PatientPhoto
{
    public const string Category = "photo";

    /// <summary>The staff app shrinks photos to under 200 KB; this only stops a full-size original being used as an avatar.</summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    private static readonly string[] ImageTypes = ["image/jpeg", "image/png", "image/webp"];

    /// <summary>
    /// The photo a patient has after a save. A save that says nothing about the photo keeps the current one, which is what
    /// every edit of the other details does; a photo is removed only when a save asks for it.
    /// </summary>
    public static Guid? Resolve(Guid? current, Guid? requested, bool remove) => remove ? null : requested ?? current;

    /// <summary>
    /// The patients, other than <paramref name="exceptPatientId"/> (null when registering), whose photo is this file. A photo
    /// file is one person's: it is never handed from a patient to another.
    /// </summary>
    public static IQueryable<Patient> UsedByOthers(HospitalDbContext db, Guid fileId, Guid? exceptPatientId) =>
        db.Set<Patient>().Where(p => p.PhotoFileId == fileId && (exceptPatientId == null || p.Id != exceptPatientId));

    /// <summary>Why <paramref name="file"/> cannot be a patient's photo, in a sentence for the front desk; null when it can.</summary>
    public static string? Problem(StoredFile file)
    {
        if (!string.Equals(file.Category, Category, StringComparison.Ordinal))
        {
            return "That file was not uploaded as a photo. Take or upload the photo again.";
        }

        if (!ImageTypes.Contains(file.ContentType, StringComparer.Ordinal))
        {
            return "The photo must be a JPEG, PNG or WebP image.";
        }

        return file.SizeBytes > MaxBytes ? "The photo is too large. Use a photo under 5 MB." : null;
    }
}
