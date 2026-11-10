namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

/// <summary>
/// The patient-facing report of one accession (LIS-02, LIS-07). Tests are released as soon as they are verified, so a
/// CBC is not held back by a 72-hour culture. The report is "Released" once anything is released, and
/// <see cref="IsPartial"/> says whether more tests will follow. Each released test raises DiagnosticReportReleased for
/// the EHR, and a correction after release is recorded as a numbered amendment with its reason.
/// </summary>
public sealed class LabReport : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid LabOrderId { get; set; }
    public Guid PatientId { get; set; }

    /// <summary>Printed report number (the accession number).</summary>
    public string ReportNumber { get; set; } = string.Empty;

    public LabReportStatus Status { get; set; }
    public bool IsPartial { get; set; }
    public DateTimeOffset? FirstReleasedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public Guid? ReleasedBy { get; set; }

    /// <summary>QR verification code, assigned at first release.</summary>
    public string? VerificationCode { get; set; }

    /// <summary>Amendment number (0 = original report).</summary>
    public int Revision { get; set; }

    public List<LabReportAmendment> Amendments { get; set; } = [];

    /// <summary>Derives the report status from its items.</summary>
    public void Refresh(IReadOnlyCollection<LabOrderItem> items)
    {
        var active = items.Where(i => i.IsActive).ToList();
        var released = active.Count(i => i.Status == LabOrderItemStatus.Released);
        IsPartial = released > 0 && released < active.Count;

        if (released > 0)
        {
            Status = Revision > 0 ? LabReportStatus.Amended : LabReportStatus.Released;
        }
        else if (active.Count > 0 && active.All(i => i.Status == LabOrderItemStatus.Verified))
        {
            Status = LabReportStatus.Verified;
        }
        else
        {
            Status = LabReportStatus.Draft;
        }
    }

    /// <summary>
    /// Releases every verified test not yet released and raises one DiagnosticReportReleased per test, so the
    /// Clinical module can mark each ordered item as resulted.
    /// </summary>
    public Result<IReadOnlyList<LabOrderItem>> Release(
        LabOrder order, IReadOnlySet<Guid> criticalItemIds, Guid? by, DateTimeOffset at)
    {
        var releasable = order.Items.Where(i => i.Status == LabOrderItemStatus.Verified).ToList();
        if (releasable.Count == 0)
        {
            return Error.Rule("lab.nothing_to_release", "No verified tests are waiting for release on this report.");
        }

        foreach (var item in releasable)
        {
            item.MarkReleased(by, at);
            Raise(new DiagnosticReportReleased(
                Id, DiagnosticKind.Laboratory, PatientId, order.VisitId, item.SourceOrderItemId, item.TestName, criticalItemIds.Contains(item.Id), at));
        }

        VerificationCode ??= Domain.VerificationCode.New();
        FirstReleasedAt ??= at;
        ReleasedAt = at;
        ReleasedBy = by;
        Refresh(order.Items);
        order.RefreshStatus(at);
        return releasable;
    }

    /// <summary>
    /// Records a post-release correction. The corrected tests are re-announced to the EHR marked "(amended)"; the
    /// amendment reason and the old → new values are printed on every later copy of the report. A correction one
    /// pathologist asked for and another approved names both: <paramref name="by"/> approved it, and
    /// <paramref name="requestedBy"/> asked for it.
    /// </summary>
    public Result Amend(
        LabOrder order, string reason, string changeSummary, IReadOnlyCollection<LabOrderItem> affectedItems, bool hasCritical, Guid? by, DateTimeOffset at,
        (Guid Id, string Name)? requestedBy = null)
    {
        if (Status is not (LabReportStatus.Released or LabReportStatus.Amended))
        {
            return Error.Rule("lab.report_not_released", "Only a released report can be amended. Correct unreleased results through result entry.");
        }

        Revision++;
        Amendments.Add(new LabReportAmendment
        {
            LabReportId = Id,
            Revision = Revision,
            Reason = reason,
            Changes = changeSummary.Length > 2000 ? changeSummary[..2000] : changeSummary,
            AmendedAt = at,
            AmendedBy = by,
            RequestedBy = requestedBy?.Id,
            RequestedByName = requestedBy?.Name,
        });

        foreach (var item in affectedItems)
        {
            Raise(new DiagnosticReportReleased(
                Id, DiagnosticKind.Laboratory, PatientId, order.VisitId, item.SourceOrderItemId, $"{item.TestName} (amended)", hasCritical, at));
        }

        ReleasedAt = at;
        ReleasedBy = by;
        Refresh(order.Items);
        return Result.Success();
    }
}

/// <summary>One post-release correction of a lab report (reason and old → new values), printed on the report.</summary>
public sealed class LabReportAmendment : AuditableEntity
{
    public Guid LabReportId { get; set; }
    public int Revision { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Changes { get; set; } = string.Empty;
    public DateTimeOffset AmendedAt { get; set; }

    /// <summary>Who released the correction: the pathologist who made it, or the second pathologist who approved it.</summary>
    public Guid? AmendedBy { get; set; }

    /// <summary>
    /// The pathologist who asked for the correction, where the lab requires a second person to verify
    /// (<c>lab.separateVerifier</c>); null when the pathologist who made it released it.
    /// </summary>
    public Guid? RequestedBy { get; set; }

    public string? RequestedByName { get; set; }
}
