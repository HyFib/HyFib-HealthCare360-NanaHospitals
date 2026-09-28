namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

/// <summary>
/// A laboratory accession (LIS-02): the tests ordered together for one patient. It carries one accession number
/// ("NHLB260000123"), however many tubes are drawn. Orders come from a clinician's order (all items of one clinical
/// order share one accession) or are registered at the lab counter for walk-in and outside-referral patients.
/// </summary>
public sealed class LabOrder : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string AccessionNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }
    public PatientClass PatientClass { get; set; }
    public LabOrderSource Source { get; set; }

    /// <summary>Clinical order this accession was created from (null for walk-in orders).</summary>
    public Guid? SourceOrderId { get; set; }

    public string? SourceOrderNumber { get; set; }

    /// <summary>Highest priority among the items (a STAT test makes the whole accession STAT at the counter).</summary>
    public OrderPriority Priority { get; set; }

    public Guid? OrderingPractitionerId { get; set; }

    /// <summary>Outside referring doctor for walk-in / referral patients (printed on the report).</summary>
    public string? ReferringDoctorName { get; set; }

    public string? ClinicalNotes { get; set; }
    public DateTimeOffset OrderedAt { get; set; }
    public LabOrderStatus Status { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public List<LabOrderItem> Items { get; set; } = [];

    /// <summary>New items can join only while nothing has been released or cancelled on the accession.</summary>
    public bool AcceptsNewItems => Status is LabOrderStatus.Ordered or LabOrderStatus.InProgress;

    public void AddItem(LabOrderItem item)
    {
        item.LabOrderId = Id;
        item.FacilityId = FacilityId;
        Items.Add(item);
        if (item.Priority > Priority)
        {
            Priority = item.Priority;
        }
    }

    /// <summary>Derives the accession status from its items (cancelled, completed when all active items are released).</summary>
    public void RefreshStatus(DateTimeOffset now)
    {
        var active = Items.Where(i => i.Status != LabOrderItemStatus.Cancelled).ToList();
        if (active.Count == 0)
        {
            Status = Items.Count == 0 ? LabOrderStatus.Ordered : LabOrderStatus.Cancelled;
            return;
        }

        if (active.All(i => i.Status == LabOrderItemStatus.Released))
        {
            Status = LabOrderStatus.Completed;
            CompletedAt ??= now;
            return;
        }

        CompletedAt = null;
        Status = active.Any(i => i.Status != LabOrderItemStatus.Ordered) ? LabOrderStatus.InProgress : LabOrderStatus.Ordered;
    }
}

