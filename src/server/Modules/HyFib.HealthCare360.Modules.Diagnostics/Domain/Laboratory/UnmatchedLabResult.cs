namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

/// <summary>Why a result an analyser sent could not go on a sample's test.</summary>
public enum UnmatchedResultReason
{
    /// <summary>The analyser named no sample, or its sample id could not be read.</summary>
    NoSampleId = 0,

    /// <summary>No tube of the analyser's facility has the sample id the analyser sent.</summary>
    UnknownSample,

    /// <summary>The tube is not in the lab: not collected yet, rejected or cancelled.</summary>
    SampleNotInLab,

    /// <summary>None of the tube's tests maps any channel the analyser sent for it.</summary>
    NoMatchingTest,

    /// <summary>
    /// The test already has a verified result, and the analyser sent another value, or the same value with another unit,
    /// flags, status or comment (a correction, say), or for a result typed in or from another analyser.
    /// </summary>
    DiffersFromVerified,

    /// <summary>The value is not one the test can hold (not a number for a numeric test, not one of its answers).</summary>
    InvalidValue,

    /// <summary>
    /// The analyser sent the test twice in one message, the second time with another value or the same value read
    /// otherwise; the first is on the test.
    /// </summary>
    RepeatedInMessage,
}

public enum UnmatchedResultStatus
{
    /// <summary>Waiting for a lab user to assign or discard it.</summary>
    Open = 0,

    /// <summary>Put on a sample's test by a lab user: it is then an entered result a person verifies.</summary>
    Assigned,

    /// <summary>Set aside by a lab user, with the reason.</summary>
    Discarded,
}

/// <summary>
/// A result an analyser sent that HC360 could not put on an open test (INT-LAB, LIS-04), kept with everything the
/// analyser said about it. The analyser's message is acknowledged so it moves on, instead of sending the message for
/// ever, and the result is never acknowledged and lost: it waits here until a lab user assigns it to the right sample's
/// test, where it is entered as a typed-in result is and never auto-validated, or discards it with a reason. Both are
/// audited.
/// </summary>
public sealed class UnmatchedLabResult : AuditableEntity, IFacilityScoped
{
    public const int SampleBarcodeLength = 64;
    public const int ChannelCodeLength = 64;
    public const int DiscardReasonLength = 300;

    /// <summary>The analyser's facility, whose lab resolves it.</summary>
    public Guid FacilityId { get; set; }

    public string InstrumentCode { get; set; } = string.Empty;

    /// <summary>The sample id as the analyser sent it; null when it sent none.</summary>
    public string? SampleBarcode { get; set; }

    /// <summary>The tube with that barcode in the analyser's facility, when there is one.</summary>
    public Guid? SampleId { get; set; }

    public Guid? PatientId { get; set; }

    /// <summary>
    /// The tube's result the value was for, when the LIS found one: the verified result it differs from, the test whose
    /// value it cannot be, the test the message gave twice.
    /// </summary>
    public Guid? LabResultId { get; set; }

    public string ChannelCode { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string? InstrumentUnit { get; set; }
    public string? InstrumentFlags { get; set; }
    public string? InstrumentStatus { get; set; }
    public string? InstrumentComment { get; set; }

    /// <summary>When the analyser measured it, if it said.</summary>
    public DateTimeOffset? ResultedAt { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
    public UnmatchedResultReason Reason { get; set; }

    /// <summary>What the LIS found, in words ("Every test on sample 000000123 that maps GLU is verified…").</summary>
    public string Detail { get; set; } = string.Empty;

    public UnmatchedResultStatus Status { get; set; }

    /// <summary>The result it was put on.</summary>
    public Guid? AssignedResultId { get; set; }

    public string? DiscardReason { get; set; }
    public Guid? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    public Result Assign(Guid labResultId, Guid? by, DateTimeOffset at)
    {
        if (Status != UnmatchedResultStatus.Open)
        {
            return Resolved();
        }

        Status = UnmatchedResultStatus.Assigned;
        AssignedResultId = labResultId;
        ResolvedBy = by;
        ResolvedAt = at;
        return Result.Success();
    }

    public Result Discard(string reason, Guid? by, DateTimeOffset at)
    {
        if (Status != UnmatchedResultStatus.Open)
        {
            return Resolved();
        }

        Status = UnmatchedResultStatus.Discarded;
        DiscardReason = reason.Trim();
        ResolvedBy = by;
        ResolvedAt = at;
        return Result.Success();
    }

    private Error Resolved() => Error.Rule("lab.unmatched_resolved",
        $"The {ChannelCode} result from {InstrumentCode} was already {(Status == UnmatchedResultStatus.Assigned ? "assigned" : "discarded")}.");
}
