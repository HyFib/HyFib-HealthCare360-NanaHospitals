namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;

/// <summary>
/// Blood-borne virus status used to segregate dialysis machines. A co-infected patient is assigned the dedication
/// chosen by the nephrologist (usually HBsAg, the most transmissible in dialysis units).
/// </summary>
public enum SeroStatus
{
    Negative = 0,
    HBsAg,
    HCV,
    HIV,
}

public enum VascularAccessType
{
    /// <summary>Arteriovenous fistula.</summary>
    AVF = 0,

    /// <summary>Arteriovenous graft.</summary>
    AVG,

    /// <summary>Tunnelled or temporary haemodialysis catheter.</summary>
    Catheter,
}

public enum DialysisShift
{
    Morning = 0,
    Afternoon,
    Evening,
    Night,
}

public enum DialysisMachineStatus
{
    Available = 0,
    InUse,
    Maintenance,
    Retired,
}

public enum DialysisSessionStatus
{
    Scheduled = 0,
    InProgress,
    Completed,
    Cancelled,

    /// <summary>The patient did not come (tracked separately because missed dialysis is a clinical risk).</summary>
    Missed,
}

public enum DialyzerReusePolicy
{
    SingleUse = 0,
    Reuse,
}

public enum DialysisComplication
{
    Hypotension = 0,
    MuscleCramps,
    NauseaVomiting,
    Headache,
    ChestPain,
    FeverRigors,
    Arrhythmia,
    AccessBleeding,
    CircuitClotting,
    Hypoglycaemia,
    Other,
}
