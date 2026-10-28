namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

/// <summary>Laboratory bench / department; drives worklists and sample routing.</summary>
public enum LabSection
{
    Hematology = 0,
    Biochemistry,
    Microbiology,
    Serology,
    ClinicalPathology,
    Histopathology,
    Immunology,
}

public enum SpecimenType
{
    WholeBlood = 0,
    Serum,
    Plasma,
    Urine,
    Stool,
    Swab,
    Sputum,
    Csf,
    BodyFluid,
    Tissue,
    Other,
}

/// <summary>Collection container (cap colours follow the common Indian vacutainer convention).</summary>
public enum ContainerType
{
    /// <summary>K2/K3 EDTA, lavender cap — haematology, HbA1c, blood group.</summary>
    Edta = 0,

    /// <summary>Plain / clot activator, red or yellow cap — serum chemistry, serology, immunoassays.</summary>
    Plain,

    /// <summary>Sodium fluoride, grey cap — glucose (stops glycolysis).</summary>
    Fluoride,

    /// <summary>3.2 % sodium citrate, light-blue cap — coagulation.</summary>
    Citrate,

    /// <summary>Lithium heparin, green cap — stat chemistry.</summary>
    Heparin,

    UrineContainer,
    Swab,
    StoolContainer,

    /// <summary>Sterile container for cultures.</summary>
    SterileContainer,
}

public enum LabResultType
{
    Numeric = 0,
    Text,

    /// <summary>One of the answers listed on the test (Negative / Positive, 1+ / 2+ …).</summary>
    Options,

    /// <summary>Culture: growth statement, organism, colony count and antibiotic susceptibilities.</summary>
    Culture,
}

/// <summary>Interpretation flag printed next to a result.</summary>
public enum ResultFlag
{
    Normal = 0,
    Low,
    High,
    CriticalLow,
    CriticalHigh,
    Abnormal,

    /// <summary>Critical non-numeric result (for example a falciparum-positive malaria test).</summary>
    Critical,
}

public enum LabOrderSource
{
    /// <summary>Created from a clinician's order (Clinical module, ServiceOrderPlaced).</summary>
    ClinicalOrder = 0,
    WalkIn,
    OutsideReferral,
}

public enum LabOrderStatus
{
    Ordered = 0,
    InProgress,

    /// <summary>Every active test is released.</summary>
    Completed,
    Cancelled,
}

public enum LabOrderItemStatus
{
    Ordered = 0,
    Collected,
    InLab,
    ResultsEntered,
    Verified,
    Released,
    Cancelled,
}

public enum SampleStatus
{
    Pending = 0,
    Collected,
    Received,
    InProcess,
    Completed,
    Rejected,

    /// <summary>Every test on the tube was cancelled before it was processed.</summary>
    Cancelled,
}

/// <summary>Standard pre-analytical rejection reasons (reported as a NABH quality indicator).</summary>
public enum SampleRejectionReason
{
    Haemolysed = 0,
    Clotted,
    InsufficientVolume,
    WrongContainer,
    Mislabelled,
    Unlabelled,
    Lipaemic,
    DelayedTransport,
    Leaked,
    Contaminated,
    Other,
}

public enum LabResultStatus
{
    Pending = 0,
    Entered,
    Verified,
}

public enum LabReportStatus
{
    Draft = 0,

    /// <summary>Every active test is verified and waiting for release.</summary>
    Verified,

    /// <summary>At least one test is released (see <c>IsPartial</c> for pending tests).</summary>
    Released,

    /// <summary>Released and later corrected; the amendment history is printed on the report.</summary>
    Amended,
}

public enum SusceptibilityInterpretation
{
    Sensitive = 0,
    Intermediate,
    Resistant,
}

public enum InstrumentProtocol
{
    Hl7 = 0,
    Astm,
}

public static class ResultFlags
{
    public static bool IsCritical(this ResultFlag? flag) =>
        flag is ResultFlag.CriticalLow or ResultFlag.CriticalHigh or ResultFlag.Critical;

    public static bool IsCritical(this ResultFlag flag) => ((ResultFlag?)flag).IsCritical();
}

/// <summary>On what an HIV test is done (HIV and AIDS Act 2017, s.5 and 6).</summary>
public enum ConsentBasis
{
    /// <summary>The patient gave informed consent, after pre-test counselling.</summary>
    Patient = 0,

    /// <summary>The patient's representative did (for a child, or a patient who cannot consent).</summary>
    Representative,

    /// <summary>A court ordered the test (s.6).</summary>
    CourtOrder,
}
