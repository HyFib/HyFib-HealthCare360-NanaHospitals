namespace HyFib.HealthCare360.Modules.VirtualCare.Domain;

public enum TelehealthStatus
{
    Scheduled = 0,

    /// <summary>The patient is in the virtual waiting room; the doctor has not admitted them yet (TEL-03).</summary>
    WaitingRoom,

    InProgress,
    Completed,
    Cancelled,

    /// <summary>Nobody started the consultation within an hour of its scheduled end.</summary>
    Missed,
}

/// <summary>
/// One 1:1 video consultation (TEL-01..05). Media flows peer-to-peer over WebRTC and is never relayed through or
/// recorded by the server; the server keeps the metadata — waiting room, admission, start, end, duration,
/// participants and connection quality — and brokers the signalling. The patient's join link carries a random token
/// of which only a session-bound hash is stored; issuing a new link invalidates the previous one, and links stop
/// working two hours after the scheduled end or when the session closes.
/// </summary>
public sealed class TelehealthSession : AuditableEntity, IFacilityScoped
{
    public static readonly TimeSpan LinkGraceAfterEnd = TimeSpan.FromHours(2);
    public static readonly TimeSpan MissedAfter = TimeSpan.FromHours(1);
    public const int DefaultEarlyJoinMinutes = 30;
    public const int MaxNotesLength = 1000;

    public Guid FacilityId { get; set; }
    public Guid? AppointmentId { get; set; }
    public string? AppointmentNumber { get; set; }
    public Guid PatientId { get; set; }
    public Guid PractitionerId { get; set; }
    public Guid? DepartmentId { get; set; }

    /// <summary>The telehealth visit the EMR notes and e-prescriptions are written against (TEL-04).</summary>
    public Guid? VisitId { get; set; }

