namespace HyFib.HealthCare360.Modules.Patients.Domain;

public enum VisitType
{
    OutPatient = 0,
    InPatient,
    Emergency,
    DayCare,
    Telehealth,
    HealthCheckup,
}

public enum VisitStatus
{
    /// <summary>Checked in, waiting.</summary>
    Open = 0,
    InProgress,
    Completed,
    Cancelled,
}

public enum ReferralSource
{
    Self = 0,
    Doctor,
    Hospital,
    HealthCamp,
    Online,
    Corporate,
    Advertisement,
    Other,
}

/// <summary>
/// One encounter with the hospital (OP consultation, admission, ER, day care, telehealth, health checkup).
/// The anchor that clinical orders, charges and bills refer to.
/// </summary>
public sealed class Visit : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string VisitNumber { get; set; } = string.Empty;
    public Guid PatientId { get; set; }
    public VisitType Type { get; set; }
    public VisitStatus Status { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? PractitionerId { get; set; }
    public Guid? AppointmentId { get; set; }
    public DateTimeOffset VisitedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Follow-up of an earlier paid consultation with the same doctor within the validity window.</summary>
    public bool IsFollowUp { get; set; }

    public bool IsFreeFollowUp { get; set; }
    public Guid? PreviousVisitId { get; set; }
    public PayerType PayerType { get; set; }
    public Guid? PayerId { get; set; }
    public ReferralSource ReferralSource { get; set; }
    public string? ReferredBy { get; set; }
    public string? ChiefComplaint { get; set; }

    /// <summary>Medico-legal case (police intimation required).</summary>
    public bool IsMlc { get; set; }

    public string? MlcNumber { get; set; }

    /// <summary>
    /// Why the visit is treated free of cost by law, as its bills and pharmacy sales print it: the victim of a sexual
    /// offence or an acid attack (BNSS 2023, s.397), from the MLC register; <c>null</c> when it is billed as usual. It names
    /// the law and the MLC entry, never the offence.
    /// </summary>
    public string? FreeTreatmentReason { get; set; }

    public string? Notes { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>Notifies other modules (billing, scheduling, clinical) in the same transaction.</summary>
    public void MarkCreated() => Raise(new VisitCreated(Id, PatientId, FacilityId, Type, DepartmentId, PractitionerId, AppointmentId));

    /// <summary>The consultation (or the admission) has begun; Scheduling moves a linked appointment to "in consultation".</summary>
    public void Start()
    {
        if (Status == VisitStatus.Open)
        {
            Status = VisitStatus.InProgress;
            Raise(new VisitStarted(Id, PatientId, FacilityId, PractitionerId, AppointmentId));
        }
    }

    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status is VisitStatus.Completed or VisitStatus.Cancelled)
        {
            return Error.Rule("visit.closed", "This visit is already closed.");
        }

        Status = VisitStatus.Cancelled;
        CancelReason = reason;
        CompletedAt = now;
        Raise(new VisitCancelled(Id, PatientId, FacilityId));
        return Result.Success();
    }

    public Result Complete(DateTimeOffset now)
    {
        if (Status == VisitStatus.Cancelled)
        {
            return Error.Rule("visit.cancelled", "A cancelled visit cannot be completed.");
        }

        if (Status == VisitStatus.Completed)
        {
            return Result.Success();
        }

        Status = VisitStatus.Completed;
        CompletedAt = now;
        Raise(new VisitCompleted(Id, PatientId, FacilityId, PractitionerId));
        return Result.Success();
    }
}

public enum TokenStatus
{
    Waiting = 0,
    Called,
    InService,
    Completed,
    Skipped,
    NoShow,
    Cancelled,
}

/// <summary>Higher value is served first.</summary>
public enum TokenPriority
{
    Normal = 0,

    /// <summary>Senior citizens, pregnant women, differently-abled.</summary>
    Priority = 1,

    Emergency = 2,
}

/// <summary>Daily department queue token ("GM-012") shown on display boards and called by the doctor.</summary>
public sealed class QueueToken : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid VisitId { get; set; }
    public Guid PatientId { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid? PractitionerId { get; set; }
    public DateOnly TokenDate { get; set; }
    public int Sequence { get; set; }
    public string Label { get; set; } = string.Empty;
    public TokenStatus Status { get; set; }
    public TokenPriority Priority { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? CalledAt { get; set; }
    public DateTimeOffset? ServiceStartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Consultation room / counter announced on the display.</summary>
    public string? Room { get; set; }

    public int CallCount { get; set; }
    public uint Version { get; set; }

    public Result Call(string? room, DateTimeOffset now)
    {
        if (Status is TokenStatus.Completed or TokenStatus.Cancelled)
        {
            return Error.Rule("token.closed", "This token is already closed.");
        }

        Status = TokenStatus.Called;
        Room = string.IsNullOrWhiteSpace(room) ? Room : room.Trim();
        CalledAt = now;
        CallCount++;
        return Result.Success();
    }

    public Result Start(DateTimeOffset now)
    {
        if (Status is TokenStatus.Completed or TokenStatus.Cancelled)
        {
            return Error.Rule("token.closed", "This token is already closed.");
        }

        Status = TokenStatus.InService;
        ServiceStartedAt ??= now;
        CalledAt ??= now;
        return Result.Success();
    }

    public Result Finish(DateTimeOffset now)
    {
        if (Status == TokenStatus.Cancelled)
        {
            return Error.Rule("token.cancelled", "This token was cancelled.");
        }

        Status = TokenStatus.Completed;
        CompletedAt = now;
        return Result.Success();
    }

    public Result Skip()
    {
        if (Status is TokenStatus.Completed or TokenStatus.Cancelled)
        {
            return Error.Rule("token.closed", "This token is already closed.");
        }

        Status = TokenStatus.Skipped;
        return Result.Success();
    }

    public Result Requeue()
    {
        if (Status is not (TokenStatus.Skipped or TokenStatus.Called or TokenStatus.NoShow))
        {
            return Error.Rule("token.not_requeueable", "Only called, skipped or no-show tokens can be put back in the queue.");
        }

        Status = TokenStatus.Waiting;
        return Result.Success();
    }
}

public sealed record VisitCreated(Guid VisitId, Guid PatientId, Guid FacilityId, VisitType Type, Guid? DepartmentId, Guid? PractitionerId, Guid? AppointmentId) : DomainEvent;

public sealed record VisitStarted(Guid VisitId, Guid PatientId, Guid FacilityId, Guid? PractitionerId, Guid? AppointmentId) : DomainEvent;

public sealed record VisitCompleted(Guid VisitId, Guid PatientId, Guid FacilityId, Guid? PractitionerId) : DomainEvent;

public sealed record VisitCancelled(Guid VisitId, Guid PatientId, Guid FacilityId) : DomainEvent;
