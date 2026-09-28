namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

public enum BloodComponentType
{
    WholeBlood = 0,

    /// <summary>Packed red blood cells.</summary>
    PRBC,

    /// <summary>Fresh frozen plasma.</summary>
    FFP,

    /// <summary>Random-donor platelet concentrate.</summary>
    PlateletConcentrate,
    Cryoprecipitate,
}

public enum BloodUnitStatus
{
    /// <summary>Collected/prepared but grouping and TTI screening are not complete: must not be issued.</summary>
    Quarantine = 0,
    Available,

    /// <summary>Cross-matched compatible and held for one blood request.</summary>
    Reserved,
    Issued,
    Discarded,
    Expired,
}

public enum TtiResult
{
    Pending = 0,
    NonReactive,
    Reactive,
}

public enum DonationType
{
    Voluntary = 0,

    /// <summary>A relative/friend donating to replace blood used by a patient.</summary>
    Replacement,
}

/// <summary>Number of satellite bags, which limits the components that can be separated.</summary>
public enum BloodBagType
{
    Single = 0,
    Double,
    Triple,
    Quadruple,
}

public enum DonationStatus
{
    Collected = 0,
    Separated,
    Discarded,
}

public enum DonorEligibilityOutcome
{
    Eligible = 0,
    TemporarilyDeferred,
    PermanentlyDeferred,
}

public enum BloodRequestUrgency
{
    Routine = 0,
    Urgent,

    /// <summary>Life-threatening bleeding: allows authorised release of uncross-matched O Rh(D)-negative red cells / AB plasma.</summary>
    Emergency,
}

public enum BloodRequestStatus
{
    /// <summary>Awaiting the patient's sample and blood-bank grouping.</summary>
    Pending = 0,
    InProcess,

    /// <summary>All requested units are cross-matched and reserved.</summary>
    Ready,
    PartiallyIssued,
    Issued,
    Cancelled,
}

public enum CrossmatchMethod
{
    ImmediateSpin = 0,

    /// <summary>Indirect antiglobulin (AHG/Coombs) cross-match.</summary>
    AntiHumanGlobulin,
    GelCard,
    Electronic,
}

public enum CrossmatchResult
{
    Compatible = 0,
    Incompatible,
}

public enum ReactionSymptom
{
    Fever = 0,
    ChillsRigors,
    Urticaria,
    Itching,
    Flushing,
    Dyspnoea,
    Wheeze,
    Hypotension,
    Hypertension,
    Tachycardia,
    ChestPain,
    BackPain,
    Haemoglobinuria,
    Oliguria,
    NauseaVomiting,
    Other,
}

/// <summary>Classification used by the Haemovigilance Programme of India (HvPI).</summary>
public enum ReactionType
{
    Unclassified = 0,
    FebrileNonHaemolytic,
    AllergicMinor,
    Anaphylactic,
    AcuteHaemolytic,
    DelayedHaemolytic,
    TransfusionAssociatedCirculatoryOverload,
    TransfusionRelatedAcuteLungInjury,
    BacterialContamination,
    Hypotensive,
    Other,
}

public enum ReactionSeverity
{
    Mild = 0,
    Moderate,
    Severe,
    LifeThreatening,
    Death,
}

public enum ReactionStatus
{
    Reported = 0,
    UnderInvestigation,
    Closed,
}
