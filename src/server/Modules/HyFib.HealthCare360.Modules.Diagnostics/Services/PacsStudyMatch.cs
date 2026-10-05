using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Decides whether what PACS answered for a study's accession number (or study UID) is that study's images (pure;
/// unit-tested). It fails closed: exactly one PACS study must match, and it must carry a patient ID equal to the
/// patient's UHID. Several studies, a study without a patient ID or under another one are refused, because linking or
/// opening them could show another patient's images.
/// </summary>
internal static class PacsStudyMatch
{
    public const string NotFound = "pacs.study_not_found";
    public const string Ambiguous = "pacs.ambiguous_study";
    public const string PatientIdMissing = "pacs.patient_id_missing";
    public const string PatientMismatch = "pacs.patient_mismatch";
    public const string PatientUnknown = "pacs.patient_unknown";

    /// <summary>
    /// The refusals that mean PACS holds something under the accession that cannot be shown as this patient's.
    /// </summary>
    public static bool IsPatientSafety(string? code) => code is Ambiguous or PatientIdMissing or PatientMismatch;

    /// <param name="found">The studies PACS returned for the query.</param>
    /// <param name="study">The HC360 study looked for.</param>
    /// <param name="byAccession">True when PACS was searched by accession number, false when by study UID.</param>
    /// <param name="uhid">The patient's UHID; null or empty when it could not be read.</param>
    public static Result<ImagingStudyInfo> Pick(
        IReadOnlyList<ImagingStudyInfo> found, ImagingStudy study, bool byAccession, string? uhid)
    {
        var matches = found
            .Where(s => byAccession
                ? string.Equals(s.AccessionNumber?.Trim(), study.AccessionNumber, StringComparison.OrdinalIgnoreCase)
                : string.Equals(s.StudyInstanceUid.Trim(), study.StudyInstanceUid, StringComparison.Ordinal))
            .DistinctBy(s => s.StudyInstanceUid.Trim())
            .ToList();
        if (matches.Count == 0)
        {
            return byAccession
                ? Error.Rule(NotFound, $"No images for accession {study.AccessionNumber} have reached PACS yet.")
                : Error.Rule(NotFound,
                    $"No images for {study.AccessionNumber} have reached PACS yet. Its accession number is longer than "
                    + "DICOM allows, so HC360 looks for the study by its UID, which reaches the modality only through "
                    + "the worklist.");
        }

        if (matches.Count > 1)
        {
            return Error.Rule(Ambiguous,
                $"PACS holds {matches.Count} studies with accession {study.AccessionNumber}, so HC360 cannot tell "
                + "which are this patient's images. Merge or correct the studies in PACS, then link again.");
        }

        var match = matches[0];
        if (!DicomConventions.IsValidUid(match.StudyInstanceUid))
        {
            return Error.Rule("pacs.invalid_uid", "PACS returned an invalid Study Instance UID for this accession.");
        }

        if (string.IsNullOrWhiteSpace(uhid))
        {
            return Error.Rule(PatientUnknown,
                "The patient's UHID could not be read, so HC360 cannot check that the images in PACS are this "
                + "patient's.");
        }

        if (string.IsNullOrWhiteSpace(match.PatientId))
        {
            return Error.Rule(PatientIdMissing,
                $"PACS holds accession {study.AccessionNumber} without a patient ID, so HC360 cannot check that the "
                + "images are this patient's. Correct the study in PACS before linking.");
        }

        if (!string.Equals(match.PatientId.Trim(), uhid.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Error.Rule(PatientMismatch,
                $"PACS holds accession {study.AccessionNumber} under patient ID {match.PatientId.Trim()}, not "
                + $"{uhid.Trim()}. Correct the study at the modality/PACS before linking.");
        }

        return match;
    }
}
