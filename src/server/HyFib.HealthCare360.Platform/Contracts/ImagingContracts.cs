namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>
/// One entry of the DICOM modality worklist (IMG-01, IMG-02), already in DICOM form: names as PN
/// (<c>FAMILY^GIVEN^MIDDLE</c>), dates <c>yyyyMMdd</c>, times <c>HHmmss</c>, sex M/F/O, the modality as its DICOM code (CR
/// for conventional radiography) and 2.25 UIDs. The accession number is HC360's own and can be longer than the 16
/// characters DICOM allows (<see cref="AccessionFitsDicom"/>); the worklist server then leaves it out and never shortens
/// it, and the images link to the study by <see cref="StudyInstanceUid"/> instead.
/// </summary>
/// <param name="ScheduledStepStatus">SCHEDULED, ARRIVED or STARTED.</param>
/// <param name="ScheduledStepLocation">The room, when it fits a DICOM short string (16 characters).</param>
/// <param name="RequestedProcedurePriority">STAT, HIGH or ROUTINE.</param>
/// <param name="PlacerOrderNumber">The clinician's order number, when the study came from an order.</param>
public sealed record ModalityWorklistItem(
    Guid StudyId,
    Guid FacilityId,
    string AccessionNumber,
    string RequestedProcedureId,
    string ScheduledProcedureStepId,
    string StudyInstanceUid,
    string PatientId,
    string PatientName,
    string? PatientBirthDate,
    string PatientSex,
    string Modality,
    string? ScheduledStationAeTitle,
    string ScheduledStepStartDate,
    string ScheduledStepStartTime,
    string ScheduledStepStatus,
    string? ScheduledStepLocation,
    string RequestedProcedureDescription,
    string? BodyPartExamined,
    string? ReferringPhysicianName,
    string RequestedProcedurePriority,
    string? PlacerOrderNumber)
{
    /// <summary>The longest DICOM short string (SH): accession numbers, procedure ids, AE titles.</summary>
    public const int MaxShortStringLength = 16;

    /// <summary>False when DICOM's Accession Number (SH, 16 characters) cannot hold this accession number.</summary>
    public bool AccessionFitsDicom => AccessionNumber.Length is > 0 and <= MaxShortStringLength;
}

/// <summary>
/// The worklist entries a modality may see: one facility's imaging studies still to be done (ordered, scheduled,
/// arrived or started) whose procedure step starts on a day from <see cref="From"/> to <see cref="To"/> (hospital time).
/// <see cref="Modalities"/> are DICOM modality codes (CR, DX, CT, MR, US, MG, RF); empty means every modality.
/// <see cref="StationAeTitle"/>, <see cref="AccessionNumber"/> and <see cref="PatientId"/> (the UHID) are exact matches,
/// applied when set; a study with no station assigned matches every station. At most <see cref="Limit"/> entries are
/// returned, earliest first.
/// </summary>
public sealed record ModalityWorklistQuery(
    Guid FacilityId,
    DateOnly From,
    DateOnly To,
    IReadOnlyCollection<string> Modalities,
    string? StationAeTitle = null,
    string? AccessionNumber = null,
    string? PatientId = null,
    int Limit = 500);

/// <summary>
/// Implemented by the Diagnostics module (radiology). The DICOM modality worklist server in Integrations resolves it in
/// a new DI scope for every query. It runs outside any request, so implementations filter by
/// <see cref="ModalityWorklistQuery.FacilityId"/> themselves.
/// </summary>
public interface IModalityWorklistSource
{
    Task<IReadOnlyList<ModalityWorklistItem>> FindAsync(ModalityWorklistQuery query, CancellationToken cancellationToken);
}
