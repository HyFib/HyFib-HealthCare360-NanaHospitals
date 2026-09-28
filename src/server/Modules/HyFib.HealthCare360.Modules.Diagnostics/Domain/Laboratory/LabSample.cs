namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

/// <summary>
/// One barcode-labelled tube or container (LIS-02, LIS-03). Tests that share a container, specimen, section and
/// destination (in-house or a reference lab) share a tube. The state machine is
/// Pending → Collected → Received → InProcess → Completed. A tube can be Rejected at any point after collection, and a
/// Pending tube is re-created for recollection. Every transition records who did it and when (pre-analytical TAT).
/// </summary>
public sealed class LabSample : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid LabOrderId { get; set; }
    public Guid PatientId { get; set; }

    /// <summary>Numeric barcode printed on the label and scanned by analysers (unique across facilities).</summary>
    public string Barcode { get; set; } = string.Empty;

    public SpecimenType SpecimenType { get; set; }
    public ContainerType Container { get; set; }
    public LabSection Section { get; set; }

    /// <summary>Reference laboratory the tube goes to (outsourced tests travel in their own tube).</summary>
    public Guid? ReferralLabId { get; set; }

    /// <summary>Set when the tube is reserved for one timed test (<see cref="LabTest.DrawSeparately"/>).</summary>
    public Guid? DedicatedTestId { get; set; }

    public OrderPriority Priority { get; set; }
    public SampleStatus Status { get; set; }
    public DateTimeOffset? CollectedAt { get; set; }
    public Guid? CollectedBy { get; set; }

    /// <summary>Collection point or site ("OP collection counter", "Ward 3B", "Left antecubital vein").</summary>
    public string? CollectionSite { get; set; }

    public DateTimeOffset? ReceivedAt { get; set; }
    public Guid? ReceivedBy { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public SampleRejectionReason? RejectionReason { get; set; }
    public string? RejectionRemarks { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public Guid? RejectedBy { get; set; }

    /// <summary>The rejected tube this one replaces.</summary>
    public Guid? RecollectionOfSampleId { get; set; }

    /// <summary>The replacement tube created after this one was rejected.</summary>
    public Guid? RecollectedAsSampleId { get; set; }

    public string? Notes { get; set; }

    public Result Collect(Guid? by, DateTimeOffset at, string? site)
    {
        if (Status != SampleStatus.Pending)
        {
            return Error.Rule("sample.not_pending", $"Sample {Barcode} is already {Status.ToString().ToLowerInvariant()}.");
        }

        Status = SampleStatus.Collected;
        CollectedAt = at;
        CollectedBy = by;
        CollectionSite = string.IsNullOrWhiteSpace(site) ? CollectionSite : site.Trim();
        return Result.Success();
    }

    /// <summary>Receipt in the lab. A tube must be collected first, so payment gating at collection cannot be bypassed.</summary>
    public Result Receive(Guid? by, DateTimeOffset at)
    {
        switch (Status)
        {
            case SampleStatus.Pending:
                return Error.Rule("sample.not_collected", $"Sample {Barcode} has not been collected yet.");
            case SampleStatus.Received or SampleStatus.InProcess or SampleStatus.Completed:
                return Error.Conflict("sample.already_received", $"Sample {Barcode} was already received.");
            case SampleStatus.Rejected or SampleStatus.Cancelled:
                return Error.Rule("sample.closed", $"Sample {Barcode} was {Status.ToString().ToLowerInvariant()}.");
        }

        if (CollectedAt is { } collected && at < collected)
        {
            return Error.Rule("sample.receipt_before_collection", "The receipt time cannot be earlier than the collection time.");
        }

        Status = SampleStatus.Received;
        ReceivedAt = at;
        ReceivedBy = by;
        return Result.Success();
    }

    /// <summary>First result recorded (manual or analyser).</summary>
    public Result StartProcessing()
    {
        if (Status == SampleStatus.InProcess)
        {
            return Result.Success();
        }

        if (Status != SampleStatus.Received)
        {
            return Error.Rule("sample.not_received", $"Sample {Barcode} must be received in the lab before results are recorded.");
        }

        Status = SampleStatus.InProcess;
        return Result.Success();
    }

    /// <summary>All results on the tube are verified.</summary>
    public Result Complete(DateTimeOffset at)
    {
        if (Status == SampleStatus.Completed)
        {
            return Result.Success();
        }

        if (Status is not (SampleStatus.Received or SampleStatus.InProcess))
        {
            return Error.Rule("sample.not_in_lab", $"Sample {Barcode} is not in the lab.");
        }

        Status = SampleStatus.Completed;
        CompletedAt = at;
        return Result.Success();
    }

    /// <summary>Returns a completed tube to processing when one of its verified results is reopened.</summary>
    public void Reopen()
    {
        if (Status == SampleStatus.Completed)
        {
            Status = SampleStatus.InProcess;
            CompletedAt = null;
        }
    }

    public Result Reject(SampleRejectionReason reason, string? remarks, Guid? by, DateTimeOffset at)
    {
        if (Status is not (SampleStatus.Collected or SampleStatus.Received or SampleStatus.InProcess))
        {
            return Error.Rule("sample.not_rejectable", $"Sample {Barcode} cannot be rejected while it is {Status.ToString().ToLowerInvariant()}.");
        }

        if (reason == SampleRejectionReason.Other && string.IsNullOrWhiteSpace(remarks))
        {
            return Error.Field("remarks", "Describe why the sample is rejected.");
        }

        Status = SampleStatus.Rejected;
        RejectionReason = reason;
        RejectionRemarks = remarks?.Trim();
        RejectedAt = at;
        RejectedBy = by;
        return Result.Success();
    }

    /// <summary>Every test on the tube was cancelled before processing finished.</summary>
    public Result Cancel()
    {
        if (Status is SampleStatus.Cancelled)
        {
            return Result.Success();
        }

        if (Status is SampleStatus.Completed or SampleStatus.Rejected)
        {
            return Error.Rule("sample.closed", $"Sample {Barcode} is already {Status.ToString().ToLowerInvariant()}.");
        }

        Status = SampleStatus.Cancelled;
        return Result.Success();
    }

    /// <summary>Creates the replacement tube for a rejected sample (same container and tests, new barcode).</summary>
    public Result<LabSample> CreateRecollection(string barcode)
    {
        if (Status != SampleStatus.Rejected)
        {
            return Error.Rule("sample.not_rejected", "Only a rejected sample can be recollected.");
        }

        if (RecollectedAsSampleId is not null)
        {
            return Error.Conflict("sample.recollection_exists", "A recollection was already requested for this sample.");
        }

        var replacement = new LabSample
        {
            FacilityId = FacilityId,
            LabOrderId = LabOrderId,
            PatientId = PatientId,
            Barcode = barcode,
            SpecimenType = SpecimenType,
            Container = Container,
            Section = Section,
            ReferralLabId = ReferralLabId,
            DedicatedTestId = DedicatedTestId,
            Priority = Priority,
            Status = SampleStatus.Pending,
            RecollectionOfSampleId = Id,
            Notes = RejectionReason is { } reason ? $"Recollection: previous sample rejected ({reason})." : null,
        };
        RecollectedAsSampleId = replacement.Id;
        return replacement;
    }
}
