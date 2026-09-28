namespace HyFib.HealthCare360.Modules.VirtualCare.Domain;

public enum RpmAlertKind
{
    /// <summary>A reading outside the patient's limits (RPM-03).</summary>
    Threshold = 0,

    /// <summary>An assigned device sent nothing for the configured number of hours (RPM-05).</summary>
    DeviceSilence,
}

public enum RpmAlertSeverity
{
    Warning = 0,
    Critical,
}

public enum RpmAlertStatus
{
    Open = 0,
    Acknowledged,
    Resolved,
}

/// <summary>
/// An alert for the care team with acknowledgement tracking (RPM-03). There is at most one active alert per enrolment
/// and reading type: repeated out-of-range readings are counted on it instead of paging the team again, but a warning
/// that turns critical is escalated and reopened so it is looked at again.
/// </summary>
public sealed class RpmAlert : AuditableEntity, IFacilityScoped
{
    public const int MaxNotesLength = 1000;

    public Guid FacilityId { get; set; }
    public Guid EnrollmentId { get; set; }
    public Guid PatientId { get; set; }
    public RpmAlertKind Kind { get; set; }
    public Guid? ReadingId { get; set; }
    public Guid? DeviceId { get; set; }
    public RpmReadingType? ReadingType { get; set; }
    public decimal? Value { get; set; }
    public string? Unit { get; set; }
    public RpmAlertSeverity Severity { get; set; }
    public RpmAlertStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset RaisedAt { get; set; }
    public DateTimeOffset LastOccurredAt { get; set; }
    public int Occurrences { get; set; } = 1;
    public Guid? AcknowledgedByUserId { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? Notes { get; set; }

    public bool IsActive => Status is RpmAlertStatus.Open or RpmAlertStatus.Acknowledged;

    public static RpmAlert ForReading(RpmEnrollment enrollment, RpmReading reading, DateTimeOffset now)
    {
        var severity = reading.Severity == ReadingSeverity.Critical ? RpmAlertSeverity.Critical : RpmAlertSeverity.Warning;
        return new RpmAlert
        {
            FacilityId = enrollment.FacilityId,
            EnrollmentId = enrollment.Id,
            PatientId = enrollment.PatientId,
            Kind = RpmAlertKind.Threshold,
            ReadingId = reading.Id,
            DeviceId = reading.DeviceId,
            ReadingType = reading.Type,
            Value = reading.Value,
            Unit = reading.Unit,
            Severity = severity,
            Status = RpmAlertStatus.Open,
            Message = Describe(reading, severity),
            RaisedAt = now,
            LastOccurredAt = reading.MeasuredAt,
        };
    }

    public static RpmAlert ForSilence(RpmEnrollment enrollment, RpmDevice device, TimeSpan silentFor, DateTimeOffset now) => new()
    {
        FacilityId = enrollment.FacilityId,
        EnrollmentId = enrollment.Id,
        PatientId = enrollment.PatientId,
        Kind = RpmAlertKind.DeviceSilence,
        DeviceId = device.Id,
        Severity = RpmAlertSeverity.Warning,
        Status = RpmAlertStatus.Open,
        Message = $"No readings from {device.Type} {device.SerialNumber} for {(int)silentFor.TotalHours} hours",
        RaisedAt = now,
        LastOccurredAt = now,
    };

    /// <summary>Counts a further breach of the same kind. Returns true when a warning escalated to critical (notify again).</summary>
    public bool Recur(RpmReading reading, DateTimeOffset now)
    {
        Occurrences++;
        if (reading.MeasuredAt > LastOccurredAt)
        {
            LastOccurredAt = reading.MeasuredAt;
        }

        if (reading.Severity != ReadingSeverity.Critical || Severity == RpmAlertSeverity.Critical)
        {
            return false;
        }

        Severity = RpmAlertSeverity.Critical;
        ReadingId = reading.Id;
        Value = reading.Value;
        Unit = reading.Unit;
        Message = Describe(reading, RpmAlertSeverity.Critical);
        Status = RpmAlertStatus.Open;
        AcknowledgedAt = null;
        AcknowledgedByUserId = null;
        RaisedAt = now;
        return true;
    }

    public Result Acknowledge(Guid? userId, string? notes, DateTimeOffset now)
    {
        if (Status == RpmAlertStatus.Resolved)
        {
            return Error.Rule("rpm.alert_resolved", "This alert is already resolved.");
        }

        if (Status == RpmAlertStatus.Open)
        {
            Status = RpmAlertStatus.Acknowledged;
            AcknowledgedByUserId = userId;
            AcknowledgedAt = now;
        }

        AddNote(notes);
        return Result.Success();
    }

    public Result Resolve(Guid? userId, string? notes, DateTimeOffset now)
    {
        if (Status == RpmAlertStatus.Resolved)
        {
            return Result.Success();
        }

        AcknowledgedByUserId ??= userId;
        AcknowledgedAt ??= now;
        Status = RpmAlertStatus.Resolved;
        ResolvedByUserId = userId;
        ResolvedAt = now;
        AddNote(notes);
        return Result.Success();
    }

    private void AddNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        var combined = string.IsNullOrEmpty(Notes) ? note.Trim() : $"{Notes}\n{note.Trim()}";
        Notes = combined.Length > MaxNotesLength ? combined[..MaxNotesLength] : combined;
    }

    private static string Describe(RpmReading reading, RpmAlertSeverity severity) =>
        $"{(severity == RpmAlertSeverity.Critical ? "Critical" : "Out of range")}: {ReadingUnits.Describe(reading.Type, reading.Value, reading.TextValue)}";
}
