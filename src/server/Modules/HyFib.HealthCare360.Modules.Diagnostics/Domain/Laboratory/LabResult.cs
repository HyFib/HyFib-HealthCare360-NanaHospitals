using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

/// <summary>Everything captured for one result entry, already interpreted against the patient's reference interval.</summary>
public sealed record ResultCapture(
    InterpretedResult Value,
    DeltaOutcome Delta,
    string? Remarks,
    string? Organism,
    string? ColonyCount,
    string? Method,
    string? InstrumentCode);

/// <summary>
/// One analyte result on one order item (LIS-02, LIS-04). A placeholder row is created when the tube is received,
/// then filled by manual entry or an analyser, then verified by a pathologist or auto-validated. The flag, the
/// reference text and the delta against the previous result are stored as they were at entry, because the printed
/// report must not change when masters are edited later. Every change is audited through the unit of work.
/// </summary>
public sealed class LabResult : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid LabOrderId { get; set; }
    public Guid LabOrderItemId { get; set; }

    /// <summary>The analyte (a component of a panel, or the test itself for a single-analyte test).</summary>
    public Guid LabTestId { get; set; }

    public Guid? SampleId { get; set; }
    public Guid PatientId { get; set; }
    public string TestCode { get; set; } = string.Empty;
    public string TestName { get; set; } = string.Empty;
    public string? GroupName { get; set; }
    public int DisplayOrder { get; set; }
    public LabResultType ResultType { get; set; }
    public LabResultStatus Status { get; set; }

    /// <summary>The result as printed ("13.5", "&lt;0.005", "Non-reactive").</summary>
    public string? ValueText { get; set; }

    public decimal? NumericValue { get; set; }
    public string? Unit { get; set; }
    public ResultFlag? Flag { get; set; }
    public string? ReferenceText { get; set; }
    public decimal? PreviousValue { get; set; }
    public DateTimeOffset? PreviousResultAt { get; set; }
    public decimal? DeltaPercent { get; set; }
    public bool DeltaExceeded { get; set; }
    public string? Method { get; set; }
    public string? InstrumentCode { get; set; }

    /// <summary>Verified by the auto-validation rules (in range, QC accepted, delta passed) rather than by a person.</summary>
    public bool AutoValidated { get; set; }

    public string? Remarks { get; set; }
    public string? Organism { get; set; }
    public string? ColonyCount { get; set; }
    public DateTimeOffset? EnteredAt { get; set; }
    public Guid? EnteredBy { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? VerifiedBy { get; set; }

    public List<LabSusceptibility> Susceptibilities { get; set; } = [];

    public bool IsCritical => Flag.IsCritical();

    /// <summary>Records (or re-records before verification) the value. Verified results change only through reopen or amendment.</summary>
    public Result Record(ResultCapture capture, Guid? by, DateTimeOffset at)
    {
        if (Status == LabResultStatus.Verified)
        {
            return Error.Rule("lab.result_verified", $"{TestName} is already verified. Reopen it, or amend the released report, to change it.");
        }

        Apply(capture);
        Status = LabResultStatus.Entered;
        EnteredAt = at;
        EnteredBy = by;
        return Result.Success();
    }

    public Result Verify(Guid? by, DateTimeOffset at, bool automatic)
    {
        switch (Status)
        {
            case LabResultStatus.Verified:
                return Result.Success();
            case LabResultStatus.Pending:
                return Error.Rule("lab.result_missing", $"Enter a value for {TestName} before verifying it.");
        }

        Status = LabResultStatus.Verified;
        VerifiedAt = at;
        VerifiedBy = automatic ? null : by;
        AutoValidated = automatic;
        return Result.Success();
    }

    /// <summary>Sends a verified (not yet released) result back for correction or a rerun.</summary>
    public Result Reopen()
    {
        if (Status != LabResultStatus.Verified)
        {
            return Error.Rule("lab.result_not_verified", $"{TestName} is not verified.");
        }

        Status = LabResultStatus.Entered;
        VerifiedAt = null;
        VerifiedBy = null;
        AutoValidated = false;
        return Result.Success();
    }

    /// <summary>
    /// Corrects a verified, released result. The amending pathologist becomes the verifier; the caller records the
    /// old and new values in the report's amendment history.
    /// </summary>
    public Result Amend(ResultCapture capture, Guid? by, DateTimeOffset at)
    {
        if (Status != LabResultStatus.Verified)
        {
            return Error.Rule("lab.result_not_verified", $"{TestName} is not verified; correct it through result entry instead.");
        }

        Apply(capture);
        VerifiedAt = at;
        VerifiedBy = by;
        AutoValidated = false;
        return Result.Success();
    }

    /// <summary>Printable value with its unit, used in amendment notes and alerts.</summary>
    public string Describe() =>
        ValueText is null ? "(no value)"
        : string.IsNullOrEmpty(Unit) ? ValueText
        : $"{ValueText} {Unit}";

    private void Apply(ResultCapture capture)
    {
        ValueText = capture.Value.ValueText;
        NumericValue = capture.Value.NumericValue;
        Flag = capture.Value.Flag;
        ReferenceText = capture.Value.ReferenceText;
        PreviousValue = capture.Delta.PreviousValue;
        PreviousResultAt = capture.Delta.PreviousAt;
        DeltaPercent = capture.Delta.Percent;
        DeltaExceeded = capture.Delta.Exceeded;
        Remarks = capture.Remarks;
        Organism = capture.Organism;
        ColonyCount = capture.ColonyCount;
        Method = capture.Method ?? Method;
        InstrumentCode = capture.InstrumentCode;
    }
}

/// <summary>Antibiotic susceptibility for a culture result (CLSI interpretation, optional MIC).</summary>
public sealed class LabSusceptibility : AuditableEntity
{
    public Guid LabResultId { get; set; }
    public string Antibiotic { get; set; } = string.Empty;
    public SusceptibilityInterpretation Interpretation { get; set; }

    /// <summary>Minimum inhibitory concentration as reported ("≤0.25 µg/mL"); null for disc diffusion.</summary>
    public string? Mic { get; set; }

    public int DisplayOrder { get; set; }
}
