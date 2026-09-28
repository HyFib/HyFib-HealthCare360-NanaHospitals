namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;

public enum QcOutcome
{
    Accepted = 0,

    /// <summary>Only the 1-2s warning rule fired: patient results may be reported, but the run needs attention.</summary>
    AcceptedWithWarning,

    /// <summary>A rejection rule fired: verification of the assay's results is locked until QC passes or is overridden.</summary>
    Rejected,
}

/// <summary>
/// A lot of control material at one level with its established target mean and SD (LIS-06). QC runs are judged
/// against the lot's own targets, so z-scores from different levels and lots can be placed on one Levey-Jennings
/// chart and evaluated together by the Westgard rules.
/// </summary>
public sealed class QcLot : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>The analyte the control is run for (tests in the same QC group share it).</summary>
    public Guid LabTestId { get; set; }

    /// <summary>Control material, for example "Bio-Rad Lyphochek Assayed Chemistry Control".</summary>
    public string Material { get; set; } = string.Empty;

    /// <summary>Control level (1 = low/normal, 2 = abnormal, 3 = high).</summary>
    public int Level { get; set; }

    public string LotNumber { get; set; } = string.Empty;
    public DateOnly ExpiryDate { get; set; }
    public decimal TargetMean { get; set; }
    public decimal TargetSd { get; set; }
    public string? Unit { get; set; }

    /// <summary>Analyser the lot is run on (its QC history is kept separate from other analysers).</summary>
    public string? InstrumentCode { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsExpiredOn(DateOnly date) => ExpiryDate < date;

    /// <summary>Standard deviations from the target mean, rounded to 3 decimals.</summary>
    public decimal ZScore(decimal value) =>
        TargetSd <= 0 ? 0 : Math.Round((value - TargetMean) / TargetSd, 3, MidpointRounding.AwayFromZero);
}

/// <summary>One control measurement, with the Westgard rules it violated and its review (LIS-06).</summary>
public sealed class QcRun : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid QcLotId { get; set; }
    public Guid LabTestId { get; set; }
    public string? InstrumentCode { get; set; }
    public decimal Value { get; set; }
    public decimal ZScore { get; set; }
    public DateTimeOffset RunAt { get; set; }
    public QcOutcome Outcome { get; set; }

    /// <summary>Violated rules in Westgard notation ("1-3s", "2-2s", …).</summary>
    public List<string> Violations { get; set; } = [];

    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
    public string? CorrectiveAction { get; set; }

    /// <summary>A rejected run must record the corrective action taken (recalibration, new reagent, repeat).</summary>
    public Result Review(string? note, string? correctiveAction, Guid? by, DateTimeOffset at)
    {
        if (Outcome == QcOutcome.Rejected && string.IsNullOrWhiteSpace(correctiveAction))
        {
            return Error.Field("correctiveAction", "Record the corrective action taken for a rejected QC run.");
        }

        ReviewNote = note?.Trim();
        CorrectiveAction = correctiveAction?.Trim();
        ReviewedBy = by;
        ReviewedAt = at;
        return Result.Success();
    }
}

/// <summary>
/// Supervisor override of a QC lockout (LIS-06). It is bound to the specific rejected run, so a later rejected run
/// locks the assay again, and it is always written to the audit trail with its reason.
/// </summary>
public sealed class QcOverride : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid LabTestId { get; set; }
    public Guid QcRunId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset OverriddenAt { get; set; }
    public Guid? OverriddenBy { get; set; }
}
