namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>What the chart says about allergies, as shown on the patient banner (EHR-03).</summary>
public enum AllergyRecordingStatus
{
    /// <summary>At least one active allergy is on the chart.</summary>
    Recorded = 0,

    /// <summary>A clinician recorded "no known allergies" and no active allergy has been recorded since.</summary>
    NoKnownAllergies,

    /// <summary>Neither: nobody has recorded allergies or their absence (inactive or resolved allergies only count as not recorded).</summary>
    NotRecorded,
}

/// <summary>
/// A clinician's statement that the patient has no known allergies (NKA), so the banner can say so instead of
/// "Allergies not recorded" — an empty allergy list alone never claims it. Patient-wide like <see cref="Allergy"/>,
/// with at most one live statement per patient. It cannot be recorded while an active allergy exists, and recording
/// (or re-activating) an allergy removes it in the same save; removed statements stay soft-deleted as history.
/// </summary>
public sealed class NoKnownAllergiesStatement : AuditableEntity
{
    public Guid PatientId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }

    /// <summary>Who recorded it, as shown on the banner ("NKA recorded by Dr. …").</summary>
    public string? RecordedByName { get; set; }

    /// <summary>
    /// Active allergies always win: a stale statement next to an active allergy never hides it, and "no known
    /// allergies" is claimed only when it was recorded.
    /// </summary>
    public static AllergyRecordingStatus StatusOf(bool hasActiveAllergy, bool hasStatement) =>
        hasActiveAllergy ? AllergyRecordingStatus.Recorded
        : hasStatement ? AllergyRecordingStatus.NoKnownAllergies
        : AllergyRecordingStatus.NotRecorded;

    /// <summary>"No known allergies" contradicts an active allergy on the chart; those must be resolved or corrected first.</summary>
    public static Result CanRecord(int activeAllergies) =>
        activeAllergies > 0
            ? Error.Conflict(
                "allergy.active_exist",
                $"The patient has {activeAllergies} active {(activeAllergies == 1 ? "allergy" : "allergies")}. Mark them resolved, inactive or entered in error before recording 'No known allergies'.",
                new { activeAllergies })
            : Result.Success();
}
