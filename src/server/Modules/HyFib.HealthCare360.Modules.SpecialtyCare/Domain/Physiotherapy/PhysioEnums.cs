namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;

public enum PhysioReferralStatus
{
    New = 0,
    Assessed,
    InTreatment,
    Completed,
    Cancelled,
}

/// <summary>Electrotherapy, thermotherapy and exercise modalities offered by Indian physiotherapy departments.</summary>
public enum PhysioModality
{
    /// <summary>Interferential therapy.</summary>
    IFT = 0,

    /// <summary>Transcutaneous electrical nerve stimulation.</summary>
    TENS,

    /// <summary>Therapeutic ultrasound.</summary>
    UST,

    /// <summary>Short-wave diathermy.</summary>
    SWD,
    HotPack,
    ColdPack,
    Traction,
    Exercises,
    Mobilisation,
    ChestPhysiotherapy,
    GaitTraining,
    WaxBath,
    Laser,
    Other,
}

public enum PhysioAssessmentType
{
    Initial = 0,
    Review,
    Discharge,
}

public enum PhysioSessionStatus
{
    Scheduled = 0,
    Attended,
    Missed,
    Cancelled,
}
