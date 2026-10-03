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
/// <see cref="StationAeTitle"/> is an exact match; a study with no station assigned matches every station.
/// <see cref="AccessionNumber"/> and <see cref="PatientId"/> (the UHID) are exact, or DICOM wildcard patterns when they
/// hold <c>*</c> or <c>?</c>, without regard to case. <see cref="PatientNamePrefix"/> keeps patients whose DICOM family
/// name component starts with it (without regard to case). <see cref="FromTime"/> and <see cref="ToTime"/> (inclusive,
/// hospital time) limit the start time on each day. Every filter is applied only when set. Entries come earliest
/// first, or nearest to <see cref="Around"/> first when it is set; <see cref="Offset"/> entries in that order are
/// skipped, and at most <see cref="Limit"/> (never more than <see cref="MaxLimit"/>) are returned.
/// </summary>
public sealed record ModalityWorklistQuery(
    Guid FacilityId,
    DateOnly From,
    DateOnly To,
    IReadOnlyCollection<string> Modalities,
    string? StationAeTitle = null,
    string? AccessionNumber = null,
    string? PatientId = null,
    int Limit = 500)
{
    /// <summary>Most entries returned for one query, whatever <see cref="Limit"/> asks for.</summary>
    public const int MaxLimit = 1000;

    public string? PatientNamePrefix { get; init; }

    public TimeOnly? FromTime { get; init; }

    public TimeOnly? ToTime { get; init; }

    public DateTimeOffset? Around { get; init; }

    public int Offset { get; init; }
}

/// <summary>
/// Implemented by the Diagnostics module (radiology). The DICOM modality worklist server in Integrations resolves it in
/// a new DI scope for every query. It runs outside any request, so implementations filter by
/// <see cref="ModalityWorklistQuery.FacilityId"/> themselves.
/// </summary>
public interface IModalityWorklistSource
{
    Task<IReadOnlyList<ModalityWorklistItem>> FindAsync(ModalityWorklistQuery query, CancellationToken cancellationToken);
}
