namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

public enum AnaesthesiaType
{
    GA = 0,
    Spinal,
    Epidural,
    Regional,
    Local,
    Sedation,
}

public enum SurgeryPriority
{
    Elective = 0,
    Urgent,
    Emergency,
}

public enum SurgeryStatus
{
    /// <summary>Asked for (by a surgeon or from an EMR order) but not yet given a theatre and slot.</summary>
    Requested = 0,

    /// <summary>Theatre, slot and surgeon confirmed; the slot is protected against double booking.</summary>
    Scheduled,

    /// <summary>Patient received in the pre-operative holding area.</summary>
    InPreOp,

    /// <summary>Knife to skin (only after WHO Sign-in and Time-out).</summary>
    InProgress,

    /// <summary>Patient left the theatre (only after WHO Sign-out).</summary>
    Completed,
    Cancelled,
    Postponed,
}

/// <summary>Operative side, checked at site marking and at WHO Time-out to prevent wrong-site surgery.</summary>
public enum Laterality
{
    NotApplicable = 0,
    Left,
    Right,
    Bilateral,
}

public enum ChecklistItemState
{
    Pending = 0,
    Done,
    NotApplicable,
}
