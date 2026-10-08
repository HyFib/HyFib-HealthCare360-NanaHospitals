namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;

public enum CardiologyStudyType
{
    Ecg = 0,
    Echo,
    Tmt,
    Holter,
    Abpm,
    Other,
}

public enum CardiologyStudyStatus
{
    Ordered = 0,
    Scheduled,
    InProgress,
    Acquired,
    Reported,
    Verified,
    Released,
    Cancelled,
}

/// <summary>
/// A cardiology investigation (CAR-01..04): ECG, 2D echo, TMT, Holter or ABPM. The technician performs it and attaches
/// the device output (PDF tracing, DICOM echo loops or SCP-ECG), stored encrypted through the platform file store and
/// referenced by id. The cardiologist then writes a structured report and signs it off.
/// </summary>
public sealed class CardiologyStudy : AuditableEntity, IFacilityScoped
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
    public CardiologyStudyType StudyType { get; set; }
    public OrderPriority Priority { get; set; }
    public Guid? OrderingPractitionerId { get; set; }
    public string? ClinicalNotes { get; set; }
    public DateTimeOffset OrderedAt { get; set; }
    public CardiologyStudyStatus Status { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public string? Room { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? AcquiredAt { get; set; }
    public Guid? TechnicianUserId { get; set; }

    /// <summary>ECG machine / echo system / TMT or Holter recorder used.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Device output files in the encrypted platform file store.</summary>
    public List<Guid> DeviceFileIds { get; set; } = [];

    /// <summary>
    /// The study UID its DICOM files are filed under in the PACS (INT-PACS), from the study's id
    /// (<see cref="Imaging.DicomConventions.UidFrom"/>); null until they are first sent.
    /// </summary>
    public string? StudyInstanceUid { get; set; }

    public string? TechnicianNotes { get; set; }
    public DateTimeOffset? ReportedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public Guid? ChargeId { get; set; }
    public string? ChargeError { get; set; }

    public bool IsPackageComponent => PackageBookingId is not null;

    public Result Schedule(DateTimeOffset at, string? room)
    {
        if (Status is not (CardiologyStudyStatus.Ordered or CardiologyStudyStatus.Scheduled))
        {
            return Error.Rule("cardiology.not_schedulable", "The study has already started and cannot be rescheduled.");
        }

        Status = CardiologyStudyStatus.Scheduled;
        ScheduledAt = at;
        Room = string.IsNullOrWhiteSpace(room) ? Room : room.Trim();
        return Result.Success();
    }

    public Result Start(Guid? technicianUserId, string? deviceName, DateTimeOffset now)
    {
        if (Status is not (CardiologyStudyStatus.Ordered or CardiologyStudyStatus.Scheduled))
        {
            return Error.Rule("cardiology.not_startable", "The study has already started.");
        }

        Status = CardiologyStudyStatus.InProgress;
        StartedAt = now;
        TechnicianUserId = technicianUserId ?? TechnicianUserId;
        DeviceName = string.IsNullOrWhiteSpace(deviceName) ? DeviceName : deviceName.Trim();
        return Result.Success();
    }

    /// <summary>Attaches device output. Files may be added until the report is signed (e.g. a repeat ECG strip).</summary>
    public Result AttachFiles(IEnumerable<Guid> fileIds)
    {
        if (Status is CardiologyStudyStatus.Verified or CardiologyStudyStatus.Released or CardiologyStudyStatus.Cancelled)
        {
            return Error.Rule("cardiology.closed", "Files cannot be added after the report is signed.");
        }

        foreach (var id in fileIds.Where(id => !DeviceFileIds.Contains(id)))
        {
            DeviceFileIds.Add(id);
        }

        return Result.Success();
    }

    /// <summary>Acquisition finished. Starting is implied for walk-in ECGs recorded at once.</summary>
    public Result Complete(Guid? technicianUserId, string? notes, DateTimeOffset now)
    {
        if (Status is not (CardiologyStudyStatus.Ordered or CardiologyStudyStatus.Scheduled or CardiologyStudyStatus.InProgress))
        {
            return Error.Rule("cardiology.not_in_progress", "The study is not in progress.");
        }

        if (DeviceFileIds.Count == 0)
        {
            return Error.Rule("cardiology.no_tracing", "Attach the device output (tracing, loops or report) before completing the study.");
        }

        Status = CardiologyStudyStatus.Acquired;
        StartedAt ??= now;
        AcquiredAt = now;
        TechnicianUserId = technicianUserId ?? TechnicianUserId;
        TechnicianNotes = string.IsNullOrWhiteSpace(notes) ? TechnicianNotes : notes.Trim();
        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status is CardiologyStudyStatus.Reported or CardiologyStudyStatus.Verified or CardiologyStudyStatus.Released)
        {
            return Error.Rule("cardiology.reported", "A reported study cannot be cancelled.");
        }

        if (Status == CardiologyStudyStatus.Cancelled)
        {
            return Result.Success();
        }

        Status = CardiologyStudyStatus.Cancelled;
        CancelReason = reason;
        CancelledAt = now;
        return Result.Success();
    }

    public Result EnsureReportable() =>
        Status is CardiologyStudyStatus.Acquired or CardiologyStudyStatus.Reported
            ? Result.Success()
            : Error.Rule("cardiology.not_reportable", "The study can be reported after the device output is attached and the study is completed.");

    public void MarkReported(DateTimeOffset now)
    {
        if (Status == CardiologyStudyStatus.Acquired)
        {
            Status = CardiologyStudyStatus.Reported;
            ReportedAt = now;
        }
    }

    public void MarkVerified(DateTimeOffset now)
    {
        if (Status is CardiologyStudyStatus.Acquired or CardiologyStudyStatus.Reported)
        {
            Status = CardiologyStudyStatus.Verified;
            ReportedAt ??= now;
            VerifiedAt = now;
        }
    }

    public void MarkReleased(DateTimeOffset now)
    {
        if (Status == CardiologyStudyStatus.Verified)
        {
            Status = CardiologyStudyStatus.Released;
            ReleasedAt = now;
        }
    }
}

/// <summary>Infers the study type from a cardiology service code or name (pure; unit-tested).</summary>
internal static class CardiologyStudyTypes
{
    public static CardiologyStudyType Resolve(string code, string name)
    {
        var text = $"{code} {name}".ToUpperInvariant();
        if (text.Contains("HOLTER", StringComparison.Ordinal))
        {
            return CardiologyStudyType.Holter;
        }

        if (text.Contains("ABPM", StringComparison.Ordinal) || text.Contains("AMBULATORY BP", StringComparison.Ordinal)
            || text.Contains("AMBULATORY BLOOD PRESSURE", StringComparison.Ordinal))
        {
            return CardiologyStudyType.Abpm;
        }

        if (text.Contains("TMT", StringComparison.Ordinal) || text.Contains("TREADMILL", StringComparison.Ordinal)
            || text.Contains("STRESS TEST", StringComparison.Ordinal))
        {
            return CardiologyStudyType.Tmt;
        }

        if (text.Contains("ECHO", StringComparison.Ordinal))
        {
            return CardiologyStudyType.Echo;
        }

        if (text.Contains("ECG", StringComparison.Ordinal) || text.Contains("EKG", StringComparison.Ordinal)
            || text.Contains("ELECTROCARDIO", StringComparison.Ordinal))
        {
            return CardiologyStudyType.Ecg;
        }

        return CardiologyStudyType.Other;
    }
}