/// <summary>
/// One ordered test (single analyte or panel) within an accession. It tracks the milestones used for TAT, the billing
/// charge posted for it, and outsourcing to a reference laboratory (LIS-08).
/// </summary>
public sealed class LabOrderItem : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid LabOrderId { get; set; }
    public Guid LabTestId { get; set; }

    /// <summary>Test code and name at the time of ordering (masters may be renamed later).</summary>
    public string TestCode { get; set; } = string.Empty;

    public string TestName { get; set; } = string.Empty;
    public LabSection Section { get; set; }
    public Guid? SampleId { get; set; }

    /// <summary>Clinical order item (ServiceOrderPlaced.OrderItemId); unique, which makes event handling idempotent.</summary>
    public Guid? SourceOrderItemId { get; set; }

    public Guid? ServiceItemId { get; set; }
    public OrderPriority Priority { get; set; }
    public LabOrderItemStatus Status { get; set; }

    /// <summary>
    /// Health-checkup package booking the test belongs to. Package tests are processed like any other work, but Billing
    /// charges the package once, so no per-test charge is posted and the per-test payment gate does not apply.
    /// </summary>
    public Guid? PackageBookingId { get; set; }

    /// <summary>Units ordered (charged as the quantity); 1 for ordinary tests.</summary>
    public int Quantity { get; set; } = 1;

    /// <summary>Charge id returned by billing; null with <see cref="ChargeError"/> set when posting failed (retryable).</summary>
    public Guid? ChargeId { get; set; }

    public string? ChargeError { get; set; }

    public Guid? ReferralLabId { get; set; }
    public DateTimeOffset? SentOutAt { get; set; }
    public Guid? SentOutBy { get; set; }

    /// <summary>Courier docket / manifest number for the outsourced sample.</summary>
    public string? DispatchReference { get; set; }

    public DateTimeOffset? ExternalReportReceivedAt { get; set; }
    public string? ExternalReference { get; set; }
    public Guid? ExternalReportFileId { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public Guid? ReleasedBy { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    public bool IsActive => Status != LabOrderItemStatus.Cancelled;

    public bool IsPackageComponent => PackageBookingId is not null;

    /// <summary>Verified or released results cannot disappear through cancellation; the report is amended instead.</summary>
    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status == LabOrderItemStatus.Cancelled)
        {
            return Result.Success();
        }

        if (Status is LabOrderItemStatus.Verified or LabOrderItemStatus.Released)
        {
            return Error.Rule("lab.item_completed", $"{TestName} is already verified. Amend the report instead of cancelling the test.");
        }

        Status = LabOrderItemStatus.Cancelled;
        CancelReason = reason;
        CancelledAt = now;
        return Result.Success();
    }

    public void MarkCollected()
    {
        if (Status == LabOrderItemStatus.Ordered)
        {
            Status = LabOrderItemStatus.Collected;
        }
    }

    public void MarkInLab()
    {
        if (Status is LabOrderItemStatus.Ordered or LabOrderItemStatus.Collected)
        {
            Status = LabOrderItemStatus.InLab;
        }
    }

    /// <summary>A rejected tube sends the test back to "awaiting collection" on the recollection tube.</summary>
    public void AwaitRecollection(Guid? newSampleId)
    {
        if (!IsActive || Status is LabOrderItemStatus.Verified or LabOrderItemStatus.Released)
        {
            return;
        }

        Status = LabOrderItemStatus.Ordered;
        SampleId = newSampleId;
    }

    /// <summary>Recomputes the result-driven status after entry, verification or reopening of results.</summary>
    public void RefreshFromResults(IReadOnlyCollection<LabResult> results, DateTimeOffset now)
    {
        if (!IsActive || Status == LabOrderItemStatus.Released || results.Count == 0)
        {
            return;
        }

        if (results.All(r => r.Status == LabResultStatus.Verified))
        {
            Status = LabOrderItemStatus.Verified;
            VerifiedAt ??= now;
            return;
        }

        VerifiedAt = null;
        Status = results.All(r => r.Status != LabResultStatus.Pending) ? LabOrderItemStatus.ResultsEntered : LabOrderItemStatus.InLab;
    }

    public Result MarkReleased(Guid? by, DateTimeOffset at)
    {
        if (Status != LabOrderItemStatus.Verified)
        {
            return Error.Rule("lab.item_not_verified", $"{TestName} is not verified yet.");
        }

        Status = LabOrderItemStatus.Released;
        ReleasedAt = at;
        ReleasedBy = by;
        return Result.Success();
    }

    public Result SendOut(Guid referralLabId, string? dispatchReference, Guid? by, DateTimeOffset at)
    {
        if (Status is not (LabOrderItemStatus.Collected or LabOrderItemStatus.InLab))
        {
            return Error.Rule("lab.outsource_state", $"{TestName} can be sent out only after the sample is collected and before results are entered.");
        }

        if (SentOutAt is not null)
        {
            return Error.Rule("lab.outsource_sent", $"{TestName} was already sent to the reference laboratory.");
        }

        ReferralLabId = referralLabId;
        DispatchReference = dispatchReference?.Trim();
        SentOutAt = at;
        SentOutBy = by;
        return Result.Success();
    }

    public Result ReceiveExternalReport(string? externalReference, Guid? fileId, DateTimeOffset at)
    {
        if (SentOutAt is null)
        {
            return Error.Rule("lab.outsource_not_sent", $"{TestName} has not been sent to a reference laboratory.");
        }

        if (!IsActive)
        {
            return Error.Rule("lab.item_cancelled", $"{TestName} was cancelled.");
        }

        ExternalReportReceivedAt = at;
        ExternalReference = externalReference?.Trim();
        ExternalReportFileId = fileId ?? ExternalReportFileId;
        return Result.Success();
    }
}
