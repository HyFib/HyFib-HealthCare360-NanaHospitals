namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

/// <summary>Technique details captured by the radiographer when acquisition completes.</summary>
public sealed record AcquisitionDetails(bool ContrastUsed, string? ContrastAgent, decimal? ContrastVolumeMl, string? DoseNote, string? Notes);

/// <summary>
/// A radiology examination (IMG-01..04). The accession number ("NHRD260000042") and a pre-assigned Study Instance UID
/// go to the modality through the DICOM worklist, so the images stored in PACS reconcile with the order by accession
/// (IMG-02). Milestones (ordered, arrived, acquired, reported, verified) are kept for TAT reporting, and repeats with
/// their reasons are kept for reject analysis.
/// </summary>
public sealed class ImagingStudy : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string AccessionNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }
    public PatientClass PatientClass { get; set; }
    public Guid? SourceOrderId { get; set; }

    /// <summary>Clinical order item (unique; makes ServiceOrderPlaced handling idempotent).</summary>
    public Guid? SourceOrderItemId { get; set; }

    public string? SourceOrderNumber { get; set; }

    /// <summary>Health-checkup package booking; the package is charged once by Billing, never per study.</summary>
    public Guid? PackageBookingId { get; set; }

    /// <summary>Units ordered (charged as the quantity); 1 for ordinary studies.</summary>
    public int Quantity { get; set; } = 1;

    public Guid? ServiceItemId { get; set; }
    public string ServiceCode { get; set; } = string.Empty;
    public string StudyName { get; set; } = string.Empty;
    public Modality Modality { get; set; }
    public string? BodyPart { get; set; }
    public OrderPriority Priority { get; set; }
    public Guid? OrderingPractitionerId { get; set; }
    public string? ReferringDoctorName { get; set; }
    public string? ClinicalNotes { get; set; }

    /// <summary>
    /// An ultrasound in pregnancy (from its procedure when ordered): it is not started until the woman's Form F declaration
    /// is recorded, and its report is not signed until the Form F is completed (PCPNDT Act; <see cref="FormF"/>).
    /// </summary>
    public bool RequiresFormF { get; set; }

    public DateTimeOffset OrderedAt { get; set; }
    public ImagingStudyStatus Status { get; set; }

    public DateTimeOffset? ScheduledAt { get; set; }
    public string? Room { get; set; }

    /// <summary>DICOM AE title of the scheduled modality (MWL "Scheduled Station AE Title").</summary>
    public string? StationAeTitle { get; set; }

    public DateTimeOffset? ArrivedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? AcquiredAt { get; set; }
    public Guid? TechnicianUserId { get; set; }
    public bool ContrastUsed { get; set; }
    public string? ContrastAgent { get; set; }
    public decimal? ContrastVolumeMl { get; set; }

    /// <summary>Radiation dose note (CTDIvol/DLP for CT, DAP for fluoroscopy) as displayed by the modality.</summary>
    public string? DoseNote { get; set; }

    public string? TechnicianNotes { get; set; }

    /// <summary>Pre-assigned at ordering (sent in the worklist); replaced by the PACS value on reconciliation.</summary>
    public string StudyInstanceUid { get; set; } = string.Empty;

    public DateTimeOffset? PacsLinkedAt { get; set; }
    public int? SeriesCount { get; set; }
    public int? InstanceCount { get; set; }
    public DateTimeOffset? ReportedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public Guid? ChargeId { get; set; }
    public string? ChargeError { get; set; }

    public List<ImagingRepeat> Repeats { get; set; } = [];

    public bool IsClosed => Status is ImagingStudyStatus.Released or ImagingStudyStatus.Cancelled;

    public bool IsPackageComponent => PackageBookingId is not null;

    public Result Schedule(DateTimeOffset at, string? room, string? stationAeTitle)
    {
        if (Status is not (ImagingStudyStatus.Ordered or ImagingStudyStatus.Scheduled))
        {
            return Error.Rule("imaging.not_schedulable", $"The study is already {Describe(Status)} and cannot be rescheduled.");
        }

        Status = ImagingStudyStatus.Scheduled;
        ScheduledAt = at;
        Room = Clean(room) ?? Room;
        StationAeTitle = Clean(stationAeTitle)?.ToUpperInvariant() ?? StationAeTitle;
        return Result.Success();
    }

    public Result Arrive(DateTimeOffset now)
    {
        if (Status is not (ImagingStudyStatus.Ordered or ImagingStudyStatus.Scheduled))
        {
            return Error.Rule("imaging.not_waiting", $"The study is already {Describe(Status)}.");
        }

        Status = ImagingStudyStatus.Arrived;
        ArrivedAt = now;
        return Result.Success();
    }

    /// <summary>Patient on the table. Portable/ER studies may start without an arrival step.</summary>
    public Result Start(Guid? technicianUserId, DateTimeOffset now)
    {
        if (Status is not (ImagingStudyStatus.Ordered or ImagingStudyStatus.Scheduled or ImagingStudyStatus.Arrived))
        {
            return Error.Rule("imaging.not_startable", $"The study is already {Describe(Status)}.");
        }

        Status = ImagingStudyStatus.InProgress;
        ArrivedAt ??= now;
        StartedAt = now;
        TechnicianUserId = technicianUserId ?? TechnicianUserId;
        return Result.Success();
    }

    public Result Acquire(AcquisitionDetails details, Guid? technicianUserId, DateTimeOffset now)
    {
        if (Status is not (ImagingStudyStatus.Arrived or ImagingStudyStatus.InProgress))
        {
            return Error.Rule("imaging.not_in_progress", $"The study is {Describe(Status)}; start it before completing acquisition.");
        }

        if (details.ContrastUsed && string.IsNullOrWhiteSpace(details.ContrastAgent))
        {
            return Error.Field("contrastAgent", "Enter the contrast agent used.");
        }

        Status = ImagingStudyStatus.Acquired;
        ArrivedAt ??= now;
        StartedAt ??= now;
        AcquiredAt = now;
        TechnicianUserId = technicianUserId ?? TechnicianUserId;
        ContrastUsed = details.ContrastUsed;
        ContrastAgent = details.ContrastUsed ? Clean(details.ContrastAgent) : null;
        ContrastVolumeMl = details.ContrastUsed ? details.ContrastVolumeMl : null;
        DoseNote = Clean(details.DoseNote);
        TechnicianNotes = Clean(details.Notes) ?? TechnicianNotes;
        return Result.Success();
    }

    public Result RecordRepeat(ImageRepeatReason reason, string? note, DateTimeOffset now)
    {
        if (Status is not (ImagingStudyStatus.InProgress or ImagingStudyStatus.Acquired))
        {
            return Error.Rule("imaging.repeat_state", "Repeats are recorded while the study is being acquired.");
        }

        if (reason == ImageRepeatReason.Other && string.IsNullOrWhiteSpace(note))
        {
            return Error.Field("note", "Describe why the image was repeated.");
        }

        Repeats.Add(new ImagingRepeat { ImagingStudyId = Id, Reason = reason, Note = Clean(note), RecordedAt = now });
        return Result.Success();
    }

    /// <summary>
    /// Links the study to the images found in PACS by accession number (IMG-02). Images in PACS prove that acquisition
    /// happened, so a study the radiographer forgot to complete moves to Acquired.
    /// </summary>
    public Result LinkPacs(string studyInstanceUid, int? seriesCount, int? instanceCount, DateTimeOffset now)
    {
        if (Status == ImagingStudyStatus.Cancelled)
        {
            return Error.Rule("imaging.cancelled", "The study was cancelled.");
        }

        StudyInstanceUid = studyInstanceUid;
        SeriesCount = seriesCount;
        InstanceCount = instanceCount;
        PacsLinkedAt = now;
        if (Status is ImagingStudyStatus.Ordered or ImagingStudyStatus.Scheduled or ImagingStudyStatus.Arrived or ImagingStudyStatus.InProgress)
        {
            Status = ImagingStudyStatus.Acquired;
            ArrivedAt ??= now;
            StartedAt ??= now;
            AcquiredAt = now;
        }

        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status is ImagingStudyStatus.Reported or ImagingStudyStatus.Verified or ImagingStudyStatus.Released)
        {
            return Error.Rule("imaging.reported", "A reported study cannot be cancelled; add an addendum to the report instead.");
        }

        if (Status == ImagingStudyStatus.Cancelled)
        {
            return Result.Success();
        }

        Status = ImagingStudyStatus.Cancelled;
        CancelReason = reason;
        CancelledAt = now;
        return Result.Success();
    }

    /// <summary>A report can be written once images exist (acquired) and until the study is released.</summary>
    public Result EnsureReportable()
    {
        return Status is ImagingStudyStatus.Acquired or ImagingStudyStatus.Reported or ImagingStudyStatus.Verified
            ? Result.Success()
            : Error.Rule("imaging.not_reportable", $"The study is {Describe(Status)}; it can be reported after acquisition.");
    }

    public void MarkReported(DateTimeOffset now)
    {
        if (Status == ImagingStudyStatus.Acquired)
        {
            Status = ImagingStudyStatus.Reported;
            ReportedAt = now;
        }
    }

    public void MarkVerified(DateTimeOffset now)
    {
        if (Status is ImagingStudyStatus.Acquired or ImagingStudyStatus.Reported)
        {
            Status = ImagingStudyStatus.Verified;
            ReportedAt ??= now;
            VerifiedAt = now;
        }
    }

    public void MarkReleased(DateTimeOffset now)
    {
        if (Status == ImagingStudyStatus.Verified)
        {
            Status = ImagingStudyStatus.Released;
            ReleasedAt = now;
        }
    }

    internal static string Describe(ImagingStudyStatus status) => status switch
    {
        ImagingStudyStatus.InProgress => "in progress",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One repeated/rejected image with its reason (IMG-04).</summary>
public sealed class ImagingRepeat : AuditableEntity
{
    public Guid ImagingStudyId { get; set; }
    public ImageRepeatReason Reason { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}