    public TelehealthStatus Status { get; set; }
    public DateTimeOffset ScheduledStartAt { get; set; }
    public DateTimeOffset ScheduledEndAt { get; set; }
    public DateTimeOffset? PatientWaitingSince { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? DurationSeconds { get; set; }

    [SensitiveData]
    public string? JoinTokenHash { get; set; }

    public DateTimeOffset? JoinTokenExpiresAt { get; set; }
    public DateTimeOffset? LinkSentAt { get; set; }
    public int LinkSendCount { get; set; }

    /// <summary>Consent to record media (TEL-05). HC360 itself never records; the flag authorises a local recording.</summary>
    public bool RecordingConsent { get; set; }

    public DateTimeOffset? RecordingConsentAt { get; set; }
    public Guid? RecordingConsentByUserId { get; set; }

    /// <summary>Either side fell back to audio only during the call (TEL-02).</summary>
    public bool AudioOnlyUsed { get; set; }

    public string? QualityNotes { get; set; }
    public string? CancelReason { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? MissedAt { get; set; }

    public bool IsClosed => Status is TelehealthStatus.Completed or TelehealthStatus.Cancelled or TelehealthStatus.Missed;

    public static TelehealthSession Schedule(
        Guid facilityId,
        Guid patientId,
        Guid practitionerId,
        Guid? departmentId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        Guid? appointmentId = null,
        string? appointmentNumber = null) => new()
    {
        FacilityId = facilityId,
        PatientId = patientId,
        PractitionerId = practitionerId,
        DepartmentId = departmentId,
        ScheduledStartAt = startsAt,
        ScheduledEndAt = endsAt > startsAt ? endsAt : startsAt.AddMinutes(15),
        AppointmentId = appointmentId,
        AppointmentNumber = appointmentNumber,
        Status = TelehealthStatus.Scheduled,
    };

    /// <summary>Issues a new join token (invalidating any earlier link). The token is returned once and never stored.</summary>
    public Result<string> IssueJoinToken()
    {
        if (IsClosed)
        {
            return Error.Rule("telehealth.closed", "This consultation is closed; a new link cannot be issued.");
        }

        var token = JoinTokens.NewToken();
        JoinTokenHash = JoinTokens.Hash(Id, token);
        JoinTokenExpiresAt = ScheduledEndAt + LinkGraceAfterEnd;
        return token;
    }

    public void MarkLinkSent(DateTimeOffset now)
    {
        LinkSentAt = now;
        LinkSendCount++;
    }

    public DateTimeOffset JoinOpensAt(TimeSpan earlyJoin) => ScheduledStartAt - earlyJoin;

    /// <summary>Checks a guest join token (constant time) and its expiry.</summary>
    public Result VerifyJoinToken(string? token, DateTimeOffset now)
    {
        if (!JoinTokens.Matches(Id, token, JoinTokenHash))
        {
            return Error.Forbidden("telehealth.invalid_link", "This consultation link is not valid. Please use the latest link sent to you.");
        }

        return JoinTokenExpiresAt is { } expires && now > expires
            ? Error.Forbidden("telehealth.link_expired", "This consultation link has expired.")
            : Result.Success();
    }

    /// <summary>Patients may join from <paramref name="earlyJoin"/> before the start until the session closes.</summary>
    public Result EnsureJoinable(DateTimeOffset now, TimeSpan earlyJoin)
    {
        if (IsClosed)
        {
            return Error.Rule("telehealth.closed", Status switch
            {
                TelehealthStatus.Completed => "This consultation has ended.",
                TelehealthStatus.Cancelled => "This consultation was cancelled.",
                _ => "This consultation was missed. Please book a new appointment.",
            });
        }

        return now < JoinOpensAt(earlyJoin)
            ? Error.Rule("telehealth.too_early", $"You can join from {(int)earlyJoin.TotalMinutes} minutes before the appointment time.")
            : Result.Success();
    }

    public void EnterWaitingRoom(DateTimeOffset now)
    {
        if (Status == TelehealthStatus.Scheduled)
        {
            Status = TelehealthStatus.WaitingRoom;
            PatientWaitingSince = now;
        }
    }

    public void LeaveWaitingRoom()
    {
        if (Status == TelehealthStatus.WaitingRoom)
        {
            Status = TelehealthStatus.Scheduled;
            PatientWaitingSince = null;
        }
    }

    public Result Start(DateTimeOffset now)
    {
        if (Status == TelehealthStatus.InProgress)
        {
            return Result.Success();
        }

        if (Status is not (TelehealthStatus.Scheduled or TelehealthStatus.WaitingRoom))
        {
            return Error.Rule("telehealth.closed", "This consultation is already closed.");
        }

        Status = TelehealthStatus.InProgress;
        StartedAt = now;
        return Result.Success();
    }

    public Result End(DateTimeOffset now, string? qualityNotes)
    {
        if (Status == TelehealthStatus.Completed)
        {
            return Result.Success();
        }

        if (Status != TelehealthStatus.InProgress)
        {
            return Error.Rule("telehealth.not_started", "Start the consultation before ending it.");
        }

        Status = TelehealthStatus.Completed;
        EndedAt = now;
        DurationSeconds = (int)Math.Max(0, (now - (StartedAt ?? now)).TotalSeconds);
        JoinTokenHash = null;
        AddQualityNote(qualityNotes);
        return Result.Success();
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status == TelehealthStatus.Cancelled)
        {
            return Result.Success();
        }

        if (Status is not (TelehealthStatus.Scheduled or TelehealthStatus.WaitingRoom))
        {
            return Error.Rule("telehealth.not_cancellable", "A consultation that has started or closed cannot be cancelled.");
        }

        Status = TelehealthStatus.Cancelled;
        CancelReason = reason.Length > 300 ? reason[..300] : reason;
        CancelledAt = now;
        JoinTokenHash = null;
        return Result.Success();
    }

    /// <summary>Marks a session nobody started as missed, one hour after its scheduled end.</summary>
    public bool MarkMissedIfOverdue(DateTimeOffset now)
    {
        if (Status is not (TelehealthStatus.Scheduled or TelehealthStatus.WaitingRoom) || now <= ScheduledEndAt + MissedAfter)
        {
            return false;
        }

        Status = TelehealthStatus.Missed;
        MissedAt = now;
        JoinTokenHash = null;
        return true;
    }

    public Result RecordConsent(bool granted, Guid? userId, DateTimeOffset now)
    {
        if (Status is TelehealthStatus.Cancelled or TelehealthStatus.Missed)
        {
            return Error.Rule("telehealth.closed", "This consultation did not take place.");
        }

        RecordingConsent = granted;
        RecordingConsentAt = now;
        RecordingConsentByUserId = userId;
        return Result.Success();
    }

    public void AttachVisit(Guid visitId) => VisitId ??= visitId;

    public void NoteAudioOnly() => AudioOnlyUsed = true;

    private void AddQualityNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        var combined = string.IsNullOrEmpty(QualityNotes) ? note.Trim() : $"{QualityNotes}\n{note.Trim()}";
        QualityNotes = combined.Length > MaxNotesLength ? combined[..MaxNotesLength] : combined;
    }
}
