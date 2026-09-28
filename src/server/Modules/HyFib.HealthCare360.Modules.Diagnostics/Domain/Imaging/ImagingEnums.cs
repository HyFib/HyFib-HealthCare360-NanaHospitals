namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

/// <summary>Imaging modality (DICOM codes; XR is conventional/computed radiography and is sent to the MWL as CR).</summary>
public enum Modality
{
    XR = 0,
    CT,
    MR,
    US,
    MG,
    RF,
    DX,
}

/// <summary>Radiology study workflow (IMG-01..04).</summary>
public enum ImagingStudyStatus
{
    Ordered = 0,
    Scheduled,
    Arrived,
    InProgress,
    Acquired,
    Reported,
    Verified,
    Released,
    Cancelled,
}

/// <summary>Why images were repeated or rejected (IMG-04 reject analysis; feeds radiographer training).</summary>
public enum ImageRepeatReason
{
    Positioning = 0,
    Exposure,
    PatientMotion,
    Artefact,
    EquipmentFault,
    WrongProtocol,
    PatientRelated,
    Other,
}

/// <summary>Review state of an external AI engine's preliminary finding (IMG-05).</summary>
public enum AiFindingStatus
{
    Preliminary = 0,
    Acknowledged,
    Dismissed,
}
